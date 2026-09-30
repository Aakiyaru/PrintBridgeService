using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ZplPrintBridge
{
    // EN: Sends raw bytes (ZPL, ESC/POS, etc.) directly to a Windows printer using
    //     the spooler API. Works for local and network printers installed on the machine.
    // RU: Отправляет сырые байты (ZPL, ESC/POS и т.п.) напрямую в принтер Windows,
    //     используя API спулера. Работает для локальных и сетевых принтеров,
    //     установленных на машине.
    public static class RawPrinterHelper
    {
        // EN: DOC_INFO_1 structure for StartDocPrinter. CharSet=Unicode + LPWStr
        //     means we call the W (wide) variants of the API.
        // RU: Структура DOC_INFO_1 для StartDocPrinter. CharSet=Unicode + LPWStr
        //     означает, что мы вызываем W-варианты (wide) функций API.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;    // EN: document name / RU: имя документа
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile; // EN: unused (null) / RU: не используется (null)
            [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;   // EN: "RAW" / RU: "RAW"
        }

        // EN: Opens the printer and returns a handle. Fails with Win32 error 1801
        //     if the printer name is not found.
        // RU: Открывает принтер и возвращает хэндл. Падает с Win32-ошибкой 1801,
        //     если имя принтера не найдено.
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

        // EN: Writes the raw buffer to the printer. pBuf must be unmanaged memory.
        // RU: Записывает сырой буфер в принтер. pBuf должен быть в неуправляемой памяти.
        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBuf, int bufLen, out int written);

        // EN: Public entry point - sends the given byte array to the named printer
        //     as a RAW document. Throws Win32Exception on any API failure.
        // RU: Публичная точка входа - отправляет переданный массив байт в принтер
        //     с указанным именем как RAW-документ. Бросает Win32Exception при ошибке.
        public static void SendBytesToPrinter(string printerName, byte[] data, string docName = "ZPL Job")
        {
            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "OpenPrinter('" + printerName + "') failed");

            try
            {
                // EN: Document info - datatype RAW tells the spooler not to interpret bytes.
                // RU: Информация о документе - тип RAW говорит спулеру не интерпретировать байты.
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

                    // EN: Allocate unmanaged memory, copy managed bytes into it.
                    // RU: Выделяем неуправляемую память, копируем туда управляемый массив.
                    IntPtr pUnmanaged = Marshal.AllocHGlobal(data.Length);
                    try
                    {
                        Marshal.Copy(data, 0, pUnmanaged, data.Length);
                        int written;
                        if (!WritePrinter(hPrinter, pUnmanaged, data.Length, out written))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed");

                        // EN: Verify all bytes were accepted by the spooler.
                        // RU: Проверяем, что спулер принял все байты.
                        if (written != data.Length)
                            throw new IOException("WritePrinter wrote " + written + "/" + data.Length + " bytes");
                    }
                    finally
                    {
                        // EN: Always free the unmanaged buffer.
                        // RU: Всегда освобождаем неуправляемый буфер.
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
                // EN: Always close the printer handle.
                // RU: Всегда закрываем хэндл принтера.
                ClosePrinter(hPrinter);
            }
        }
    }
}