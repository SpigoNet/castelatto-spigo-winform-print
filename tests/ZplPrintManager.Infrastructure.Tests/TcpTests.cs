using System.Net;
using System.Net.Sockets;
using ZplPrintManager.Infrastructure;
namespace ZplPrintManager.Infrastructure.Tests;

public sealed class TcpTests
{
    [Fact]
    public async Task TcpReceiver_GetsExactlyOriginalBytes()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); job.Destination.Host = "127.0.0.1"; job.Destination.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receive = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(timeout.Token); using var output = new MemoryStream(); await client.GetStream().CopyToAsync(output, timeout.Token); return output.ToArray(); });
        var receipt = await new TcpPrinter().SendAsync(job, _ => Task.CompletedTask, timeout.Token); Assert.Equal(job.Size, receipt.Bytes); Assert.Equal(File.ReadAllBytes(job.LocalPath!), await receive);
    }
    [Fact]
    public async Task RefusedConnection_IsSafeToRetry()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); job.Destination.Host = "127.0.0.1"; job.Destination.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var error = await Assert.ThrowsAsync<Core.TransmissionException>(() => new TcpPrinter().SendAsync(job, _ => Task.CompletedTask, timeout.Token)); Assert.False(error.MayHavePrinted);
    }
}
