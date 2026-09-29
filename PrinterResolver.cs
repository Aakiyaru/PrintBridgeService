using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System;                          // Array.Empty<string>()
using System.Collections.Generic;      // IEnumerable<T>, IReadOnlyList<T>, List<T>
using System.Linq;                     // FirstOrDefault

namespace ZplPrintBridge;

public static class PrinterResolver
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_4
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(
        uint Flags, string? Name, uint Level,
        IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    private const uint PRINTER_ENUM_LOCAL = 0x00000002;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    /// <summary>
    /// Возвращает список всех принтеров, видимых в текущем контексте
    /// (это тот же список, который использует OpenPrinter).
    /// </summary>
    public static IReadOnlyList<string> GetInstalledPrinters()
    {
        var flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        const uint level = 4;

        EnumPrinters(flags, null, level, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
            return Array.Empty<string>();

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, level, buffer, needed, out _, out uint returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumPrinters failed");

            var result = new List<string>((int)returned);
            int structSize = Marshal.SizeOf<PRINTER_INFO_4>();
            for (int i = 0; i < returned; i++)
            {
                IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(ptr);
                if (!string.IsNullOrEmpty(info.pPrinterName))
                    result.Add(info.pPrinterName);
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Находит первый принтер, чьё имя совпадает с одной из масок.
    /// Маски: * и ? — как в файловой системе, регистр не важен.
    /// </summary>
    public static string? FindByPatterns(IEnumerable<string> patterns)
    {
        var printers = GetInstalledPrinters();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            // Экранируем regex-спецсимволы, оставляя * и ? как wildcard
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            var rx = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var match = printers.FirstOrDefault(p => rx.IsMatch(p));
            if (match != null)
                return match;
        }
        return null;
    }
}