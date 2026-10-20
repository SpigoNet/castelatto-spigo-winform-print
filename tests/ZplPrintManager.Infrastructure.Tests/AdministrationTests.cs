using System.Text;
using ZplPrintManager.Contracts;
using ZplPrintManager.Infrastructure;
namespace ZplPrintManager.Infrastructure.Tests;

public sealed class AdministrationTests
{
    [Fact]
    public async Task UnauthorizedMutation_IsRejected()
    {
        using var rig = new TestRig(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        var request = new Request(1, Guid.NewGuid(), Guid.NewGuid(), "SaveConnection", Protocol.Element(rig.Connection));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.ExecuteAsync(request, "reader", Role.Reader, default)); Assert.Empty(rig.Store.Configuration().Connections);
    }
    [Fact]
    public async Task RepeatedPrintCommand_CreatesExactlyOneJob()
    {
        using var rig = new TestRig(); rig.SaveConfiguration(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        var request = new Request(1, Guid.NewGuid(), Guid.NewGuid(), "PrintTestLabel", Protocol.Element(new TestLabelAction(rig.Destination.Id, "Teste autorizado")));
        var first = await admin.ExecuteAsync(request, "operator", Role.Operator, default); var second = await admin.ExecuteAsync(request, "operator", Role.Operator, default);
        Assert.Equal(first.GetRawText(), second.GetRawText()); Assert.Single(rig.Store.Jobs()); Assert.Equal(0, rig.Printer.Calls);
    }
    [Fact]
    public async Task IdempotencyId_CannotBeReusedWithDifferentPayload()
    {
        using var rig = new TestRig(); rig.SaveConfiguration(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        var request = new Request(1, Guid.NewGuid(), Guid.NewGuid(), "Pause", Protocol.Element(new PauseAction("global", Guid.Empty, true)));
        await admin.ExecuteAsync(request, "operator", Role.Operator, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ExecuteAsync(request with { Command = "Resume" }, "operator", Role.Operator, default));
    }
    [Fact]
    public async Task ConnectionPassword_IsNeverReturnedOrStoredInDatabase()
    {
        using var rig = new TestRig(); var secrets = new FakeSecrets(); var admin = new Administration(rig.Store, secrets, rig.Remote, rig.Printer, rig.Files, rig.Root); rig.Connection.Password = "do-not-persist-this";
        rig.Connection.User = "persisted-user-canary";
        await admin.ExecuteAsync(new(1, Guid.NewGuid(), Guid.NewGuid(), "SaveConnection", Protocol.Element(rig.Connection)), "admin", Role.Administrator, default);
        Assert.Equal("", rig.Store.Configuration().Connections.Single().Password); Assert.Equal("do-not-persist-this", secrets.Read(rig.Connection.Id));
        // Capture committed WAL data and close the destination before inspecting bytes.
        // Opening the live database with File.ReadAllText conflicts with SQLite on Windows.
        string snapshot = Path.Combine(rig.Root, "password-check.db");
        rig.Store.Backup(snapshot);
        byte[] database = File.ReadAllBytes(snapshot);
        Assert.True(database.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rig.Connection.User)) >= 0);
        Assert.Equal(-1, database.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rig.Connection.Password)));
    }
    [Fact]
    public async Task Backup_CapturesConsistentDatabaseAndPayload()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        var backup = admin.CreateBackup(Guid.NewGuid()); using var copy = new SqliteStore(Path.Combine(backup.Path, "manager.db")); Assert.Equal(job.Hash, copy.Find(job.Id)!.Hash);
        Assert.Equal(File.ReadAllBytes(job.LocalPath!), File.ReadAllBytes(Path.Combine(backup.Path, "spool", $"{job.Id:N}.zpl")));
    }
    [Fact]
    public async Task Restore_PreservesPostBackupIdentityAndBlocksPendingJobs()
    {
        using var rig = new TestRig(); rig.SaveConfiguration(); var first = await rig.ReadyAsync(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        Guid backupId = Guid.NewGuid(); admin.CreateBackup(backupId); var later = await rig.ReadyAsync();
        await admin.ExecuteAsync(new(1, Guid.NewGuid(), Guid.NewGuid(), "RestoreBackup", Protocol.Element(new RestoreAction(backupId.ToString("N"), "Teste de recuperação"))), "admin", Role.Administrator, default);
        Assert.Equal(2, rig.Store.Jobs().Count); Assert.All(rig.Store.Jobs(), j => Assert.Equal(JobState.Uncertain, j.State)); Assert.True(rig.Store.Configuration().Settings.Paused);
    }
    [Fact]
    public async Task Retention_PreservesUncertainPayload()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); job.State = JobState.Uncertain; rig.Store.Save(job);
        new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root).Retain(); Assert.True(File.Exists(job.LocalPath));
    }
    [Fact]
    public async Task Csv_NeutralizesSpreadsheetFormulas()
    {
        using var rig = new TestRig(); var job = await rig.ReadyAsync(); job.PublicationId = "=HYPERLINK(1)"; rig.Store.Save(job);
        string csv = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root).HistoryCsv(); Assert.Contains("'=HYPERLINK", csv);
    }
}

public sealed class PauseTests
{
    [Fact]
    public async Task Pause_DoesNotWaitForInFlightOperation()
    {
        using var rig = new TestRig(); var admin = new Administration(rig.Store, new FakeSecrets(), rig.Remote, rig.Printer, rig.Files, rig.Root);
        using var operation = await admin.Gate.EnterOperationAsync(default);
        var response = admin.ExecuteAsync(new(1, Guid.NewGuid(), Guid.NewGuid(), "Pause", Protocol.Element(new PauseAction("global", Guid.Empty, true))), "operator", Role.Operator, default);
        await response.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(rig.Store.Configuration().Settings.Paused);
    }
}
