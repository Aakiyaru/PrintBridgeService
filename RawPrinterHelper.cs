using System.ComponentModel;
using System.Runtime.InteropServices;
using System;                          // Array.Empty<string>()
using System.Collections.Generic;      // IEnumerable<T>, IReadOnlyList<T>, List<T>
using System.Linq;
using System.IO;                     // FirstOrDefault

namespace ZplPrintBridge;

public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 di);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBuf, int bufLen, out int written);

    public static void SendBytesToPrinter(string printerName, byte[] data, string docName = "ZPL Job")
    {
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter('{printerName}') failed");

        try
        {
            var di = new DOC_INFO_1
            {
                pDocName = docName,
                pOutputFile = null,
                pDatatype = "RAW"
            };

            if (!StartDocPrinter(hPrinter, 1, ref di))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed");

            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed");

                IntPtr pUnmanaged = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, pUnmanaged, data.Length);
                    if (!WritePrinter(hPrinter, pUnmanaged, data.Length, out int written))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed");

                    if (written != data.Length)
                        throw new IOException($"WritePrinter wrote {written}/{data.Length} bytes");
                }
                finally
                {
                    Marshal.FreeHGlobal(pUnmanaged);
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}