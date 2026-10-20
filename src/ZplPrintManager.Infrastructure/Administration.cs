using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure;

public sealed class Administration(SqliteStore store, ISecretStore secrets, IRemoteFactory remote, IPrinterTransport printer, PayloadFiles payloads, string root)
{
    public ProcessingGate Gate { get; } = new();
    public DateTimeOffset HeartbeatUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? OperationalError { get; set; }
    public ProcessingEngine? Engine { get; set; }
    private static readonly HashSet<string> ReaderCommands = ["GetStatus", "GetConfiguration", "ListJobs", "GetJob", "ExportHistory"];
    private static readonly HashSet<string> OperatorCommands = ["Pause", "Resume", "ResolveJob", "Reprint", "PrintTestLabel"];
    public async Task<JsonElement> ExecuteAsync(Request request, string actor, Role role, CancellationToken token)
    {
        if (request.Version != Protocol.Version || request.CorrelationId == Guid.Empty) throw new ArgumentException("Versão/correlação inválida.");
        bool read = ReaderCommands.Contains(request.Command);
        Role minimum = read ? Role.Reader : OperatorCommands.Contains(request.Command) ? Role.Operator : Role.Administrator;
        if (role < minimum) throw new UnauthorizedAccessException("Seu grupo Windows não permite esta operação.");
        if (read) return ReadCommand(request, role);
        if (request.IdempotencyId == Guid.Empty) throw new ArgumentException("ID de idempotência obrigatório.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Command + request.Payload.GetRawText())));
        if (request.Command == "Pause")
        {
            return store.Atomic(() =>
            {
                var cachedPause = store.CachedCommand(request.IdempotencyId, actor, fingerprint); if (cachedPause.HasValue) return cachedPause.Value;
                var response = Mutate(request, actor); store.CacheCommand(request.IdempotencyId, actor, fingerprint, response); return response;
            });
        }
        await Gate.WaitAsync(token);
        try
        {
            var cached = store.CachedCommand(request.IdempotencyId, actor, fingerprint); if (cached.HasValue) return cached.Value;
            // External probes run before the short database transaction. They never print.
            await PrepareAsync(request, token);
            JsonElement result;
            if (request.Command is "CreateBackup" or "ExportDiagnostics" or "RestoreBackup")
            {
                result = Protocol.Element(request.Command switch
                {
                    "CreateBackup" => CreateBackup(request.IdempotencyId),
                    "RestoreBackup" => RestoreBackup(Protocol.Read<RestoreAction>(request.Payload), request.IdempotencyId),
                    _ => ExportDiagnostics(request.IdempotencyId)
                });
                store.Atomic(() => { store.Audit(actor, request.Command, request.IdempotencyId.ToString(), "Solicitação administrativa"); store.CacheCommand(request.IdempotencyId, actor, fingerprint, result); return 0; });
            }
            else
            {
                result = store.Atomic(() =>
                {
                    var value = Mutate(request, actor);
                    store.CacheCommand(request.IdempotencyId, actor, fingerprint, value);
                    return value;
                });
            }
            return result;
        }
        finally { Gate.Release(); }
    }
    private JsonElement ReadCommand(Request request, Role role)
    {
        var config = store.Configuration();
        return request.Command switch
        {
            "GetStatus" => Protocol.Element(new StatusSnapshot(HeartbeatUtc, config.Settings.Paused, payloads.FreeBytes, store.Jobs().GroupBy(j => Protocol.StateName(j.State)).ToDictionary(g => g.Key, g => g.Count()), Engine?.LastPoll.ToDictionary(k => k.Key, v => v.Value) ?? [], (Engine?.Alerts ?? []).Concat(OperationalError is null ? [] : new[] { OperationalError }).ToArray(), role)),
            "GetConfiguration" => Protocol.Element(config),
            "ListJobs" => Protocol.Element(store.Jobs().Reverse().Take(1000).Select(j => new JobSummary(j.Id, j.PublicationId, j.Destination.Name, Protocol.StateName(j.State), Protocol.EvidenceName(j.Evidence), j.CreatedUtc, j.Error, j.ReprintOf)).ToArray()),
            "GetJob" => Protocol.Element(store.Detail(Protocol.Read<Guid>(request.Payload))),
            "ExportHistory" => Protocol.Element(HistoryCsv()),
            _ => throw new ArgumentException("Comando desconhecido.")
        };
    }
    private async Task PrepareAsync(Request request, CancellationToken token)
    {
        var config = store.Configuration();
        if (request.Command == "SaveMapping")
        {
            var value = Protocol.Read<MappingSettings>(request.Payload); ConfigurationValidation.Mapping(value, config);
            if (value.Enabled)
            {
                var connection = config.Connections.Single(c => c.Id == value.ConnectionId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(connection.OperationTimeoutSeconds));
                await using var session = await remote.ConnectAsync(connection, deadline.Token);
                foreach (var path in new[] { value.InputPath, value.ProcessingPath, value.ArchivePath, value.ErrorPath }) await session.ProbeAsync(path, deadline.Token);
            }
        }
        else if (request.Command == "TestConnection")
        {
            var id = Protocol.Read<Guid>(request.Payload); var connection = config.Connections.Single(c => c.Id == id);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(connection.OperationTimeoutSeconds));
            await using var session = await remote.ConnectAsync(connection, deadline.Token);
            foreach (var mapping in config.Mappings.Where(m => m.ConnectionId == id))
                foreach (var path in new[] { mapping.InputPath, mapping.ProcessingPath, mapping.ArchivePath, mapping.ErrorPath }) await session.ProbeAsync(path, deadline.Token);
        }
        else if (request.Command == "TestPrinterConnectivity")
        {
            var id = Protocol.Read<Guid>(request.Payload);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await printer.TestAsync(config.Printers.Single(p => p.Id == id), deadline.Token);
        }
        else if (request.Command == "Reprint")
        {
            var action = Protocol.Read<JobAction>(request.Payload); var job = store.Find(action.JobId) ?? throw new ArgumentException("Trabalho não encontrado.");
            if (!File.Exists(job.LocalPath) && !job.LocalOnly && job.Hash is not null)
            {
                var connection = config.Connections.Single(c => c.Id == job.ConnectionId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(connection.OperationTimeoutSeconds));
                await using var session = await remote.ConnectAsync(connection, deadline.Token);
                await using var stream = await session.OpenReadAsync(job.ArchivePath, deadline.Token);
                var copy = await payloads.DownloadAsync(job.Id, stream, job.Size, config.Settings.MaximumFileBytes, job.Mapping.ValidateZpl, deadline.Token);
                if (copy.Hash != job.Hash) throw new InvalidDataException("Arquivo arquivado divergente; republicação com novo ID necessária.");
                job.LocalPath = copy.Path; store.Save(job);
            }
            await PayloadFiles.VerifyAsync(job, token);
        }
    }
    private JsonElement Mutate(Request request, string actor)
    {
        var config = store.Configuration(); object result = new { Message = "Operação concluída." };
        string entity = ""; string reason = "Alteração pela interface";
        switch (request.Command)
        {
            case "SaveConnection":
                var connection = Protocol.Read<ConnectionSettings>(request.Payload); ConfigurationValidation.Connection(connection);
                var previous = config.Connections.FirstOrDefault(c => c.Id == connection.Id);
                if ((previous?.Revision ?? 0) != connection.Revision) throw new InvalidOperationException("Configuração alterada; atualize.");
                if (!string.IsNullOrEmpty(connection.Password)) secrets.Write(connection.Id, connection.Password);
                connection.Password = ""; int cr = connection.Revision++; store.SaveConfig("connection", connection.Id.ToString(), cr, connection); entity = connection.Id.ToString(); break;
            case "SavePrinter":
                var destination = Protocol.Read<PrinterSettings>(request.Payload); ConfigurationValidation.Printer(destination, config);
                if (store.Jobs().Any(j => !JobPolicy.Terminal(j.State) && j.Destination.Id == destination.Id && j.Destination.PhysicalId != destination.PhysicalId)) throw new ArgumentException("Não altere o destino físico enquanto houver trabalhos pendentes.");
                int pr = destination.Revision++; store.SaveConfig("printer", destination.Id.ToString(), pr, destination); entity = destination.Id.ToString(); break;
            case "SaveMapping":
                var mapping = Protocol.Read<MappingSettings>(request.Payload); ConfigurationValidation.Mapping(mapping, config);
                int mr = mapping.Revision++; store.SaveConfig("mapping", mapping.Id.ToString(), mr, mapping); entity = mapping.Id.ToString(); break;
            case "SaveSettings":
                var settings = Protocol.Read<GlobalSettings>(request.Payload); ConfigurationValidation.Settings(settings);
                int sr = settings.Revision++; store.SaveConfig("settings", "global", sr, settings); break;
            case "Pause":
            case "Resume":
                var pause = Protocol.Read<PauseAction>(request.Payload); bool paused = request.Command == "Pause";
                if (pause.Scope == "global") { var s = config.Settings; int r = s.Revision++; s.Paused = paused; store.SaveConfig("settings", "global", r, s); }
                else if (pause.Scope == "printer") { var p = config.Printers.Single(p => p.Id == pause.Id); int r = p.Revision++; p.Paused = paused; store.SaveConfig("printer", p.Id.ToString(), r, p); }
                else if (pause.Scope == "mapping") { var m = config.Mappings.Single(m => m.Id == pause.Id); int r = m.Revision++; m.Paused = paused; store.SaveConfig("mapping", m.Id.ToString(), r, m); }
                else throw new ArgumentException("Escopo inválido.");
                entity = pause.Scope + ":" + pause.Id; break;
            case "ResolveJob":
                var action = Protocol.Read<JobAction>(request.Payload); Reason(action.Reason); reason = action.Reason; entity = action.JobId.ToString();
                var job = store.Find(action.JobId) ?? throw new ArgumentException("Trabalho não encontrado.");
                if (job.State is JobState.Sending or JobState.Downloading) throw new ArgumentException("Trabalho em andamento.");
                if (action.Action == "discard") job.State = JobState.Discarded;
                else if (action.Action == "confirm") { job.Evidence = Evidence.OperatorConfirmed; job.State = job.LocalOnly ? JobState.Archived : JobState.ArchivePending; job.NextAttemptUtc = null; }
                else if (action.Action == "retryArchive" && job.Evidence != Evidence.Unavailable) { job.State = JobState.ArchivePending; job.NextAttemptUtc = null; job.Failures.Remove(JobState.ArchivePending); }
                else throw new ArgumentException("Ação inválida. Para novo envio, crie reimpressão auditada.");
                job.Error = null; store.Save(job); break;
            case "Reprint":
                var reprint = Protocol.Read<JobAction>(request.Payload); Reason(reprint.Reason); reason = reprint.Reason; entity = reprint.JobId.ToString();
                var original = store.Find(reprint.JobId) ?? throw new ArgumentException("Trabalho não encontrado.");
                if (original.State is JobState.Sending or JobState.Downloading) throw new ArgumentException("Trabalho em andamento.");
                var clone = Protocol.Read<PrintJob>(Protocol.Element(original)); clone.Id = Guid.NewGuid(); clone.Sequence = 0; clone.ReprintOf = original.Id; clone.LocalOnly = true;
                clone.State = JobState.Ready; clone.Evidence = Evidence.Unavailable; clone.Failures = []; clone.Error = null; clone.BytesSent = 0; clone.SpoolerJobId = null; clone.NextAttemptUtc = null; clone.CreatedUtc = clone.UpdatedUtc = DateTimeOffset.UtcNow;
                string copy = Path.Combine(payloads.SpoolDirectory, $"{clone.Id:N}.zpl");
                using (var input = File.OpenRead(original.LocalPath!)) using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                clone.LocalPath = copy; store.Add(clone);
                if (original.State is JobState.Uncertain or JobState.Quarantine) { original.State = JobState.Discarded; original.Error = "Substituído por reimpressão autorizada; possível duplicidade."; store.Save(original); }
                result = new { JobId = clone.Id }; break;
            case "PrintTestLabel":
                var test = Protocol.Read<TestLabelAction>(request.Payload); Reason(test.Reason); reason = test.Reason; entity = test.PrinterId.ToString();
                var testPrinter = config.Printers.Single(p => p.Id == test.PrinterId);
                var label = new PrintJob { Destination = testPrinter, LocalOnly = true, State = JobState.Ready, PublicationId = "Etiqueta de teste" };
                byte[] bytes = Encoding.ASCII.GetBytes($"^XA^FO40,40^A0N,30,30^FDZPL Print Manager - TESTE^FS^FO40,90^A0N,22,22^FD{label.Id:N}^FS^XZ");
                Directory.CreateDirectory(payloads.SpoolDirectory); label.LocalPath = Path.Combine(payloads.SpoolDirectory, $"{label.Id:N}.zpl");
                using (var output = new FileStream(label.LocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                label.Size = bytes.Length; label.Hash = Convert.ToHexString(SHA256.HashData(bytes)); store.Add(label); result = new { JobId = label.Id }; break;
            case "TestConnection": result = new { Message = "Conexão autenticada. Pastas dos mapeamentos cadastrados foram testadas para leitura e rename; sem mapeamentos, somente a conexão foi validada." }; break;
            case "TestPrinterConnectivity": result = new { Message = "Conectividade verificada sem envio de etiqueta; não comprova prontidão física." }; break;
            default: throw new ArgumentException("Comando desconhecido.");
        }
        store.Audit(actor, request.Command, entity, reason); return Protocol.Element(result);
    }
    private static void Reason(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 1000) throw new ArgumentException("Informe uma justificativa de até 1000 caracteres."); }
    public ArtifactResult CreateBackup(Guid id)
    {
        string target = Path.Combine(root, "backups", id.ToString("N"));
        string manifest = Path.Combine(target, "manifest.json");
        if (File.Exists(manifest)) return new(target);
        Directory.CreateDirectory(Path.Combine(target, "spool")); store.Backup(Path.Combine(target, "manager.db"));
        var entries = new List<object>();
        foreach (var job in store.Jobs().Where(j => j.LocalPath is not null && File.Exists(j.LocalPath)))
        {
            string name = $"{job.Id:N}.zpl"; File.Copy(job.LocalPath!, Path.Combine(target, "spool", name), true);
            using var file = File.OpenRead(Path.Combine(target, "spool", name)); string hash = Convert.ToHexString(SHA256.HashData(file));
            if (hash != job.Hash) throw new InvalidDataException("Payload divergente durante backup.");
            entries.Add(new { JobId = job.Id, File = name, Hash = hash, job.Size });
        }
        var supplemental = new List<object>();
        foreach (string folder in new[] { "quarantine", "secrets" })
        {
            string source = Path.Combine(root, folder); if (!Directory.Exists(source)) continue;
            Directory.CreateDirectory(Path.Combine(target, folder));
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file); File.Copy(file, Path.Combine(target, folder, name), true);
                using var input = File.OpenRead(file); supplemental.Add(new { Folder = folder, File = name, Hash = Convert.ToHexString(SHA256.HashData(input)) });
            }
        }
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, CreatedUtc = DateTimeOffset.UtcNow, Machine = Environment.MachineName, Payloads = entries, Supplemental = supplemental }, Protocol.Json);
        using (var output = new FileStream(manifest + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(manifestBytes); output.Flush(true); }
        File.Move(manifest + ".tmp", manifest, true);
        return new(target);
    }
    private ArtifactResult RestoreBackup(RestoreAction action, Guid commandId)
    {
        Reason(action.Reason);
        if (!Guid.TryParseExact(action.BackupName, "N", out var backupId)) throw new ArgumentException("Informe o nome UUID da pasta de backup.");
        string source = Path.Combine(root, "backups", backupId.ToString("N"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "manifest.json")));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Backup incompatível.");
        var entries = manifest.RootElement.GetProperty("payloads").EnumerateArray().ToArray();
        foreach (var entry in entries)
        {
            Guid jobId = entry.GetProperty("jobId").GetGuid(); string expected = jobId.ToString("N") + ".zpl";
            if (entry.GetProperty("file").GetString() != expected) throw new InvalidDataException("Nome de payload inválido no manifesto.");
            using var file = File.OpenRead(Path.Combine(source, "spool", expected));
            if (file.Length != entry.GetProperty("size").GetInt64() || Convert.ToHexString(SHA256.HashData(file)) != entry.GetProperty("hash").GetString()) throw new InvalidDataException("Payload de backup corrompido.");
        }
        if (manifest.RootElement.TryGetProperty("supplemental", out var additional))
        {
            foreach (var entry in additional.EnumerateArray())
            {
                string folder = entry.GetProperty("folder").GetString()!; string name = entry.GetProperty("file").GetString()!;
                if (folder is not ("quarantine" or "secrets") || Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/')) throw new InvalidDataException("Caminho adicional inválido no backup.");
                string input = Path.Combine(source, folder, name); using (var file = File.OpenRead(input)) if (Convert.ToHexString(SHA256.HashData(file)) != entry.GetProperty("hash").GetString()) throw new InvalidDataException("Arquivo adicional corrompido.");
                Directory.CreateDirectory(Path.Combine(root, folder)); string destination = Path.Combine(root, folder, name);
                if (!File.Exists(destination)) File.Copy(input, destination);
            }
        }
        var paused = store.Configuration().Settings; int revision = paused.Revision++; paused.Paused = true; store.SaveConfig("settings", "global", revision, paused);
        // The pre-restore snapshot preserves all evidence and command IDs for recovery.
        ArtifactResult before = CreateBackup(commandId);
        string staging = Path.Combine(before.Path, "restore-staging.db");
        File.Copy(Path.Combine(source, "manager.db"), staging, true);
        using (var merged = new SqliteStore(staging))
        {
            foreach (var current in store.Jobs())
            {
                if (merged.Find(current.Id) is null) merged.Add(current); else merged.Save(current);
            }
            merged.PreserveCommandHistory(store);
            foreach (var job in merged.Jobs())
            {
                if (!JobPolicy.Terminal(job.State)) { job.State = JobState.Uncertain; job.Error = "Restauração: reconciliar FTP/spooler e autorizar resolução manual antes de novo envio."; }
                string restoredPayload = Path.Combine(source, "spool", $"{job.Id:N}.zpl");
                string local = Path.Combine(payloads.SpoolDirectory, $"{job.Id:N}.zpl");
                if (!File.Exists(local) && File.Exists(restoredPayload)) File.Copy(restoredPayload, local);
                job.LocalPath = File.Exists(local) ? local : null; merged.Save(job);
            }
            var settings = merged.Configuration().Settings; int r = settings.Revision++; settings.Paused = true; merged.SaveConfig("settings", "global", r, settings);
            merged.Audit("service", "RestoreBackup", backupId.ToString(), action.Reason);
        }
        // SQLite BackupDatabase replaces the live database transactionally after staging is complete.
        store.RestoreFrom(staging);
        return before;
    }
    private ArtifactResult ExportDiagnostics(Guid id)
    {
        string path = Path.Combine(root, "backups", $"diagnostics-{id:N}.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = typeof(Administration).Assembly.GetName().Version?.ToString(), Utc = DateTimeOffset.UtcNow, HeartbeatUtc, FreeBytes = payloads.FreeBytes, Counts = store.Jobs().GroupBy(j => j.State).ToDictionary(g => g.Key.ToString(), g => g.Count()), Pending = store.Jobs().Where(j => !JobPolicy.Terminal(j.State)).Select(j => new { j.Id, j.State, j.Evidence, j.BytesSent, j.CreatedUtc }) }, Protocol.Json));
        return new(path);
    }
    public string HistoryCsv()
    {
        static string Cell(string? value) { value ??= ""; if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        var output = new StringBuilder("Id;Arquivo;Impressora;Estado;Evidência;CriadoUTC\n");
        foreach (var job in store.Jobs().TakeLast(1000)) output.AppendLine(string.Join(';', new[] { job.Id.ToString(), job.PublicationId, job.Destination.Name, Protocol.StateName(job.State), Protocol.EvidenceName(job.Evidence), job.CreatedUtc.ToString("O") }.Select(Cell)));
        return output.ToString();
    }
    public void Retain()
    {
        var settings = store.Configuration().Settings; store.RetainDetails(DateTimeOffset.UtcNow.AddDays(-settings.HistoryRetentionDays)); var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.PayloadRetentionDays);
        foreach (var job in store.Jobs().Where(j => JobPolicy.Terminal(j.State) && j.UpdatedUtc < cutoff && j.LocalPath is not null))
        {
            if (File.Exists(job.LocalPath)) File.Delete(job.LocalPath); job.LocalPath = null; store.Save(job);
        }
    }
}
