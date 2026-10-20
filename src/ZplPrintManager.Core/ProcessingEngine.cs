using System.Collections.Concurrent;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Core;

public sealed class ProcessingEngine(IJobStore store, IRemoteFactory remoteFactory, IPrinterTransport printer, PayloadFiles files)
{
    public ProcessingGate Gate { get; set; } = new();
    public Func<Configuration>? CurrentConfiguration { get; set; }
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lastPoll = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> nextMappingPoll = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> nextPoll = new();
    private readonly ConcurrentDictionary<Guid, string> connectionAlerts = new();
    private readonly ConcurrentDictionary<Guid, int> suspendedConnections = new();
    private readonly ConcurrentDictionary<Guid, int> connectionFailures = new();
    public IReadOnlyDictionary<Guid, DateTimeOffset> LastPoll => lastPoll;
    public string[] Alerts => connectionAlerts.Values.ToArray();

    public void Recover()
    {
        foreach (var job in store.Jobs())
        {
            var recovered = JobPolicy.Recover(job.State);
            if (recovered == job.State) continue;
            job.State = recovered;
            job.Error = recovered == JobState.Uncertain ? "Serviço interrompido durante envio; resolução manual obrigatória." : null;
            store.Save(job);
        }
    }

    public async Task TickAsync(Configuration config, CancellationToken token)
    {
        if (config.Settings.Paused) return;
        if (files.FreeBytes < config.Settings.MinimumFreeBytes + config.Settings.MaximumFileBytes * 2)
            throw new IOException("Espaço livre insuficiente; ingestão e envio suspensos.");
        foreach (var candidate in config.Connections.Where(c => c.Enabled))
        {
            using var operation = await Gate.EnterOperationAsync(token);
            config = CurrentConfiguration?.Invoke() ?? config;
            var connection = config.Connections.FirstOrDefault(c => c.Id == candidate.Id);
            if (config.Settings.Paused) return;
            if (connection is null || !connection.Enabled) continue;
            if (suspendedConnections.TryGetValue(connection.Id, out int revision) && revision == connection.Revision) continue;
            if (nextPoll.TryGetValue(connection.Id, out var next) && next > DateTimeOffset.UtcNow) continue;
            var mappings = config.Mappings.Where(m => m.ConnectionId == connection.Id && m.Enabled && !m.Paused).ToArray();
            if (mappings.Length == 0) continue;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(connection.OperationTimeoutSeconds));
                await using var remote = await remoteFactory.ConnectAsync(connection, deadline.Token);
                foreach (var mapping in mappings)
                {
                    if (nextMappingPoll.TryGetValue(mapping.Id, out var mappingNext) && mappingNext > DateTimeOffset.UtcNow) continue;
                    var destination = config.Printers.Single(p => p.Id == mapping.PrinterId);
                    if (!destination.Enabled || destination.Paused) continue;
                    if (store.Jobs().Count(j => !JobPolicy.Terminal(j.State)) >= config.Settings.MaximumPendingJobs) break;
                    await DiscoverAsync(remote, connection, mapping, destination, config.Settings, deadline.Token);
                    nextMappingPoll[mapping.Id] = DateTimeOffset.UtcNow.AddSeconds(mapping.PollSeconds);
                }
                lastPoll[connection.Id] = DateTimeOffset.UtcNow;
                connectionFailures[connection.Id] = 0;
                connectionAlerts.TryRemove(connection.Id, out _);
                nextPoll[connection.Id] = DateTimeOffset.UtcNow.AddSeconds(mappings.Min(m => m.PollSeconds));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not StoreException)
            {
                if (ex is PermanentConnectionException) suspendedConnections[connection.Id] = connection.Revision;
                int failures = connectionFailures.AddOrUpdate(connection.Id, 1, (_, n) => n + 1);
                nextPoll[connection.Id] = DateTimeOffset.UtcNow + JobPolicy.Backoff(failures);
                connectionAlerts[connection.Id] = $"{connection.Name}: conexão indisponível ({ex.GetType().Name}). Verifique credenciais, certificado e rede.";
            }
        }
        var groups = store.Jobs().Where(j => !JobPolicy.Terminal(j.State)).GroupBy(j => j.Destination.PhysicalId).ToArray();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token }, async (group, ct) =>
        {
            bool blocked = group.Any(JobPolicy.BlocksPrinter);
            foreach (var snapshot in group.OrderBy(j => j.Sequence))
            {
                using var operation = await Gate.EnterOperationAsync(ct);
                var liveConfig = CurrentConfiguration?.Invoke() ?? config;
                if (liveConfig.Settings.Paused) return;
                var job = store.Find(snapshot.Id);
                if (job is null || JobPolicy.Terminal(job.State)) continue;
                ct.ThrowIfCancellationRequested();
                if (job.State == JobState.Quarantine && job.QuarantineMovePending)
                {
                    var quarantineConnection = job.ConnectionSnapshot ?? liveConfig.Connections.FirstOrDefault(c => c.Id == job.ConnectionId);
                    if (quarantineConnection is not null) await QuarantineRemoteAsync(job, quarantineConnection, liveConfig.Settings, ct);
                }
                if (job.State is JobState.Uncertain or JobState.Quarantine or JobState.WaitingStability or JobState.Discovered) continue;
                var currentPrinter = liveConfig.Printers.FirstOrDefault(p => p.Id == job.Destination.Id);
                var currentMapping = liveConfig.Mappings.FirstOrDefault(m => m.Id == job.MappingId);
                if (currentPrinter is null || !currentPrinter.Enabled || currentPrinter.Paused) continue;
                if (!job.LocalOnly && (currentMapping is null || !currentMapping.Enabled || currentMapping.Paused)) continue;
                bool archiveOnly = job.State is JobState.Transmitted or JobState.ArchivePending;
                if (blocked && !archiveOnly && !(job.State == JobState.WaitingRetry && job.RetryStage == JobState.Ready && !group.Any(j => j.Id != job.Id && JobPolicy.BlocksPrinter(j)))) continue;
                var connection = liveConfig.Connections.FirstOrDefault(c => c.Id == job.ConnectionId);
                if (!job.LocalOnly && (connection is null || !connection.Enabled)) continue;
                if (connection is not null && suspendedConnections.TryGetValue(connection.Id, out int revision) && revision == connection.Revision) continue;
                await ProcessAsync(job, job.ConnectionSnapshot ?? connection, liveConfig.Settings, ct);
                if (JobPolicy.BlocksPrinter(job)) blocked = true;
            }
        });
    }

    private async Task DiscoverAsync(IRemoteSession remote, ConnectionSettings connection, MappingSettings mapping, PrinterSettings destination, GlobalSettings settings, CancellationToken token)
    {
        var known = store.Jobs();
        int pending = known.Count(j => !JobPolicy.Terminal(j.State));
        var identities = known.Where(j => j.ConnectionId == connection.Id && j.MappingId == mapping.Id && j.ReprintOf is null).ToDictionary(j => j.PublicationId, StringComparer.Ordinal);
        foreach (var file in await remote.ListAsync(mapping.InputPath, token))
        {
            string name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(mapping.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (RemotePaths.Combine(mapping.InputPath, name) != file.Path) continue;
            var existing = identities.GetValueOrDefault(name);
            if (existing is not null)
            {
                if (existing.State is JobState.WaitingStability or JobState.Discovered)
                    Observe(existing, file);
                else if (existing.Hash is not null && existing.State != JobState.Quarantine)
                {
                    await using var stream = await remote.OpenReadAsync(file.Path, token);
                    if (await PayloadFiles.HashRemoteAsync(stream, settings.MaximumFileBytes, token) != existing.Hash)
                    {
                        existing.State = JobState.Quarantine;
                        existing.Error = "Identidade de publicação reutilizada com conteúdo divergente; intervenção necessária.";
                        store.Save(existing);
                    }
                }
                continue;
            }
            if (pending >= settings.MaximumPendingJobs) continue;
            var job = new PrintJob
            {
                ConnectionId = connection.Id,
                ConnectionSnapshot = connection,
                MappingId = mapping.Id,
                PublicationId = name,
                OriginalPath = file.Path,
                Size = file.Size,
                RemoteModifiedUtc = file.ModifiedUtc,
                Mapping = mapping,
                Destination = destination,
                State = mapping.Publication == PublicationMode.StableMetadata ? JobState.WaitingStability : JobState.AcquisitionPending,
                StableObservations = 1
            };
            job.ProcessingPath = $"{mapping.ProcessingPath.TrimEnd('/')}/{mapping.Id:N}/{job.Id:N}.zpl";
            job.ArchivePath = $"{mapping.ArchivePath.TrimEnd('/')}/{mapping.Id:N}/{job.CreatedUtc:yyyy/MM/dd}/{job.Id:N}.zpl";
            if (file.Size <= 0 || file.Size > settings.MaximumFileBytes)
            { job.State = JobState.Quarantine; job.QuarantineMovePending = true; job.Error = "Tamanho inválido para ingestão."; }
            if (store.Add(job)) { pending++; identities[name] = job; }
        }
    }
    private void Observe(PrintJob job, RemoteFile file)
    {
        if (job.Size != file.Size || job.RemoteModifiedUtc != file.ModifiedUtc)
        {
            job.Size = file.Size; job.RemoteModifiedUtc = file.ModifiedUtc;
            job.StableObservations = 1; job.StableSinceUtc = DateTimeOffset.UtcNow;
        }
        else job.StableObservations++;
        if (job.StableObservations >= 3 && DateTimeOffset.UtcNow - job.StableSinceUtc >= TimeSpan.FromSeconds(10)) job.State = JobState.AcquisitionPending;
        store.Save(job);
    }
    public async Task ProcessAsync(PrintJob job, ConnectionSettings? connection, GlobalSettings settings, CancellationToken token)
    {
        if (job.State == JobState.WaitingRetry)
        {
            if (job.NextAttemptUtc > DateTimeOffset.UtcNow) return;
            job.State = job.RetryStage ?? throw new InvalidDataException("Etapa de retry ausente.");
            store.Save(job);
        }
        JobState stage = job.State;
        Guid? attempt = null;
        try
        {
            if (job.State is JobState.AcquisitionPending or JobState.Acquired or JobState.Downloading)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(connection!.OperationTimeoutSeconds));
                await using var remote = await remoteFactory.ConnectAsync(connection, deadline.Token);
                if (job.State == JobState.AcquisitionPending)
                {
                    await ReconcileMoveAsync(remote, job, job.OriginalPath, job.ProcessingPath, false, settings, deadline.Token);
                    job.State = JobState.Acquired; store.Save(job);
                }
                stage = JobState.Acquired;
                attempt = store.BeginAttempt(job, stage);
                job.State = JobState.Downloading; store.Save(job);
                var before = await remote.StatAsync(job.ProcessingPath, deadline.Token) ?? throw new ReconciliationException("Origem adquirida ausente.");
                if (before.Size != job.Size || before.ModifiedUtc != job.RemoteModifiedUtc) throw new InvalidDataException("Publicação mudou após aquisição.");
                Payload payload;
                await using (var stream = await remote.OpenReadAsync(job.ProcessingPath, deadline.Token))
                    payload = await files.DownloadAsync(job.Id, stream, job.Size, settings.MaximumFileBytes, job.Mapping.ValidateZpl, deadline.Token);
                var after = await remote.StatAsync(job.ProcessingPath, deadline.Token);
                if (after != before) throw new InvalidDataException("Arquivo remoto mudou durante download.");
                job.LocalPath = payload.Path; job.Hash = payload.Hash; job.Size = payload.Size;
                job.State = JobState.Ready; job.Error = null; store.Save(job);
                store.EndAttempt(attempt.Value, "Concluído", job.Size, null); attempt = null;
            }
            if (job.State == JobState.Ready)
            {
                var current = CurrentConfiguration?.Invoke();
                if (current is not null && (current.Settings.Paused || current.Printers.Any(p => p.Id == job.Destination.Id && (!p.Enabled || p.Paused)) || current.Mappings.Any(m => m.Id == job.MappingId && (!m.Enabled || m.Paused)))) return;
                if (files.FreeBytes < settings.MinimumFreeBytes) throw new IOException("Disco sem reserva para registrar envio.");
                stage = JobState.Ready;
                await PayloadFiles.VerifyAsync(job, token);
                attempt = store.BeginAttempt(job, stage);
                job.State = JobState.Sending; store.Save(job);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.SendTimeoutSeconds));
                SendReceipt receipt = await printer.SendAsync(job, id => { job.SpoolerJobId = id; store.Save(job); return Task.CompletedTask; }, deadline.Token);
                job.BytesSent = receipt.Bytes; job.SpoolerJobId = receipt.SpoolerJobId;
                job.Evidence = Evidence.TransportAccepted; job.State = JobState.Transmitted; job.Error = null; store.Save(job);
                store.EndAttempt(attempt.Value, "Transporte aceitou", receipt.Bytes, null, receipt.SpoolerJobId); attempt = null;
            }
            if (job.State is JobState.Transmitted or JobState.ArchivePending)
            {
                if (job.NextAttemptUtc > DateTimeOffset.UtcNow) return;
                stage = JobState.ArchivePending;
                job.State = JobState.ArchivePending; store.Save(job);
                if (!job.LocalOnly)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(connection!.OperationTimeoutSeconds));
                    await using var remote = await remoteFactory.ConnectAsync(connection, deadline.Token);
                    await ReconcileMoveAsync(remote, job, job.ProcessingPath, job.ArchivePath, true, settings, deadline.Token);
                }
                job.State = JobState.Archived; job.Error = null; job.NextAttemptUtc = null; store.Save(job);
            }
        }
        catch (StoreException) { throw; }
        catch (Exception ex)
        {
            bool uncertain = ex is TransmissionException { MayHavePrinted: true } || (job.State == JobState.Sending && ex is not TransmissionException { MayHavePrinted: false });
            job.BytesSent = ex is TransmissionException transmission ? transmission.Bytes : job.BytesSent;
            job.Error = ex switch
            {
                InvalidDataException or ReconciliationException or TransmissionException => ex.Message,
                _ => $"Falha na etapa {stage} ({ex.GetType().Name}); verifique conectividade e permissões."
            };
            if (ex is PermanentConnectionException && connection is not null)
            {
                suspendedConnections[connection.Id] = connection.Revision; connectionAlerts[connection.Id] = "Conexão suspensa: credencial/certificado inválido. Corrija e salve a conexão."; job.State = stage;
            }
            else if (uncertain) job.State = JobState.Uncertain;
            else if (ex is InvalidDataException or ReconciliationException)
            {
                job.State = JobState.Quarantine;
                job.QuarantineMovePending = ex is InvalidDataException && job.Evidence == Evidence.Unavailable && !job.LocalOnly;
            }
            else
            {
                int failures = job.Failures.GetValueOrDefault(stage) + 1;
                job.Failures[stage] = failures;
                if (failures >= settings.MaximumAttempts) { job.State = JobState.Quarantine; job.Error += " Limite de tentativas atingido."; }
                else
                {
                    job.State = stage == JobState.ArchivePending ? JobState.ArchivePending : JobState.WaitingRetry;
                    job.RetryStage = stage; job.NextAttemptUtc = DateTimeOffset.UtcNow + JobPolicy.Backoff(failures, Random.Shared.NextDouble());
                }
            }
            store.Save(job);
            if (attempt.HasValue) store.EndAttempt(attempt.Value, Protocol.StateName(job.State), job.BytesSent, job.Error, job.SpoolerJobId);
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
        }
    }
    private async Task QuarantineRemoteAsync(PrintJob job, ConnectionSettings connection, GlobalSettings settings, CancellationToken token)
    {
        if (job.NextAttemptUtc > DateTimeOffset.UtcNow || job.Failures.GetValueOrDefault(JobState.Quarantine) >= settings.MaximumAttempts) return;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(connection.OperationTimeoutSeconds));
            await using var remote = await remoteFactory.ConnectAsync(connection, deadline.Token);
            string destination = $"{job.Mapping.ErrorPath.TrimEnd('/')}/{job.MappingId:N}/{job.Id:N}.zpl";
            var processing = await remote.StatAsync(job.ProcessingPath, deadline.Token);
            var original = await remote.StatAsync(job.OriginalPath, deadline.Token);
            if (processing is not null && original is not null) throw new ReconciliationException("Origens conflitantes na quarentena.");
            string source = processing is not null ? job.ProcessingPath : job.OriginalPath;
            await ReconcileMoveAsync(remote, job, source, destination, false, settings, deadline.Token);
            job.QuarantineMovePending = false; job.NextAttemptUtc = null; store.Save(job);
        }
        catch (StoreException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            int failures = job.Failures.GetValueOrDefault(JobState.Quarantine) + 1; job.Failures[JobState.Quarantine] = failures;
            job.NextAttemptUtc = DateTimeOffset.UtcNow + JobPolicy.Backoff(failures); store.Save(job);
        }
    }
    private async Task ReconcileMoveAsync(IRemoteSession remote, PrintJob job, string source, string destination, bool checkHash, GlobalSettings settings, CancellationToken token)
    {
        store.RecordMove(job, source, destination, "Intenção");
        var from = await remote.StatAsync(source, token);
        var to = await remote.StatAsync(destination, token);
        if (from is not null && to is null)
        {
            if (from.Size != job.Size) throw new ReconciliationException("Tamanho remoto divergente antes de renomear.");
            await remote.MoveAsync(source, destination, token);
            from = await remote.StatAsync(source, token); to = await remote.StatAsync(destination, token);
        }
        if (from is not null || to is null || to.Size != job.Size) throw new ReconciliationException("Movimentação ambígua: origem/destino exigem análise.");
        if (checkHash)
        {
            await using var stream = await remote.OpenReadAsync(destination, token);
            if (await PayloadFiles.HashRemoteAsync(stream, settings.MaximumFileBytes, token) != job.Hash)
                throw new ReconciliationException("Hash do arquivo remoto divergente.");
        }
        store.RecordMove(job, source, destination, "Concluído");
    }
}
public sealed class StoreException(string message, Exception inner) : IOException(message, inner);
