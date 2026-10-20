using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure.Tests;

public sealed class ProcessingTests
{
    [Fact]
    public async Task ArchiveFailure_RetriesOnlyMove()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); rig.Remote.FailMove = true;
        await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default);
        Assert.Equal(JobState.ArchivePending, job.State); Assert.Equal(Evidence.TransportAccepted, job.Evidence); Assert.Equal(1, rig.Printer.Calls);
        rig.Remote.FailMove = false; job.NextAttemptUtc = null; await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default);
        Assert.Equal(JobState.Archived, job.State); Assert.Equal(1, rig.Printer.Calls);
    }
    [Fact]
    public async Task LostRenameResponse_ReconcilesWithoutResend()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); rig.Remote.LoseMoveResponse = true;
        await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default); Assert.Equal(JobState.ArchivePending, job.State);
        job.NextAttemptUtc = null; await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default);
        Assert.Equal(JobState.Archived, job.State); Assert.Equal(1, rig.Printer.Calls); Assert.Equal(1, rig.Remote.Moves);
    }
    [Fact]
    public async Task FailureAfterWrite_NeverAutomaticallyResends()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); rig.Printer.Failure = new TransmissionException("Injected partial write", true, 3);
        await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default); await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default);
        Assert.Equal(JobState.Uncertain, job.State); Assert.Equal(1, rig.Printer.Calls); Assert.Equal(3, job.BytesSent);
    }
    [Fact]
    public async Task FailureBeforeWrite_RetriesWithinLimit()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); rig.Printer.Failure = new TransmissionException("Refused", false); rig.Settings.MaximumAttempts = 2;
        await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default); Assert.Equal(JobState.WaitingRetry, job.State);
        job.NextAttemptUtc = null; await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default); Assert.Equal(JobState.Quarantine, job.State); Assert.Equal(2, rig.Printer.Calls);
    }
    [Fact]
    public async Task RestartDuringSend_BlocksJob()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); job.State = JobState.Sending; rig.Store.Save(job); rig.Engine.Recover();
        Assert.Equal(JobState.Uncertain, rig.Store.Find(job.Id)!.State);
        await rig.Engine.TickAsync(rig.Config, default); Assert.Equal(0, rig.Printer.Calls);
    }
    [Fact]
    public async Task SamePublication_DeduplicatesButSameContentWithNewIdDoesNot()
    {
        using var rig = new TestRig(); byte[] bytes = "^XA^XZ"u8.ToArray(); rig.Remote.Put("/entrada/one.zpl", bytes); rig.Remote.Put("/entrada/two.zpl", bytes);
        await rig.Engine.TickAsync(rig.Config, default);
        Assert.Equal(2, rig.Store.Jobs().Count); Assert.Equal(2, rig.Printer.Calls);
        var existing = rig.Store.Jobs()[0]; var duplicate = Protocol.Read<PrintJob>(Protocol.Element(existing)); duplicate.Id = Guid.NewGuid(); Assert.False(rig.Store.Add(duplicate));
    }
    [Fact]
    public async Task TemporaryFile_IsNotDiscovered()
    {
        using var rig = new TestRig(); rig.Remote.Put("/entrada/file.uploading", "^XA^XZ"u8.ToArray()); await rig.Engine.TickAsync(rig.Config, default); Assert.Empty(rig.Store.Jobs());
    }
    [Fact]
    public async Task PayloadTampering_BlocksSending()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); File.WriteAllText(job.LocalPath!, "changed"); await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default); Assert.Equal(JobState.Quarantine, job.State); Assert.Equal(0, rig.Printer.Calls);
    }
    [Fact]
    public async Task PrinterSnapshot_SurvivesConfigurationChange()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); string original = job.Destination.Host; rig.Destination.Host = "changed";
        Assert.Equal(original, rig.Store.Find(job.Id)!.Destination.Host);
    }
    [Fact]
    public async Task SamePhysicalDestination_SerializesJobs()
    {
        using var rig = new TestRig(); var a = await rig.ReadyAsync(); var b = await rig.ReadyAsync(); a.LocalOnly = b.LocalOnly = true; rig.Store.Save(a); rig.Store.Save(b);
        await rig.Engine.TickAsync(rig.Config, default); Assert.Equal(2, rig.Printer.Calls); Assert.Equal(1, rig.Printer.MaximumActive);
    }
    [Fact]
    public async Task AmbiguousArchive_NeverOverwritesDestination()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); rig.Remote.Put(job.ArchivePath, "OTHER"u8.ToArray()); await rig.Engine.ProcessAsync(job, rig.Connection, rig.Settings, default);
        Assert.Equal(JobState.Quarantine, job.State); Assert.Equal("OTHER"u8.ToArray(), rig.Remote.Data[job.ArchivePath]); Assert.Equal(0, rig.Remote.Moves);
    }
}

public sealed class DiscoveryBoundaryTests
{
    [Fact]
    public async Task ReusedIdentityWithDifferentContent_IsQuarantined()
    {
        using var rig = new TestRig(); rig.Remote.Put("/entrada/one.zpl", "^XAone^XZ"u8.ToArray());
        await rig.Engine.TickAsync(rig.Config, default);
        rig.Remote.Put("/entrada/one.zpl", "^XAtwo^XZ"u8.ToArray());
        var restarted = new ProcessingEngine(rig.Store, rig.Remote, rig.Printer, rig.Files);
        await restarted.TickAsync(rig.Config, default);
        Assert.Equal(JobState.Quarantine, rig.Store.Jobs().Single().State); Assert.Equal(1, rig.Printer.Calls);
    }
    [Fact]
    public async Task StabilityMode_DoesNotAcquireBeforeWindow()
    {
        using var rig = new TestRig(); rig.Mapping.Publication = PublicationMode.StableMetadata; rig.Remote.Put("/entrada/a.zpl", "^XA^XZ"u8.ToArray());
        await rig.Engine.TickAsync(rig.Config, default);
        Assert.Equal(JobState.WaitingStability, rig.Store.Jobs().Single().State); Assert.Equal(0, rig.Printer.Calls);
        var job = rig.Store.Jobs().Single(); job.StableSinceUtc = DateTimeOffset.UtcNow.AddSeconds(-11); job.StableObservations = 2; rig.Store.Save(job);
        await new ProcessingEngine(rig.Store, rig.Remote, rig.Printer, rig.Files).TickAsync(rig.Config, default);
        Assert.Equal(JobState.Archived, rig.Store.Jobs().Single().State); Assert.Equal(1, rig.Printer.Calls);
    }
    [Fact]
    public async Task CapacityLimit_StopsIngestionWithinOneListing()
    {
        using var rig = new TestRig(); rig.Settings.MaximumPendingJobs = 1; rig.Remote.Put("/entrada/a.zpl", "^XA^XZ"u8.ToArray()); rig.Remote.Put("/entrada/b.zpl", "^XA^XZ"u8.ToArray());
        await rig.Engine.TickAsync(rig.Config, default); Assert.Single(rig.Store.Jobs()); Assert.Contains(rig.Remote.Data.Keys, k => k.StartsWith("/entrada/"));
    }
    [Fact]
    public async Task EmptyPublication_MovesToErrorsWithoutPrinting()
    {
        using var rig = new TestRig(); rig.Remote.Put("/entrada/empty.zpl", []); await rig.Engine.TickAsync(rig.Config, default);
        Assert.Equal(0, rig.Printer.Calls); Assert.Contains(rig.Remote.Data.Keys, k => k.StartsWith("/erros/")); Assert.Equal(JobState.Quarantine, rig.Store.Jobs().Single().State);
    }
}
