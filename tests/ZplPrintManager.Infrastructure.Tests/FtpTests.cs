using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZplPrintManager.Contracts;
using ZplPrintManager.Infrastructure;
namespace ZplPrintManager.Infrastructure.Tests;

public sealed class FtpTests
{
    [Fact]
    public async Task Adapter_ListsDownloadsRenamesAndProbesControlledServer()
    {
        await using var server = new FtpFixture();
        byte[] original = [.. "^XA"u8.ToArray(), 0, 255, .. "^XZ"u8.ToArray()];
        server.Files["/entrada/a.zpl"] = original;
        var settings = new ConnectionSettings { Host = "127.0.0.1", Port = server.Port, User = "test", Tls = TlsMode.None };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var remote = await new FtpRemoteFactory(new FakeSecrets()).ConnectAsync(settings, timeout.Token);
        Assert.Single(await remote.ListAsync("/entrada", timeout.Token));
        Assert.Equal(original.Length, (await remote.StatAsync("/entrada/a.zpl", timeout.Token))!.Size);
        await using (var stream = await remote.OpenReadAsync("/entrada/a.zpl", timeout.Token))
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy, timeout.Token); Assert.Equal(original, copy.ToArray());
        }
        await remote.MoveAsync("/entrada/a.zpl", "/impressos/a.zpl", timeout.Token);
        Assert.Null(await remote.StatAsync("/entrada/a.zpl", timeout.Token));
        Assert.Equal(original.Length, (await remote.StatAsync("/impressos/a.zpl", timeout.Token))!.Size);
        await remote.ProbeAsync("/entrada", timeout.Token);
        Assert.Single(server.Files); Assert.True(server.Files.ContainsKey("/impressos/a.zpl"));
    }
    [Fact]
    public async Task ExplicitTls_DoesNotFallBackToPlainFtp()
    {
        await using var server = new FtpFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(async () => await new FtpRemoteFactory(new FakeSecrets()).ConnectAsync(new() { Host = "127.0.0.1", Port = server.Port, User = "test", Tls = TlsMode.Explicit }, timeout.Token));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("PASS ", StringComparison.Ordinal));
    }
}
// A deliberately small, isolated FTP server for exercising the real FluentFTP adapter.
internal sealed class FtpFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accept;
    private readonly ConcurrentBag<Task> sessions = [];
    public ConcurrentDictionary<string, byte[]> Files { get; } = [];
    public ConcurrentQueue<string> Commands { get; } = [];
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FtpFixture() { listener.Start(); accept = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try { while (!stopping.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stopping.Token); sessions.Add(ServeAsync(client)); } }
        catch (OperationCanceledException) { }
    }
    private string Facts(string name) => $"type=file;size={Files[name].Length};modify=20260101000000; {name}";
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            TcpListener? data = null; string? rename = null;
            Task Reply(string text) => writer.WriteLineAsync(text);
            async Task Transfer(byte[]? output, string? upload = null)
            {
                await Reply("150 Opening data connection");
                using var peer = await data!.AcceptTcpClientAsync(stopping.Token);
                if (upload is null) await peer.GetStream().WriteAsync(output!, stopping.Token);
                else { using var memory = new MemoryStream(); await peer.GetStream().CopyToAsync(memory, stopping.Token); Files[upload] = memory.ToArray(); }
                peer.Close(); data.Stop(); data = null; await Reply("226 Transfer complete");
            }
            try
            {
                await Reply("220 Local integration fixture");
                while (await reader.ReadLineAsync(stopping.Token) is { } line)
                {
                    Commands.Enqueue(line); var parts = line.Split(' ', 2); string command = parts[0].ToUpperInvariant(); string arg = parts.Length > 1 ? parts[1] : "";
                    switch (command)
                    {
                        case "USER": await Reply("331 Password required"); break;
                        case "PASS": await Reply("230 Logged in"); break;
                        case "SYST": await Reply("215 UNIX Type: L8"); break;
                        case "FEAT": await Reply("211-Features\r\n UTF8\r\n SIZE\r\n MDTM\r\n MLST type*;size*;modify*;\r\n EPSV\r\n211 End"); break;
                        case "OPTS": case "TYPE": case "NOOP": case "CLNT": await Reply("200 OK"); break;
                        case "PWD": await Reply("257 \"/\" is current directory"); break;
                        case "CWD": await Reply("250 Directory changed"); break;
                        case "MKD": await Reply($"257 \"{arg}\" created"); break;
                        case "SIZE": await Reply(Files.TryGetValue(arg, out var size) ? $"213 {size.Length}" : "550 Missing"); break;
                        case "MDTM": await Reply(Files.ContainsKey(arg) ? "213 20260101000000" : "550 Missing"); break;
                        case "MLST": await Reply(Files.ContainsKey(arg) ? $"250-Object\r\n {Facts(arg)}\r\n250 End" : "550 Missing"); break;
                        case "EPSV":
                        case "PASV":
                            data?.Stop(); data = new(IPAddress.Loopback, 0); data.Start(); int port = ((IPEndPoint)data.LocalEndpoint).Port;
                            await Reply(command == "EPSV" ? $"229 Entering Extended Passive Mode (|||{port}|)" : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})"); break;
                        case "MLSD":
                            var names = Files.Keys.Where(k => k[..k.LastIndexOf('/')] == arg.TrimEnd('/'));
                            await Transfer(Encoding.UTF8.GetBytes(string.Join("\r\n", names.Select(k => Facts(k)[..(Facts(k).LastIndexOf(' ') + 1)] + k[(k.LastIndexOf('/') + 1)..])) + "\r\n")); break;
                        case "RETR": if (Files.TryGetValue(arg, out var bytes)) await Transfer(bytes); else await Reply("550 Missing"); break;
                        case "STOR": await Transfer(null, arg); break;
                        case "RNFR": rename = arg; await Reply(Files.ContainsKey(arg) ? "350 Ready" : "550 Missing"); break;
                        case "RNTO": if (rename is not null && Files.TryRemove(rename, out var moved)) { Files[arg] = moved; await Reply("250 Renamed"); } else await Reply("550 Missing"); break;
                        case "DELE": Files.TryRemove(arg, out _); await Reply("250 Deleted"); break;
                        case "QUIT": await Reply("221 Bye"); return;
                        default: await Reply("502 Not implemented"); break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            finally { data?.Stop(); }
        }
    }
    public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await accept; await Task.WhenAll(sessions); stopping.Dispose(); }
}
