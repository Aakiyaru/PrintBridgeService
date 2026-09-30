using System;
using System.ServiceProcess;

namespace ZplPrintBridge
{
    internal static class Program
    {
        // EN: Application entry point. Detects whether we were started by SCM
        //     (as a Windows service) or from a console (for debugging).
        // RU: Точка входа приложения. Определяет, запущены ли мы SCM
        //     (как служба Windows) или из консоли (для отладки).
        private static void Main(string[] args)
        {
            var service = new ZplPrintBridgeService();

            // EN: Environment.UserInteractive is true when the process is started
            //     by a logged-in user (console / double-click), and false when
            //     started by the Service Control Manager.
            // RU: Environment.UserInteractive == true, когда процесс запущен
            //     интерактивным пользователем (консоль / двойной клик), и false,
            //     когда запущен диспетчером служб (SCM).
            if (Environment.UserInteractive)
            {
                // EN: Console mode - useful for debugging without installing the service.
                // RU: Консольный режим - удобно отлаживать без установки службы.
                Console.WriteLine("Running in console mode. Press Enter to stop.");
                service.StartInConsole();
                Console.ReadLine();
                service.StopInConsole();
            }
            else
            {
                // EN: Service mode - SCM will call OnStart/OnStop on the instance.
                // RU: Режим службы - SCM сам вызовет OnStart/OnStop у экземпляра.
                ServiceBase.Run(service);
            }
        }
    }
}