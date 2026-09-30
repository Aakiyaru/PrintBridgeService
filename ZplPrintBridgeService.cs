using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;

namespace ZplPrintBridge
{
    // EN: Windows service that listens on a raw TCP port (default 9100) and
    //     forwards every received byte stream directly to a local printer
    //     selected by name pattern (Zebra, BSmart, etc.).
    // RU: Служба Windows, слушающая raw TCP-порт (по умолчанию 9100) и
    //     перенаправляющая всё принятое напрямую в локальный принтер,
    //     выбранный по маске имени (Zebra, BSmart и т.п.).
    public class ZplPrintBridgeService : ServiceBase
    {
        // EN: TCP listener for incoming raw print jobs.
        // RU: TCP-листенер для входящих raw-заданий.
        private TcpListener _listener;

        // EN: Background thread running the accept loop.
        // RU: Фоновый поток, выполняющий цикл accept.
        private Thread _listenerThread;

        // EN: Flag to signal the listener thread to stop.
        //     volatile ensures visibility across threads.
        // RU: Флаг остановки листенера.
        //     volatile гарантирует видимость изменений между потоками.
        private volatile bool _running;

        // EN: Synchronises writes to the log file from multiple threads.
        // RU: Синхронизирует запись в лог из нескольких потоков.
        private readonly object _logLock = new object();

        // EN: Configuration values loaded from App.config.
        // RU: Значения конфигурации, прочитанные из App.config.
        private string[] _printerPatterns;   // EN: name masks / RU: маски имён
        private string _printerName;         // EN: exact name (optional) / RU: точное имя (опционально)
        private int _port;                   // EN: listen port / RU: порт прослушивания
        private string _listenAddress;       // EN: bind address / RU: адрес привязки
        private bool _sendAck;               // EN: reply "OK" / RU: отвечать "OK"
        private bool _resolveOnEveryPrint;   // EN: re-resolve on each job / RU: переискивать при каждой печати
        private string _logFile;             // EN: log path / RU: путь к логу

        // EN: Cached printer name resolved by patterns. May become stale if the
        //     printer is renamed/reinstalled - then we re-resolve automatically.
        // RU: Кэш имени принтера, найденного по маске. Может устареть, если принтер
        //     переименовали/переустановили - тогда мы автоматически переискиваем.
        private string _resolvedPrinter;

        public ZplPrintBridgeService()
        {
            // EN: Service name as registered in SCM (must match sc.exe create).
            // RU: Имя службы, как оно зарегистрировано в SCM (должно совпадать с sc.exe create).
            ServiceName = "ZplPrintBridge";

            // EN: Allow SCM to stop the service and to stop it on system shutdown.
            // RU: Разрешаем SCM останавливать службу и останавливать её при выключении системы.
            CanStop = true;
            CanShutdown = true;

            // EN: Let ServiceBase write start/stop events to the Application event log.
            // RU: Пусть ServiceBase пишет события старта/остановки в журнал "Приложение".
            AutoLog = true;
        }

        // EN: Called by SCM when the service starts.
        // RU: Вызывается SCM при запуске службы.
        protected override void OnStart(string[] args)
        {
            LoadConfig();
            StartInConsole();
        }

        // EN: Called by SCM when the service is stopped.
        // RU: Вызывается SCM при остановке службы.
        protected override void OnStop()
        {
            StopInConsole();
        }

        // EN: Called by SCM on OS shutdown - clean up the same way as OnStop.
        // RU: Вызывается SCM при выключении ОС - убираемся так же, как в OnStop.
        protected override void OnShutdown()
        {
            StopInConsole();
            base.OnShutdown();
        }

        // EN: Shared start routine used both by OnStart (service mode) and Main (console mode).
        // RU: Общая процедура запуска, используемая и OnStart (служба), и Main (консоль).
        public void StartInConsole()
        {
            // EN: Config may not be loaded yet when running in console mode.
            // RU: В консольном режиме конфиг мог ещё не загрузиться.
            if (_printerPatterns == null) LoadConfig();

            Log("=== ZPL Print Bridge starting ===");

            // EN: Diagnostic: list all printers visible in the current service context.
            //     If this list is empty under LocalSystem, the session-0 isolation is at play.
            // RU: Диагностика: перечислим все принтеры, видимые в текущем контексте службы.
            //     Если список пуст под LocalSystem - виновата изоляция сеанса 0.
            try
            {
                var printers = PrinterResolver.GetInstalledPrinters();
                Log("Available printers (" + printers.Count + "):");
                foreach (var p in printers) Log("  - " + p);
            }
            catch (Exception ex)
            {
                Log("Failed to enumerate printers: " + ex.Message);
            }

            // EN: Try to resolve the target printer at startup.
            // RU: Пытаемся найти целевой принтер сразу на старте.
            _resolvedPrinter = ResolvePrinter();
            Log(_resolvedPrinter != null
                ? "Resolved printer: " + _resolvedPrinter
                : "Printer not found by patterns. Will retry on first print.");

            // EN: Parse bind address and set up the TCP listener.
            // RU: Парсим адрес привязки и настраиваем TCP-листенер.
            var ip = IPAddress.Parse(_listenAddress);
            _listener = new TcpListener(ip, _port);

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                // EN: Port busy or insufficient privileges - log and rethrow so SCM knows
                //     the service failed to start.
                // RU: Порт занят или не хватает прав - логируем и пробрасываем дальше,
                //     чтобы SCM увидел, что служба не стартовала.
                Log("FATAL: cannot start listener on " + ip + ":" + _port + " - " + ex.Message);
                throw;
            }

