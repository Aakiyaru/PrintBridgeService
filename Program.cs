using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.DependencyInjection;
using ZplPrintBridge;

var builder = Host.CreateApplicationBuilder(args);

// Чтобы работало и как служба Windows, и как консольное приложение
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ZplPrintBridge";
});

builder.Services.Configure<PrintBridgeOptions>(
    builder.Configuration.GetSection("PrintBridge"));

builder.Services.AddHostedService<ZplPrintBridgeWorker>();

var host = builder.Build();
await host.RunAsync();