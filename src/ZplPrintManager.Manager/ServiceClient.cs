using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Manager;

public sealed class ServiceClient
{
    public async Task<JsonElement> CallAsync(string command, object payload, CancellationToken token = default)
    {
        var request = new Request(Protocol.Version, Guid.NewGuid(), Guid.NewGuid(), command, Protocol.Element(payload));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                await pipe.ConnectAsync(3000, token);
                await Framing.WriteAsync(pipe, request, token);
                var response = await Framing.ReadAsync<Response>(pipe, token);
                if (response.CorrelationId != request.CorrelationId || response.Version != Protocol.Version) throw new InvalidDataException("Resposta IPC inválida.");
                if (!response.Success) throw new InvalidOperationException(response.Error);
                return response.Payload;
            }
            catch (IOException) when (attempt == 0) { await Task.Delay(300, token); }
        }
    }
    public async Task<T> GetAsync<T>(string command, object payload, CancellationToken token = default) => Protocol.Read<T>(await CallAsync(command, payload, token));
}
