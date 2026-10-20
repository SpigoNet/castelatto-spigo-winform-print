using System.Security.Cryptography;
namespace ZplPrintManager.Core;

public sealed class PayloadFiles(string spoolDirectory, string quarantineDirectory)
{
    public string SpoolDirectory { get; } = Path.GetFullPath(spoolDirectory);
    public string QuarantineDirectory { get; } = Path.GetFullPath(quarantineDirectory);
    public long FreeBytes => new DriveInfo(Path.GetPathRoot(SpoolDirectory)!).AvailableFreeSpace;
    public async Task<Payload> DownloadAsync(Guid id, Stream source, long expectedSize, long maximumBytes, bool validateZpl, CancellationToken token)
    {
        Directory.CreateDirectory(SpoolDirectory);
        string partial = Path.Combine(SpoolDirectory, $"{id:N}.part");
        string final = Path.Combine(SpoolDirectory, $"{id:N}.zpl");
        try
        {
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > maximumBytes || total > expectedSize) throw new InvalidDataException("Payload excede o limite ou mudou de tamanho.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total == 0 || total != expectedSize) throw new InvalidDataException("Payload vazio ou incompleto.");
                await output.FlushAsync(token);
                output.Flush(true);
            }
            if (validateZpl)
            {
                byte[] bytes = await File.ReadAllBytesAsync(partial, token);
                if (bytes.AsSpan().IndexOf("^XA"u8) < 0 || bytes.AsSpan().IndexOf("^XZ"u8) < 0)
                    throw new InvalidDataException("Delimitadores ZPL ausentes. Validação mínima, sem garantia de segurança.");
            }
            File.Move(partial, final, true);
            return new(final, Convert.ToHexString(hash.GetHashAndReset()), total);
        }
        catch (InvalidDataException)
        {
            Directory.CreateDirectory(QuarantineDirectory);
            if (File.Exists(partial)) File.Move(partial, Path.Combine(QuarantineDirectory, $"{id:N}.invalid"), true);
            throw;
        }
        catch { File.Delete(partial); throw; }
    }
    public static async Task VerifyAsync(PrintJob job, CancellationToken token)
    {
        if (job.LocalPath is null || !File.Exists(job.LocalPath)) throw new InvalidDataException("Payload local indisponível.");
        await using var stream = File.OpenRead(job.LocalPath);
        if (stream.Length != job.Size || Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != job.Hash)
            throw new InvalidDataException("Hash/tamanho local divergente; envio bloqueado.");
    }
    public static async Task<string> HashRemoteAsync(Stream stream, long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; long total = 0; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Arquivo remoto excede o limite.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
