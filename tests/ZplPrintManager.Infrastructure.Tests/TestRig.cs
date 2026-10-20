using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
using ZplPrintManager.Infrastructure;
namespace ZplPrintManager.Infrastructure.Tests;

internal sealed class TestRig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zpl-tests-" + Guid.NewGuid().ToString("N"));
    public SqliteStore Store { get; }
    public FakeRemote Remote { get; } = new();
    public FakePrinter Printer { get; } = new();
    public PayloadFiles Files { get; }
    public ProcessingEngine Engine { get; }
    public ConnectionSettings Connection { get; } = new() { Host = "localhost" };
    public PrinterSettings Destination { get; } = new() { Host = "localhost" };
    public MappingSettings Mapping { get; }
    public GlobalSettings Settings { get; } = new() { Paused = false, MinimumFreeBytes = 0 };
    public TestRig()
    {
        Store = new(Path.Combine(Root, "data", "test.db")); Files = new(Path.Combine(Root, "spool"), Path.Combine(Root, "quarantine")); Directory.CreateDirectory(Files.SpoolDirectory);
        Mapping = new() { ConnectionId = Connection.Id, PrinterId = Destination.Id, Enabled = true }; Engine = new(Store, Remote, Printer, Files);
    }
    public Configuration Config => new([Connection], [Destination], [Mapping], Settings);
    public async Task<PrintJob> ReadyAsync()
    {
        var job = new PrintJob { ConnectionId = Connection.Id, MappingId = Mapping.Id, Mapping = Mapping, Destination = Destination, State = JobState.Ready, PublicationId = Guid.NewGuid() + ".zpl", OriginalPath = "/entrada/a.zpl", ProcessingPath = "/processando/a.zpl", ArchivePath = "/impressos/a.zpl" };
        byte[] bytes = "^XA^FO10,10^FDTeste^FS^XZ"u8.ToArray(); var payload = await Files.DownloadAsync(job.Id, new MemoryStream(bytes), bytes.Length, 1000, true, default);
        job.LocalPath = payload.Path; job.Hash = payload.Hash; job.Size = payload.Size; Remote.Put(job.ProcessingPath, bytes); Store.Add(job); return job;
    }
    public void SaveConfiguration()
    {
        Store.SaveConfig("connection", Connection.Id.ToString(), 0, Connection); Store.SaveConfig("printer", Destination.Id.ToString(), 0, Destination); Store.SaveConfig("mapping", Mapping.Id.ToString(), 0, Mapping);
    }
    public void Dispose() { Store.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
internal sealed class FakePrinter : IPrinterTransport
{
    public int Calls; public Exception? Failure; public int Active; public int MaximumActive;
    public async Task<SendReceipt> SendAsync(PrintJob job, Func<int, Task> record, CancellationToken token)
    {
        Interlocked.Increment(ref Calls); int active = Interlocked.Increment(ref Active); MaximumActive = Math.Max(MaximumActive, active);
        try { await Task.Delay(5, token); if (Failure is not null) throw Failure; return new(job.Size); } finally { Interlocked.Decrement(ref Active); }
    }
    public Task TestAsync(PrinterSettings settings, CancellationToken token) => Task.CompletedTask;
}
internal sealed class FakeRemote : IRemoteFactory, IRemoteSession
{
    public Dictionary<string, byte[]> Data { get; } = [];
    public int Moves; public bool FailMove; public bool LoseMoveResponse;
    private static readonly DateTimeOffset Modified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Put(string path, byte[] bytes) => Data[path] = bytes;
    public Task<IRemoteSession> ConnectAsync(ConnectionSettings connection, CancellationToken token) => Task.FromResult<IRemoteSession>(this);
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken token) => Task.FromResult<IReadOnlyList<RemoteFile>>(Data.Where(k => k.Key[..k.Key.LastIndexOf('/')] == path).Select(k => new RemoteFile(k.Key, k.Value.Length, Modified)).ToArray());
    public Task<RemoteFile?> StatAsync(string path, CancellationToken token) => Task.FromResult(Data.TryGetValue(path, out var bytes) ? new RemoteFile(path, bytes.Length, Modified) : null);
    public Task MoveAsync(string source, string destination, CancellationToken token)
    {
        Moves++; if (FailMove) throw new IOException("Simulated rename failure"); if (Data.ContainsKey(destination)) throw new IOException("Destination collision"); Data[destination] = Data[source]; Data.Remove(source); if (LoseMoveResponse) { LoseMoveResponse = false; throw new IOException("Lost rename response"); }
        return Task.CompletedTask;
    }
    public Task<Stream> OpenReadAsync(string path, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream(Data[path]));
    public Task ProbeAsync(string directory, CancellationToken token) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
internal sealed class FakeSecrets : ISecretStore
{
    private readonly Dictionary<Guid, string> values = [];
    public string Read(Guid id) => values.GetValueOrDefault(id, "");
    public void Write(Guid id, string password) => values[id] = password;
}
