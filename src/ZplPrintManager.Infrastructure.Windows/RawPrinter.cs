using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure.Windows;

public sealed class RawPrinter(string helperExecutable) : IPrinterTransport
{
    public async Task TestAsync(PrinterSettings printer, CancellationToken token) => await RunAsync(new PrintJob { Destination = printer }, true, _ => Task.CompletedTask, token);
    public Task<SendReceipt> SendAsync(PrintJob job, Func<int, Task> recordSpoolerId, CancellationToken token) => RunAsync(job, false, recordSpoolerId, token);
    private async Task<SendReceipt> RunAsync(PrintJob job, bool probe, Func<int, Task> recordSpoolerId, CancellationToken token)
    {
        var start = new ProcessStartInfo(helperExecutable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(probe ? "--raw-probe" : "--raw-helper");
        using var process = Process.Start(start) ?? throw new TransmissionException("Não foi possível iniciar auxiliar RAW.", false);
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        bool started = false; long bytes = 0; int? jobId = null;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(job, Protocol.Json).AsMemory(), token);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) throw new IOException("Auxiliar RAW terminou sem resultado.");
                var message = JsonSerializer.Deserialize<RawMessage>(line, Protocol.Json)!;
                bytes = message.Bytes;
                if (message.Kind == "created")
                {
                    started = true; jobId = message.JobId; await recordSpoolerId(jobId!.Value);
                    await process.StandardInput.WriteLineAsync("ACK".AsMemory(), token);
                }
                else if (message.Kind == "done") { await process.WaitForExitAsync(token); return new(bytes, jobId); }
                else throw new TransmissionException("Spooler RAW: " + message.Error, started || message.Uncertain, bytes);
            }
        }
        catch (TransmissionException) { throw; }
        catch (Exception ex) { throw new TransmissionException("Auxiliar RAW interrompido; reconciliar fila antes de reenviar.", !probe, bytes, ex); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
public record RawMessage(string Kind, int? JobId = null, long Bytes = 0, string? Error = null, bool Uncertain = false);
public static class RawHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Output; [MarshalAs(UnmanagedType.LPWStr)] public string DataType; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr handle);
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void Emit(RawMessage message) { Console.WriteLine(JsonSerializer.Serialize(message, Protocol.Json)); Console.Out.Flush(); }
    public static int Run(bool probe)
    {
        bool created = false; long bytes = 0; IntPtr handle = IntPtr.Zero;
        try
        {
            var job = JsonSerializer.Deserialize<PrintJob>(Console.ReadLine()!, Protocol.Json)!;
            Check(OpenPrinter(job.Destination.Queue, out handle, IntPtr.Zero));
            if (probe) { Emit(new("done")); return 0; }
            var doc = new DocInfo { Name = $"ZPL {job.Id:D}", DataType = "RAW" };
            int id = StartDocPrinter(handle, 1, ref doc); if (id == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            created = true; Emit(new("created", id));
            if (Console.ReadLine() != "ACK") throw new IOException("Persistência do ID RAW não confirmada.");
            Check(StartPagePrinter(handle));
            using var file = File.OpenRead(job.LocalPath!); byte[] buffer = new byte[65536]; int count;
            while ((count = file.Read(buffer)) > 0)
            {
                int offset = 0;
                while (offset < count)
                {
                    byte[] chunk = buffer.AsSpan(offset, count - offset).ToArray();
                    bool ok = WritePrinter(handle, chunk, chunk.Length, out int written);
                    bytes += Math.Max(written, 0); Check(ok);
                    if (written <= 0 || written > chunk.Length) throw new IOException("Contagem RAW inválida.");
                    offset += written;
                }
            }
            Check(EndPagePrinter(handle)); Check(EndDocPrinter(handle)); Emit(new("done", id, bytes)); return 0;
        }
        catch (Exception ex) { Emit(new("error", Bytes: bytes, Error: ex is Win32Exception win ? $"Erro Win32 {win.NativeErrorCode}" : ex.GetType().Name, Uncertain: created)); return 1; }
        finally { if (handle != IntPtr.Zero) ClosePrinter(handle); }
    }
}