            // EN: Start the accept loop in a background thread.
            // RU: Запускаем цикл accept в фоновом потоке.
            _running = true;
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "TcpListener"
            };
            _listenerThread.Start();

            Log("Listening on " + ip + ":" + _port);
        }

        // EN: Shared stop routine used by OnStop/OnShutdown (service mode) and Main (console mode).
        // RU: Общая процедура остановки, используемая OnStop/OnShutdown (служба) и Main (консоль).
        public void StopInConsole()
        {
            if (!_running) return;

            _running = false;

            // EN: Stop() causes AcceptTcpClient to throw SocketException/ObjectDisposedException,
            //     which breaks the loop in ListenLoop.
            // RU: Stop() заставляет AcceptTcpClient выбросить SocketException/ObjectDisposedException,
            //     что прерывает цикл в ListenLoop.
            try { _listener?.Stop(); } catch { }

            // EN: Wait for the listener thread to finish, but not indefinitely.
            // RU: Дожидаемся завершения потока-листенера, но не бесконечно.
            try { _listenerThread?.Join(5000); } catch { }

            Log("=== ZPL Print Bridge stopped ===");
        }

        // EN: Accept loop. Runs on a dedicated background thread.
        // RU: Цикл accept. Работает в отдельном фоновом потоке.
        private void ListenLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (!_running)
                {
                    // EN: Expected when Stop() is called.
                    // RU: Ожидаемое поведение при вызове Stop().
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // EN: Listener was disposed - exit loop.
                    // RU: Листенер уничтожен - выходим из цикла.
                    break;
                }
                catch (Exception ex)
                {
                    // EN: Unexpected error - log and continue accepting.
                    // RU: Неожиданная ошибка - логируем и продолжаем принимать.
                    Log("Accept error: " + ex.Message);
                    continue;
                }

                // EN: Handle each client on its own thread so accept loop is not blocked.
                // RU: Обрабатываем каждого клиента в отдельном потоке, чтобы не блокировать accept.
                var t = new Thread(() => HandleClient(client))
                {
                    IsBackground = true
                };
                t.Start();
            }
        }

        // EN: Reads the entire raw stream from the client and sends it to the printer.
        // RU: Читает весь raw-поток от клиента и отправляет его на принтер.
        private void HandleClient(TcpClient client)
        {
            // EN: Remote endpoint for logging (may be null in rare cases).
            // RU: Удалённая точка для логов (в редких случаях может быть null).
            var remote = client.Client != null && client.Client.RemoteEndPoint != null
                ? client.Client.RemoteEndPoint.ToString()
                : "unknown";

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    // EN: Read until the client closes the connection (EOF).
                    // RU: Читаем, пока клиент не закроет соединение (EOF).
                    var buffer = new byte[8192];
                    using (var ms = new MemoryStream())
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            ms.Write(buffer, 0, read);

                        var zplData = ms.ToArray();
                        if (zplData.Length == 0)
                        {
                            // EN: Client connected but sent nothing - nothing to print.
                            // RU: Клиент подключился, но ничего не прислал - печатать нечего.
                            Log("Empty request from " + remote);
                            return;
                        }

                        var printerName = PrintWithRetry(zplData);
                        Log("Sent " + zplData.Length + " bytes to '" + printerName + "' (client " + remote + ")");

                        // EN: Optional "OK\n" acknowledgement so the client knows the job was accepted.
                        // RU: Необязательный ответ "OK\n", чтобы клиент знал, что задание принято.
                        if (_sendAck)
                        {
                            var ok = System.Text.Encoding.ASCII.GetBytes("OK\n");
                            stream.Write(ok, 0, ok.Length);
                            stream.Flush();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // EN: Any exception here kills only this client's thread, not the service.
                // RU: Любое исключение здесь убивает только поток этого клиента, а не службу.
                Log("Error handling client " + remote + ": " + ex.Message);
            }
        }

        // EN: Prints data to the resolved printer. If the printer is no longer valid
        //     (Win32 error 1801 = ERROR_INVALID_PRINTER_NAME), re-resolves by pattern
        //     and retries once. This survives printer renames/reinstalls.
        // RU: Печатает данные на найденный принтер. Если принтер больше не валиден
        //     (Win32-ошибка 1801 = ERROR_INVALID_PRINTER_NAME), переискивает по маске
        //     и пробует ещё раз. Это переживает переименование/переустановку принтера.
        private string PrintWithRetry(byte[] zplData)
        {
            // EN: Optionally re-resolve before every job (slower but always fresh).
            // RU: Опционально переискиваем перед каждой печатью (медленнее, но всегда актуально).
            if (_resolveOnEveryPrint) _resolvedPrinter = ResolvePrinter();

            var printer = _resolvedPrinter ?? ResolvePrinter();
            if (printer == null)
                throw new InvalidOperationException(
                    "No printer found by patterns: " + string.Join(", ", _printerPatterns));

            try
            {
                RawPrinterHelper.SendBytesToPrinter(printer, zplData);
                _resolvedPrinter = printer;
                return printer;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1801)
            {
                Log("Printer '" + printer + "' not found (1801). Re-resolving by patterns.");
                var retry = ResolvePrinter();
                if (retry == null || retry == printer) throw; // EN: nothing new to try / RU: нечего пробовать

                Log("Found new printer: '" + retry + "'. Retrying print.");
                RawPrinterHelper.SendBytesToPrinter(retry, zplData);
                _resolvedPrinter = retry;
                return retry;
            }
        }

        // EN: Returns the exact printer name if configured, otherwise searches by masks.
        // RU: Возвращает точное имя принтера, если оно задано, иначе ищет по маскам.
        private string ResolvePrinter()
        {
            if (!string.IsNullOrWhiteSpace(_printerName)) return _printerName;
            return PrinterResolver.FindByPatterns(_printerPatterns);
        }

        // EN: Reads all settings from App.config appSettings section.
        // RU: Читает все настройки из секции appSettings файла App.config.
        private void LoadConfig()
        {
            var s = ConfigurationManager.AppSettings;

            // EN: Printer name masks, comma-separated. First match wins.
            // RU: Маски имён принтеров через запятую. Побеждает первое совпадение.
            var patternsRaw = s["PrinterNamePatterns"];
            if (string.IsNullOrWhiteSpace(patternsRaw)) patternsRaw = "*Zebra*,*BSmart*,*4BARCODE*";
            _printerPatterns = patternsRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < _printerPatterns.Length; i++)
                _printerPatterns[i] = _printerPatterns[i].Trim();

            // EN: Optional exact printer name (overrides patterns).
            // RU: Опциональное точное имя принтера (перекрывает маски).
            _printerName = s["PrinterName"];

            // EN: Listen port; default 9100.
            // RU: Порт прослушивания; по умолчанию 9100.
            int port;
            _port = int.TryParse(s["Port"], out port) ? port : 9100;

            // EN: Bind address; default all interfaces.
            // RU: Адрес привязки; по умолчанию все интерфейсы.
            _listenAddress = string.IsNullOrWhiteSpace(s["ListenAddress"]) ? "0.0.0.0" : s["ListenAddress"];

            // EN: Whether to send "OK" after each job.
            // RU: Отправлять ли "OK" после каждого задания.
            bool sendAck;
            _sendAck = !bool.TryParse(s["SendAck"], out sendAck) || sendAck;

            // EN: Whether to re-resolve printer before every print.
            // RU: Переискивать ли принтер перед каждой печатью.
            bool resolveEvery;
            _resolveOnEveryPrint = bool.TryParse(s["ResolveOnEveryPrint"], out resolveEvery) && resolveEvery;

            // EN: Log file path. Relative paths are resolved against the exe folder.
            // RU: Путь к логу. Относительные пути считаются от папки с exe.
            var logPath = s["LogFile"];
            if (string.IsNullOrWhiteSpace(logPath)) logPath = "ZplPrintBridge.log";
            if (!Path.IsPathRooted(logPath))
                logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logPath);
            _logFile = logPath;
        }

        // EN: Writes a timestamped line to console (if interactive) and to the log file.
        //     Never throws - logging must not bring the service down.
        // RU: Пишет строку с отметкой времени в консоль (если интерактивно) и в лог-файл.
        //     Никогда не выбрасывает исключений - логирование не должно ронять службу.
        private void Log(string message)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;

            // EN: In console mode print to the terminal as well.
            // RU: В консольном режиме дублируем вывод в терминал.
            try { Console.WriteLine(line); } catch { }

            try
            {
                lock (_logLock)
                {
                    var dir = Path.GetDirectoryName(_logFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(_logFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // EN: Swallow any log errors silently.
                // RU: Проглатываем любые ошибки записи в лог.
            }
        }
    }
}