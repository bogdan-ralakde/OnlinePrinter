using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LabelBridge.Core;

/// <summary>Delivers raw printer bytes either to a socket or to a Windows queue.</summary>
public static class PrinterOutput
{
    public static async Task SendTcpAsync(string host, int port, byte[] data, CancellationToken ct, int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("Target printer address is not configured.");

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            using var ns = client.GetStream();
            await ns.WriteAsync(data, timeout.Token);
            await ns.FlushAsync(timeout.Token);

            // Send FIN so the printer knows the job is complete, then wait
            // briefly for the printer to close its side.
            client.Client.Shutdown(SocketShutdown.Send);
            var drain = new byte[256];
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (await ns.ReadAsync(drain, closeTimeout.Token) > 0) { }
            }
            catch { /* printer may just close - that is fine */ }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Printer {host}:{port} did not accept the job within {timeoutMs / 1000}s.");
        }
    }

    /// <summary>Re-spools raw bytes to an installed Windows printer (RAW datatype).</summary>
    public static void SendToWindowsQueue(string printerName, byte[] data, int timeoutMs = 30000)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Windows queue name is not configured.");

        var task = Task.Run(() => SendRaw(printerName, data));
        if (!task.Wait(timeoutMs))
            throw new TimeoutException($"Windows queue '{printerName}' did not accept the job within {timeoutMs / 1000}s.");
        task.GetAwaiter().GetResult();
    }

    private static void SendRaw(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open printer '{printerName}'.");

        try
        {
            var doc = new DOC_INFO_1
            {
                pDocName = "LabelBridge job",
                pDatatype = "RAW",
                pOutputFile = null
            };

            if (StartDocPrinter(handle, 1, doc) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed.");

            try
            {
                if (!StartPagePrinter(handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed.");

                try
                {
                    var ptr = Marshal.AllocCoTaskMem(data.Length);
                    try
                    {
                        Marshal.Copy(data, 0, ptr, data.Length);
                        if (!WritePrinter(handle, ptr, data.Length, out int written) || written != data.Length)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed.");
                    }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
                finally { EndPagePrinter(handle); }
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOC_INFO_1
    {
        public string pDocName = "";
        public string? pDatatype;
        public string? pOutputFile;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}
