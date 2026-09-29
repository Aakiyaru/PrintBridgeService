using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZplPrintBridge
{
    public class ZplPrintBridgeWorker : BackgroundService
    {
        private readonly ILogger<ZplPrintBridgeWorker> _logger;
        private readonly PrintBridgeOptions _options;
        private TcpListener _listener;
        private string _resolvedPrinter;

        public ZplPrintBridgeWorker(
            ILogger<ZplPrintBridgeWorker> logger,
            IOptions<PrintBridgeOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LogAvailablePrinters();

            _resolvedPrinter = ResolvePrinter();
            if (_resolvedPrinter == null)
            {
                _logger.LogWarning(
                    "Printer not found by patterns [{Patterns}]. Will retry on first print.",
                    string.Join(", ", _options.PrinterNamePatterns));
            }

            var ip = IPAddress.Parse(_options.ListenAddress);
            _listener = new TcpListener(ip, _options.Port);

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Cannot start TcpListener on {Ip}:{Port}", ip, _options.Port);
                throw;
            }

            // .NET Framework 4.8: AcceptTcpClientAsync() без токена.
            // Отмену реализуем через остановку листенера.
            using (stoppingToken.Register(() => { try { _listener.Stop(); } catch { } }))
            {
                _logger.LogInformation(
                    "ZPL Print Bridge started. Listening {Ip}:{Port}. Printer: {Printer}",
                    ip, _options.Port, _resolvedPrinter ?? "<not found>");

                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        TcpClient client;
                        try
                        {
                            client = await _listener.AcceptTcpClientAsync();
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (SocketException) when (stoppingToken.IsCancellationRequested) { break; }

                        _ = Task.Run(() => HandleClientAsync(client, stoppingToken));
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    try { _listener.Stop(); } catch { }
                    _logger.LogInformation("ZPL Print Bridge stopped.");
                }
            }
        }

        private void LogAvailablePrinters()
        {
            try
            {
                var printers = PrinterResolver.GetInstalledPrinters();
                if (printers.Count == 0)
                {
                    _logger.LogWarning("No printers visible in the service context.");
                    return;
                }
                _logger.LogInformation("Available printers ({Count}):", printers.Count);
                foreach (var p in printers)
                    _logger.LogInformation("  - {Printer}", p);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate printers");
            }
        }

        private string ResolvePrinter()
        {
            if (!string.IsNullOrWhiteSpace(_options.PrinterName))
                return _options.PrinterName;

            return PrinterResolver.FindByPatterns(_options.PrinterNamePatterns);
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var remote = client.Client.RemoteEndPoint != null
                ? client.Client.RemoteEndPoint.ToString()
                : "unknown";

            // .NET Framework 4.8: ReadAsync/WriteAsync без CancellationToken.
            // Отмену реализуем через закрытие клиента.
            using (ct.Register(() => { try { client.Close(); } catch { } }))
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var buffer = new byte[8192];
                        using (var ms = new MemoryStream())
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                ms.Write(buffer, 0, read);

                            var zplData = ms.ToArray();
                            if (zplData.Length == 0)
                            {
                                _logger.LogWarning("Empty request from {Remote}", remote);
                                return;
                            }

                            var printerName = await PrintWithRetryAsync(zplData, ct);

                            _logger.LogInformation(
                                "Sent {Bytes} bytes to '{Printer}' (client {Remote})",
                                zplData.Length, printerName, remote);

                            if (_options.SendAck)
                            {
                                var ok = Encoding.ASCII.GetBytes("OK\n");
                                await stream.WriteAsync(ok, 0, ok.Length);
                                await stream.FlushAsync();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling client {Remote}", remote);
                }
            }
        }

        private async Task<string> PrintWithRetryAsync(byte[] zplData, CancellationToken ct)
        {
            if (_options.ResolveOnEveryPrint)
                _resolvedPrinter = ResolvePrinter();

            var printer = _resolvedPrinter ?? ResolvePrinter();

            if (printer == null)
                throw new InvalidOperationException(
                    "No printer found by patterns: " + string.Join(", ", _options.PrinterNamePatterns));

            try
            {
                await Task.Run(() => RawPrinterHelper.SendBytesToPrinter(printer, zplData), ct);
                _resolvedPrinter = printer;
                return printer;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1801)
            {
                _logger.LogWarning(
                    "Printer '{Printer}' no longer found (1801). Re-resolving by patterns.",
                    printer);

                var retry = ResolvePrinter();
                if (retry == null || retry == printer)
                    throw;

                _logger.LogInformation("Found new printer: '{Printer}'. Retrying print.", retry);
                await Task.Run(() => RawPrinterHelper.SendBytesToPrinter(retry, zplData), ct);
                _resolvedPrinter = retry;
                return retry;
            }
        }
    }
}