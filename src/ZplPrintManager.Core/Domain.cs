using ZplPrintManager.Contracts;
namespace ZplPrintManager.Core;

public sealed class PrintJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Sequence { get; set; }
    public Guid ConnectionId { get; set; }
    public ConnectionSettings? ConnectionSnapshot { get; set; }
    public Guid MappingId { get; set; }
    public string PublicationId { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string ProcessingPath { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string? LocalPath { get; set; }
    public string? Hash { get; set; }
    public long Size { get; set; }
    public DateTimeOffset? RemoteModifiedUtc { get; set; }
    public MappingSettings Mapping { get; set; } = new();
    public PrinterSettings Destination { get; set; } = new();
    public JobState State { get; set; } = JobState.Discovered;
    public JobState? RetryStage { get; set; }
    public Dictionary<JobState, int> Failures { get; set; } = [];
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public Evidence Evidence { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StableSinceUtc { get; set; } = DateTimeOffset.UtcNow;
    public int StableObservations { get; set; }
    public Guid? ReprintOf { get; set; }
    public bool QuarantineMovePending { get; set; }
    public bool LocalOnly { get; set; }
    public int? SpoolerJobId { get; set; }
    public long BytesSent { get; set; }
}
public record RemoteFile(string Path, long Size, DateTimeOffset? ModifiedUtc);
public record Payload(string Path, string Hash, long Size);
public record SendReceipt(long Bytes, int? SpoolerJobId = null);
public sealed class TransmissionException(string message, bool mayHavePrinted, long bytes = 0, Exception? inner = null) : IOException(message, inner)
{
    public bool MayHavePrinted { get; } = mayHavePrinted;
    public long Bytes { get; } = bytes;
}
public sealed class ReconciliationException(string message) : IOException(message);
public interface IRemoteSession : IAsyncDisposable
{
    Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken token);
    Task<RemoteFile?> StatAsync(string path, CancellationToken token);
    Task MoveAsync(string source, string destination, CancellationToken token);
    Task<Stream> OpenReadAsync(string path, CancellationToken token);
    Task ProbeAsync(string directory, CancellationToken token);
}
public interface IRemoteFactory { Task<IRemoteSession> ConnectAsync(ConnectionSettings connection, CancellationToken token); }
public interface IPrinterTransport
{
    Task<SendReceipt> SendAsync(PrintJob job, Func<int, Task> recordSpoolerId, CancellationToken token);
    Task TestAsync(PrinterSettings printer, CancellationToken token);
}
public interface IJobStore
{
    IReadOnlyList<PrintJob> Jobs();
    PrintJob? Find(Guid id);
    bool Add(PrintJob job);
    void Save(PrintJob job);
    Guid BeginAttempt(PrintJob job, JobState stage);
    void EndAttempt(Guid attempt, string result, long bytes, string? error, int? spoolerId = null);
    void RecordMove(PrintJob job, string source, string destination, string result);
}
public static class JobPolicy
{
    public static bool Terminal(JobState state) => state is JobState.Archived or JobState.Discarded;
    public static bool BlocksPrinter(PrintJob job) => job.State == JobState.Uncertain || (job.State == JobState.Quarantine && job.Evidence == Evidence.Unavailable) || (job.State == JobState.WaitingRetry && job.RetryStage == JobState.Ready);
    public static bool MaySend(PrintJob job) => job.State == JobState.Ready;
    public static JobState Recover(JobState state) => state switch
    {
        JobState.Sending => JobState.Uncertain,
        JobState.Downloading => JobState.Acquired,
        _ => state
    };
    public static TimeSpan Backoff(int failure, double jitter = 0.5)
    {
        int[] delays = [5, 15, 60, 300];
        return TimeSpan.FromSeconds(delays[Math.Clamp(failure - 1, 0, 3)] * (1 + Math.Clamp(jitter, 0, 1) * .2));
    }
}
public static class RemotePaths
{
    public static string Normalize(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Any(char.IsControl) || path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Caminho remoto deve ser absoluto, sem traversal ou caracteres de controle.");
        return path == "/" ? path : path.TrimEnd('/');
    }
    public static string Combine(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name is "." or ".." || name.Any(char.IsControl))
            throw new ArgumentException("Nome remoto inválido.");
        return Normalize(root).TrimEnd('/') + "/" + name;
    }
    public static bool Overlaps(string a, string b) => a == b || a.StartsWith(b.TrimEnd('/') + "/", StringComparison.Ordinal) || b.StartsWith(a.TrimEnd('/') + "/", StringComparison.Ordinal);
}

public sealed class PermanentConnectionException(string message, Exception inner) : IOException(message, inner);
