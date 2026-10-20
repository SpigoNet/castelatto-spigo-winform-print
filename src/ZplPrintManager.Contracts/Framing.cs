using System.Buffers.Binary;
using System.Text.Json;
namespace ZplPrintManager.Contracts;

public static class Framing
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        if (payload.Length > Protocol.MaximumMessageBytes) throw new InvalidDataException("Mensagem excede o limite.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > Protocol.MaximumMessageBytes) throw new InvalidDataException("Tamanho de mensagem inválido.");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, Protocol.Json) ?? throw new InvalidDataException("Mensagem inválida.");
    }
}
