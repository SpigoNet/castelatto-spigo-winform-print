using System.Net.Sockets;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure;

public sealed class TcpPrinter : IPrinterTransport
{
    public async Task TestAsync(PrinterSettings printer, CancellationToken token)
    {
        using var client = new TcpClient(); await client.ConnectAsync(printer.Host, printer.Port, token);
    }
    public async Task<SendReceipt> SendAsync(PrintJob job, Func<int, Task> recordSpoolerId, CancellationToken token)
    {
        bool writeStarted = false; long sent = 0;
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(job.Destination.Host, job.Destination.Port, token);
            await using var file = File.OpenRead(job.LocalPath!);
            byte[] buffer = new byte[65536]; int count;
            while ((count = await file.ReadAsync(buffer, token)) > 0)
            {
                int offset = 0;
                while (offset < count)
                {
                    token.ThrowIfCancellationRequested(); writeStarted = true;
                    int written = await socket.SendAsync(buffer.AsMemory(offset, count - offset), SocketFlags.None, token);
                    if (written == 0) throw new IOException("Socket encerrado durante escrita.");
                    offset += written; sent += written;
                }
            }
            socket.Shutdown(SocketShutdown.Send);
            return new(sent);
        }
        catch (Exception ex) { throw new TransmissionException(writeStarted ? "Falha após iniciar escrita TCP; impressão pode ter ocorrido." : "Falha TCP anterior à primeira escrita.", writeStarted, sent, ex); }
    }
}
