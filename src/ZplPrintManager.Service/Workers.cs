using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
using ZplPrintManager.Infrastructure;
using ZplPrintManager.Infrastructure.Windows;
namespace ZplPrintManager.Service;

public sealed class ProcessingWorker(ProcessingEngine engine, SqliteStore store, Administration admin, ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            engine.Gate = admin.Gate; engine.CurrentConfiguration = store.Configuration;
            engine.Recover();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            DateTimeOffset nextRetention = DateTimeOffset.MinValue;
            do
            {
                admin.HeartbeatUtc = DateTimeOffset.UtcNow;
                try
                {
                    await engine.TickAsync(store.Configuration(), stoppingToken);
                    if (DateTimeOffset.UtcNow >= nextRetention)
                    {
                        await admin.Gate.WaitAsync(stoppingToken);
                        try { admin.Retain(); nextRetention = DateTimeOffset.UtcNow.AddHours(1); }
                        finally { admin.Gate.Release(); }
                    }
                    admin.OperationalError = null;
                }
                catch (StoreException) { throw; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (IOException ex) { admin.OperationalError = "Processamento suspenso por falha de disco/rede: " + ex.GetType().Name; logger.LogWarning("Ciclo suspenso: {Type}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Environment.ExitCode = 1; logger.LogCritical("Falha fatal no processamento: {Type}", ex.GetType().Name); throw; }
    }
}
public sealed class PipeWorker(Administration admin, ILogger<PipeWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ListenAsync(stoppingToken)));
    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = PipeSecurityPolicy.CreateServer();
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(10));
                // Impersonation is synchronous and ends before executing any operation as the service.
                var request = await Framing.ReadAsync<Request>(pipe, timeout.Token);
                var (actor, role) = PipeSecurityPolicy.Identify(pipe);
                Response response;
                try
                {
                    var result = await admin.ExecuteAsync(request, actor, role, timeout.Token);
                    response = new(Protocol.Version, request.CorrelationId, true, result, null);
                }
                catch (StoreException) { Environment.ExitCode = 1; throw; }
                catch (Exception ex)
                {
                    string error = ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException ? ex.Message : $"Operação não concluída ({ex.GetType().Name}). Verifique serviço, permissões e conectividade.";
                    response = new(Protocol.Version, request.CorrelationId, false, Protocol.Element(new { }), error);
                }
                await Framing.WriteAsync(pipe, response, timeout.Token);
            }
            catch (StoreException) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Cliente IPC desconectado ou rejeitado: {Type}", ex.GetType().Name); }
        }
    }
}
