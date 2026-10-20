using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZplPrintManager.Contracts;

public enum TlsMode { Explicit, Implicit, None }
public enum PrinterKind { Tcp, WindowsRaw }
public enum PublicationMode { AtomicRename, StableMetadata }
public enum JobState { Discovered, WaitingStability, AcquisitionPending, Acquired, Downloading, Ready, Sending, Transmitted, ArchivePending, Archived, WaitingRetry, Uncertain, Quarantine, Discarded }
public enum Evidence { Unavailable, TransportAccepted, QueueReportedCompletion, DeviceReportedCompletion, OperatorConfirmed }
public enum Role { Reader, Operator, Administrator }

public class ConnectionSettings
{
    [ReadOnly(true)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nova conexão";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public TlsMode Tls { get; set; } = TlsMode.Explicit;
    public string User { get; set; } = "";
    [PasswordPropertyText(true), Description("Vazio preserva a senha existente. Nunca é devolvida pelo serviço.")]
    public string Password { get; set; } = "";
    public bool Passive { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ReadTimeoutSeconds { get; set; } = 30;
    public int OperationTimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    [ReadOnly(true)] public int Revision { get; set; }
}
public class PrinterSettings
{
    [ReadOnly(true)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nova impressora";
    [Description("Mesmo identificador para acessos TCP/USB ao mesmo equipamento.")]
    public string PhysicalId { get; set; } = Guid.NewGuid().ToString("N");
    public PrinterKind Kind { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9100;
    public string Queue { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    [ReadOnly(true)] public int Revision { get; set; }
}
public class MappingSettings
{
    [ReadOnly(true)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Novo mapeamento";
    public Guid ConnectionId { get; set; }
    public Guid PrinterId { get; set; }
    public string InputPath { get; set; } = "/entrada";
    public string ProcessingPath { get; set; } = "/processando";
    public string ArchivePath { get; set; } = "/impressos";
    public string ErrorPath { get; set; } = "/erros";
    public string Extension { get; set; } = ".zpl";
    public PublicationMode Publication { get; set; }
    public int PollSeconds { get; set; } = 5;
    public bool ValidateZpl { get; set; } = true;
    public bool Enabled { get; set; }
    public bool Paused { get; set; }
    [ReadOnly(true)] public int Revision { get; set; }
}
public class GlobalSettings
{
    public bool Paused { get; set; } = true;
    public long MaximumFileBytes { get; set; } = 10 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaximumPendingJobs { get; set; } = 10000;
    public int MaximumAttempts { get; set; } = 5;
    public int SendTimeoutSeconds { get; set; } = 120;
    public int PayloadRetentionDays { get; set; } = 7;
    public int HistoryRetentionDays { get; set; } = 180;
    public int LogRetentionDays { get; set; } = 30;
    public long MaximumLogBytes { get; set; } = 500L * 1024 * 1024;
    [ReadOnly(true)] public int Revision { get; set; }
}
public record Configuration(ConnectionSettings[] Connections, PrinterSettings[] Printers, MappingSettings[] Mappings, GlobalSettings Settings);
public record JobSummary(Guid Id, string File, string Printer, string State, string Evidence, DateTimeOffset CreatedUtc, string? Error, Guid? ReprintOf);
public record StatusSnapshot(DateTimeOffset HeartbeatUtc, bool Paused, long FreeBytes, Dictionary<string, int> Counts, Dictionary<Guid, DateTimeOffset> LastPollUtc, string[] Alerts, Role Role);
public record Request(int Version, Guid CorrelationId, Guid IdempotencyId, string Command, JsonElement Payload);
public record Response(int Version, Guid CorrelationId, bool Success, JsonElement Payload, string? Error);
public record JobAction(Guid JobId, string Action, string Reason);
public record PauseAction(string Scope, Guid Id, bool Paused);
public record TestLabelAction(Guid PrinterId, string Reason);
public record ArtifactResult(string Path);
public record RestoreAction(string BackupName, string Reason);
public static class Protocol
{
    public const string PipeName = "ZplPrintManager.v1";
    public const int Version = 1;
    public const int MaximumMessageBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new InvalidDataException("Mensagem vazia.");
    public static string StateName(JobState state) => state switch
    {
        JobState.Discovered => "DESCOBERTO",
        JobState.WaitingStability => "AGUARDANDO_ESTABILIDADE",
        JobState.AcquisitionPending => "AQUISICAO_PENDENTE",
        JobState.Acquired => "ADQUIRIDO",
        JobState.Downloading => "BAIXANDO",
        JobState.Ready => "PRONTO",
        JobState.Sending => "ENVIANDO",
        JobState.Transmitted => "TRANSMITIDO",
        JobState.ArchivePending => "ARQUIVAMENTO_PENDENTE",
        JobState.Archived => "ARQUIVADO",
        JobState.WaitingRetry => "AGUARDANDO_RETRY",
        JobState.Uncertain => "RESULTADO_INCERTO",
        JobState.Quarantine => "QUARENTENA",
        _ => "DESCARTADO"
    };
    public static string EvidenceName(Evidence evidence) => evidence switch
    {
        Evidence.TransportAccepted => "Transporte aceitou — impressão física não confirmada",
        Evidence.OperatorConfirmed => "Confirmado pelo operador",
        Evidence.QueueReportedCompletion => "Fila reportou conclusão — impressão física não confirmada",
        Evidence.DeviceReportedCompletion => "Dispositivo reportou conclusão",
        _ => "Não disponível"
    };
}
