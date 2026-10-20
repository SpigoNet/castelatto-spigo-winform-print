using FluentFTP;
using System.Net.Security;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure;

public interface ISecretStore { string Read(Guid id); void Write(Guid id, string password); }
public sealed class FtpRemoteFactory(ISecretStore secrets) : IRemoteFactory
{
    public async Task<IRemoteSession> ConnectAsync(ConnectionSettings connection, CancellationToken token)
    {
        var client = new AsyncFtpClient(connection.Host, connection.User, secrets.Read(connection.Id), connection.Port);
        client.Config.EncryptionMode = connection.Tls switch { TlsMode.Explicit => FtpEncryptionMode.Explicit, TlsMode.Implicit => FtpEncryptionMode.Implicit, _ => FtpEncryptionMode.None };
        client.Config.DataConnectionEncryption = true;
        client.Config.DataConnectionType = connection.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.ConnectTimeout = connection.ConnectTimeoutSeconds * 1000;
        client.Config.ReadTimeout = connection.ReadTimeoutSeconds * 1000;
        client.Config.DataConnectionConnectTimeout = connection.ConnectTimeoutSeconds * 1000;
        client.Config.DataConnectionReadTimeout = connection.ReadTimeoutSeconds * 1000;
        client.Config.ValidateAnyCertificate = false;
        client.ValidateCertificate += (_, e) => e.Accept = e.PolicyErrors == SslPolicyErrors.None;
        try { await client.Connect(token); return new FtpRemoteSession(client); }
        catch (Exception ex) when (ex is FluentFTP.Exceptions.FtpAuthenticationException or System.Security.Authentication.AuthenticationException) { client.Dispose(); throw new PermanentConnectionException("Credencial/certificado inválido; conexão suspensa.", ex); }
        catch { client.Dispose(); throw; }
    }
}
public sealed class FtpRemoteSession(AsyncFtpClient client) : IRemoteSession
{
    private static RemoteFile ConvertFile(FtpListItem item) => new(item.FullName, item.Size, item.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc)));
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken token) => (await client.GetListing(path, token)).Where(i => i.Type == FtpObjectType.File).Select(ConvertFile).ToArray();
    public async Task<RemoteFile?> StatAsync(string path, CancellationToken token)
    {
        var item = await client.GetObjectInfo(path, true, token); return item is null ? null : ConvertFile(item);
    }
    public async Task MoveAsync(string source, string destination, CancellationToken token)
    {
        string directory = destination[..destination.LastIndexOf('/')];
        await client.CreateDirectory(directory, token);
        if (!await client.MoveFile(source, destination, FtpRemoteExists.Skip, token)) throw new IOException("Rename remoto não concluído; destino existente ou sem permissão.");
    }
    public async Task<Stream> OpenReadAsync(string path, CancellationToken token) => await client.OpenRead(path, FtpDataType.Binary, 0, true, token);
    public async Task ProbeAsync(string directory, CancellationToken token)
    {
        string source = RemotePaths.Combine(directory, $".zpl-probe-{Guid.NewGuid():N}.tmp"); string destination = source + ".renamed";
        byte[] expected = "ZPL Print Manager: teste de acesso, sem impressão."u8.ToArray();
        try
        {
            if (await client.UploadBytes(expected, source, FtpRemoteExists.Skip, false, token: token) != FtpStatus.Success) throw new IOException("Falha ao criar arquivo exclusivo de teste.");
            await using (var input = await OpenReadAsync(source, token))
            {
                using var output = new MemoryStream(); await input.CopyToAsync(output, token);
                if (!output.ToArray().SequenceEqual(expected)) throw new InvalidDataException("Leitura de teste divergente.");
            }
            await MoveAsync(source, destination, token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await client.FileExists(source, cleanup.Token)) await client.DeleteFile(source, cleanup.Token);
            if (await client.FileExists(destination, cleanup.Token)) await client.DeleteFile(destination, cleanup.Token);
        }
    }
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}
