using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ZplPrintBridge
{
    // EN: Enumerates installed printers via winspool.drv EnumPrinters and finds
    //     the first one whose name matches one of the configured masks.
    //     Uses the same API family as OpenPrinter, so the list matches exactly.
    // RU: Перечисляет установленные принтеры через EnumPrinters из winspool.drv
    //     и возвращает первый, чьё имя подпадает под одну из масок.
    //     Использует то же семейство API, что и OpenPrinter, поэтому список точный.
    public static class PrinterResolver
    {
        // EN: PRINTER_INFO_4 structure (Level 4 enumeration).
        //     Level 4 returns only name and server - enough for our needs and fast.
        // RU: Структура PRINTER_INFO_4 (перечисление Level 4).
        //     Level 4 возвращает только имя и сервер - этого достаточно и это быстро.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PRINTER_INFO_4
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pServerName;
            public uint Attributes;
        }

        [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool EnumPrinters(
            uint Flags, string Name, uint Level,
            IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

        // EN: PRINTER_ENUM_LOCAL - printers defined on this machine.
        //     PRINTER_ENUM_CONNECTIONS - printers connected to this user.
        // RU: PRINTER_ENUM_LOCAL - принтеры, определённые на этой машине.
        //     PRINTER_ENUM_CONNECTIONS - принтеры, подключённые к этому пользователю.
        private const uint PRINTER_ENUM_LOCAL = 0x00000002;
        private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

        // EN: Returns all printers visible in the current process context.
        //     If the service runs under LocalSystem and cannot see user printers,
        //     the returned list may be empty - this is the session-0 isolation effect.
        // RU: Возвращает все принтеры, видимые в текущем контексте процесса.
        //     Если служба работает под LocalSystem и не видит пользовательские
        //     принтеры, список может быть пустым - это эффект изоляции сеанса 0.
        public static IReadOnlyList<string> GetInstalledPrinters()
        {
            var flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
            const uint level = 4;

            // EN: First call with zero buffer to learn the required size.
            // RU: Первый вызов с нулевым буфером - узнаём требуемый размер.
            uint needed;
            EnumPrinters(flags, null, level, IntPtr.Zero, 0, out needed, out _);
            if (needed == 0) return new List<string>();

            // EN: Allocate unmanaged buffer of the required size.
            // RU: Выделяем неуправляемый буфер нужного размера.
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                uint returned;
                if (!EnumPrinters(flags, null, level, buffer, needed, out _, out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumPrinters failed");

                var result = new List<string>((int)returned);
                int structSize = Marshal.SizeOf(typeof(PRINTER_INFO_4));

                // EN: The returned data is an array of PRINTER_INFO_4 structures.
                //     Walk it entry by entry.
                // RU: Возвращённые данные - массив структур PRINTER_INFO_4.
                //     Идём по нему по одной записи.
                for (int i = 0; i < returned; i++)
                {
                    IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                    var info = (PRINTER_INFO_4)Marshal.PtrToStructure(ptr, typeof(PRINTER_INFO_4));
                    if (!string.IsNullOrEmpty(info.pPrinterName))
                        result.Add(info.pPrinterName);
                }

                return result;
            }
            finally
            {
                // EN: Always free the unmanaged buffer.
                // RU: Всегда освобождаем неуправляемый буфер.
                Marshal.FreeHGlobal(buffer);
            }
        }

        // EN: Finds the first printer whose name matches one of the patterns.
        //     Pattern syntax: * = any sequence, ? = any single character.
        //     Matching is case-insensitive.
        // RU: Находит первый принтер, чьё имя подпадает под одну из масок.
        //     Синтаксис масок: * = любая последовательность, ? = любой один символ.
        //     Сравнение без учёта регистра.
        public static string FindByPatterns(string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return null;

            var printers = GetInstalledPrinters();

            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                // EN: Translate wildcard syntax to regex: escape everything,
                //     then turn \* and \? back into .* and .
                // RU: Переводим синтаксис wildcard в regex: экранируем всё,
                //     затем превращаем \* и \? обратно в .* и .
                var regexPattern = "^" + Regex.Escape(pattern)
                    .Replace(@"\*", ".*")
                    .Replace(@"\?", ".") + "$";

                var rx = new Regex(regexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                foreach (var p in printers)
                {
                    if (rx.IsMatch(p)) return p;
                }
            }

            return null;
        }
    }
}