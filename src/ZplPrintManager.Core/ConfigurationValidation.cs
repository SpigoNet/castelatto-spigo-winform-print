using ZplPrintManager.Contracts;
namespace ZplPrintManager.Core;

public static class ConfigurationValidation
{
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    public static void Connection(ConnectionSettings value)
    {
        Require(value.Id != Guid.Empty && !string.IsNullOrWhiteSpace(value.Name), "ID e nome obrigatórios.");
        Require(!string.IsNullOrWhiteSpace(value.Host) && !value.Host.Any(char.IsWhiteSpace) && !value.Host.Contains('/'), "Host inválido.");
        Require(value.Port is > 0 and <= 65535, "Porta inválida.");
        Require(value.ConnectTimeoutSeconds is >= 1 and <= 120 && value.ReadTimeoutSeconds is >= 1 and <= 300 && value.OperationTimeoutSeconds is >= 1 and <= 600, "Timeout inválido.");
        Require(Enum.IsDefined(value.Tls), "Modo TLS inválido.");
    }
    public static void Printer(PrinterSettings value, Configuration config)
    {
        Require(value.Id != Guid.Empty && !string.IsNullOrWhiteSpace(value.Name) && !string.IsNullOrWhiteSpace(value.PhysicalId), "ID, nome e destino físico obrigatórios.");
        Require(Enum.IsDefined(value.Kind), "Tipo de impressora inválido.");
        Require(value.Kind == PrinterKind.WindowsRaw ? !string.IsNullOrWhiteSpace(value.Queue) : !string.IsNullOrWhiteSpace(value.Host) && value.Port is > 0 and <= 65535, "Endereço/fila inválido.");
        var duplicate = config.Printers.FirstOrDefault(p => p.Id != value.Id && p.Kind == value.Kind && (value.Kind == PrinterKind.Tcp ? string.Equals(p.Host, value.Host, StringComparison.OrdinalIgnoreCase) && p.Port == value.Port : string.Equals(p.Queue, value.Queue, StringComparison.OrdinalIgnoreCase)));
        Require(duplicate is null || duplicate.PhysicalId == value.PhysicalId, "Destino já cadastrado; use o mesmo identificador físico para serializar os envios.");
    }
    public static void Mapping(MappingSettings value, Configuration config)
    {
        Require(value.Id != Guid.Empty && !string.IsNullOrWhiteSpace(value.Name), "ID e nome obrigatórios.");
        Require(config.Connections.Any(c => c.Id == value.ConnectionId), "Conexão não encontrada.");
        Require(config.Printers.Any(p => p.Id == value.PrinterId), "Impressora não encontrada.");
        value.InputPath = RemotePaths.Normalize(value.InputPath); value.ProcessingPath = RemotePaths.Normalize(value.ProcessingPath); value.ArchivePath = RemotePaths.Normalize(value.ArchivePath); value.ErrorPath = RemotePaths.Normalize(value.ErrorPath);
        string[] paths = [value.InputPath, value.ProcessingPath, value.ArchivePath, value.ErrorPath];
        for (int i = 0; i < paths.Length; i++) for (int j = i + 1; j < paths.Length; j++) Require(!RemotePaths.Overlaps(paths[i], paths[j]), "Pastas de entrada, processamento, arquivo e erro não podem se sobrepor.");
        foreach (var other in config.Mappings.Where(m => m.Id != value.Id && m.ConnectionId == value.ConnectionId))
        {
            Require(!RemotePaths.Overlaps(value.InputPath, other.InputPath), "Entrada sobreposta a outro mapeamento.");
            foreach (var destination in new[] { other.ProcessingPath, other.ArchivePath, other.ErrorPath }) Require(!RemotePaths.Overlaps(value.InputPath, destination), "Entrada sobreposta a destino de outro mapeamento.");
            foreach (var destination in paths.Skip(1)) Require(!RemotePaths.Overlaps(other.InputPath, destination), "Destino sobreposto a entrada de outro mapeamento.");
        }
        Require(value.Extension.StartsWith('.') && value.Extension.Length >= 2 && value.Extension.All(c => char.IsLetterOrDigit(c) || c == '.'), "Extensão inválida.");
        Require(value.PollSeconds is >= 1 and <= 3600, "Polling deve estar entre 1 e 3600 segundos.");
        Require(Enum.IsDefined(value.Publication), "Protocolo de publicação inválido.");
    }
    public static void Settings(GlobalSettings value)
    {
        Require(value.MaximumFileBytes is > 0 and <= 104857600, "Limite de arquivo deve ser de até 100 MiB.");
        Require(value.MinimumFreeBytes >= 0 && value.MaximumPendingJobs > 0, "Limites de capacidade inválidos.");
        Require(value.MaximumAttempts is >= 1 and <= 10 && value.SendTimeoutSeconds is >= 1 and <= 600, "Tentativas/timeout inválidos.");
        Require(value.PayloadRetentionDays >= 1 && value.HistoryRetentionDays >= 1 && value.LogRetentionDays >= 1 && value.MaximumLogBytes >= 1048576, "Retenção inválida.");
    }
}
