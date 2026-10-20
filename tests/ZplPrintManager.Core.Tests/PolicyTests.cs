using System.Security.Cryptography;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Core.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(JobState.Sending, JobState.Uncertain)]
    [InlineData(JobState.Downloading, JobState.Acquired)]
    [InlineData(JobState.ArchivePending, JobState.ArchivePending)]
    [InlineData(JobState.Archived, JobState.Archived)]
    [InlineData(JobState.Ready, JobState.Ready)]
    public void Recovery_PreservesTransmissionBoundary(JobState before, JobState after) => Assert.Equal(after, JobPolicy.Recover(before));
    [Theory]
    [InlineData("../evil.zpl")]
    [InlineData("a/b.zpl")]
    [InlineData("a\\b.zpl")]
    [InlineData("..")]
    [InlineData("a\r\n.zpl")]
    public void RemoteName_RejectsTraversal(string name) => Assert.Throws<ArgumentException>(() => RemotePaths.Combine("/entrada", name));
    [Fact] public void DifferentCase_PreservesServerIdentity() => Assert.NotEqual(RemotePaths.Combine("/entrada", "A.zpl"), RemotePaths.Combine("/entrada", "a.zpl"));
    [Fact] public void ArchiveFailure_DoesNotBlockPrinter() => Assert.False(JobPolicy.BlocksPrinter(new() { State = JobState.Quarantine, Evidence = Evidence.TransportAccepted }));
    [Fact] public void Uncertainty_BlocksPrinter() => Assert.True(JobPolicy.BlocksPrinter(new() { State = JobState.Uncertain }));
    [Fact] public void Backoff_IsBounded() => Assert.Equal(360, JobPolicy.Backoff(100, 1).TotalSeconds);
    [Fact]
    public async Task Download_PreservesBinaryBytesAndHash()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var files = new PayloadFiles(root, Path.Combine(root, "quarantine")); byte[] bytes = [.. "^XA"u8.ToArray(), 0, 255, 13, 10, .. "^XZ"u8.ToArray()];
            var result = await files.DownloadAsync(Guid.NewGuid(), new MemoryStream(bytes), bytes.Length, 100, true, default);
            Assert.Equal(bytes, File.ReadAllBytes(result.Path)); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Hash);
            Assert.Empty(Directory.GetFiles(root, "*.part"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(1, 10)]
    [InlineData(100, 10)]
    [InlineData(0, 10)]
    public async Task InvalidSize_NeverCreatesReadyPayload(int expected, int maximum)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { var files = new PayloadFiles(root, Path.Combine(root, "quarantine")); await Assert.ThrowsAsync<InvalidDataException>(() => files.DownloadAsync(Guid.NewGuid(), new MemoryStream("^XA^XZ"u8.ToArray()), expected, maximum, true, default)); Assert.Empty(Directory.GetFiles(root, "*.zpl")); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Mapping_RejectsArchiveUnderInput()
    {
        var connection = new ConnectionSettings(); var printer = new PrinterSettings(); var mapping = new MappingSettings { ConnectionId = connection.Id, PrinterId = printer.Id, ArchivePath = "/entrada/impressos" };
        Assert.Throws<ArgumentException>(() => ConfigurationValidation.Mapping(mapping, new([connection], [printer], [], new())));
    }
    [Fact]
    public async Task Framing_RejectsOversizedLengthBeforeAllocation()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(Protocol.MaximumMessageBytes + 1)); await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(stream, default));
    }
}

public sealed class GateTests
{
    [Fact]
    public async Task Maintenance_DrainsOperationsAndPreventsAdmission()
    {
        var gate = new ProcessingGate();
        var first = await gate.EnterOperationAsync(default);
        Task maintenance = gate.WaitAsync(default);
        Assert.False(maintenance.IsCompleted);
        Task<IDisposable> later = gate.EnterOperationAsync(default);
        Assert.False(later.IsCompleted);
        first.Dispose(); await maintenance.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(later.IsCompleted);
        gate.Release(); using var admitted = await later.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task CancelledMaintenance_DoesNotLeakSlots()
    {
        var gate = new ProcessingGate(); using var operation = await gate.EnterOperationAsync(default);
        using var cancellation = new CancellationTokenSource(); var waiting = gate.WaitAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var second = await gate.EnterOperationAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
    }
}
