using Microsoft.Data.Sqlite;
using System.Text.Json;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
namespace ZplPrintManager.Infrastructure;

public sealed class SqliteStore : IJobStore, IDisposable
{
    private readonly object sync = new();
    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;
    public string DatabasePath { get; }
    public SqliteStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        long version = Convert.ToInt64(Scalar("PRAGMA user_version;"));
        if (version > 1) throw new InvalidDataException("Banco de versão superior; rollback incompatível.");
        Atomic(() =>
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS SchemaVersions(Version INTEGER PRIMARY KEY, AppliedUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Config(Kind TEXT NOT NULL, Id TEXT NOT NULL, Revision INTEGER NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Kind,Id));
                CREATE TABLE IF NOT EXISTS Jobs(Sequence INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE, State TEXT NOT NULL, NextAttemptUtc TEXT, UpdatedUtc TEXT NOT NULL, Json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Jobs_State ON Jobs(State,NextAttemptUtc);
                CREATE TABLE IF NOT EXISTS ProcessedIdentities(ConnectionId TEXT NOT NULL, MappingId TEXT NOT NULL, PublicationId TEXT NOT NULL, JobId TEXT NOT NULL REFERENCES Jobs(Id), PRIMARY KEY(ConnectionId,MappingId,PublicationId));
                CREATE TABLE IF NOT EXISTS Attempts(Id TEXT PRIMARY KEY, JobId TEXT NOT NULL REFERENCES Jobs(Id), Stage TEXT NOT NULL, StartedUtc TEXT NOT NULL, FinishedUtc TEXT, Result TEXT, Bytes INTEGER, Error TEXT, SpoolerId INTEGER);
                CREATE TABLE IF NOT EXISTS RemoteOperations(JobId TEXT NOT NULL REFERENCES Jobs(Id), Source TEXT NOT NULL, Destination TEXT NOT NULL, Result TEXT NOT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY(JobId,Source,Destination));
                CREATE TABLE IF NOT EXISTS AuditEvents(Id INTEGER PRIMARY KEY AUTOINCREMENT, Utc TEXT NOT NULL, Actor TEXT NOT NULL, Action TEXT NOT NULL, Entity TEXT NOT NULL, Reason TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Commands(Id TEXT PRIMARY KEY, Actor TEXT NOT NULL, Fingerprint TEXT NOT NULL, Response TEXT NOT NULL);
                INSERT OR IGNORE INTO SchemaVersions VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                PRAGMA user_version=1;
                """);
            return 0;
        });
        if (Convert.ToString(Scalar("PRAGMA quick_check;")) != "ok") throw new InvalidDataException("Integridade SQLite inválida; processamento bloqueado.");
    }
    private SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params (string, object?)[] args)
    {
        try { using var command = Command(sql, args); return command.ExecuteNonQuery(); }
        catch (SqliteException ex) { throw new StoreException("Falha de persistência; processamento deve parar.", ex); }
    }
    private object? Scalar(string sql, params (string, object?)[] args)
    {
        try { using var command = Command(sql, args); return command.ExecuteScalar(); }
        catch (SqliteException ex) { throw new StoreException("Falha de leitura SQLite.", ex); }
    }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (sync)
        {
            try { using var command = Command(sql, args); using var reader = command.ExecuteReader(); var result = new List<T>(); while (reader.Read()) result.Add(map(reader)); return result; }
            catch (SqliteException ex) { throw new StoreException("Falha de leitura SQLite.", ex); }
        }
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Protocol.Json);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Protocol.Json)!;
    public T Atomic<T>(Func<T> action)
    {
        lock (sync)
        {
            if (transaction is not null) return action();
            using var tx = connection.BeginTransaction(); transaction = tx;
            try { T result = action(); tx.Commit(); return result; }
            finally { transaction = null; }
        }
    }
    public IReadOnlyList<PrintJob> Jobs() => Query("SELECT Sequence,Json FROM Jobs ORDER BY Sequence", r => { var j = Read<PrintJob>(r.GetString(1)); j.Sequence = r.GetInt64(0); return j; });
    public PrintJob? Find(Guid id) => Query("SELECT Sequence,Json FROM Jobs WHERE Id=$id", r => { var j = Read<PrintJob>(r.GetString(1)); j.Sequence = r.GetInt64(0); return j; }, ("$id", id.ToString())).FirstOrDefault();
    public bool Add(PrintJob job) => Atomic(() =>
    {
        if (!job.LocalOnly && Scalar("SELECT JobId FROM ProcessedIdentities WHERE ConnectionId=$c AND MappingId=$m AND PublicationId=$p", ("$c", job.ConnectionId.ToString()), ("$m", job.MappingId.ToString()), ("$p", job.PublicationId)) is not null) return false;
        Execute("INSERT INTO Jobs(Id,State,UpdatedUtc,Json) VALUES($id,$s,$u,$j)", ("$id", job.Id.ToString()), ("$s", job.State.ToString()), ("$u", job.UpdatedUtc.ToString("O")), ("$j", Json(job)));
        job.Sequence = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        if (!job.LocalOnly) Execute("INSERT INTO ProcessedIdentities VALUES($c,$m,$p,$id)", ("$c", job.ConnectionId.ToString()), ("$m", job.MappingId.ToString()), ("$p", job.PublicationId), ("$id", job.Id.ToString()));
        return true;
    });
    public void Save(PrintJob job)
    {
        lock (sync)
        {
            string? previous = Convert.ToString(Scalar("SELECT State FROM Jobs WHERE Id=$id", ("$id", job.Id.ToString())));
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            if (Execute("UPDATE Jobs SET State=$s,NextAttemptUtc=$n,UpdatedUtc=$u,Json=$j WHERE Id=$id", ("$s", job.State.ToString()), ("$n", job.NextAttemptUtc?.ToString("O")), ("$u", job.UpdatedUtc.ToString("O")), ("$j", Json(job)), ("$id", job.Id.ToString())) != 1) throw new InvalidOperationException("Trabalho não encontrado.");
            if (previous != job.State.ToString()) Audit("service", "Transition", job.Id.ToString(), $"{previous} -> {job.State}");
        }
    }
    public Guid BeginAttempt(PrintJob job, JobState stage)
    {
        lock (sync) { var id = Guid.NewGuid(); Execute("INSERT INTO Attempts(Id,JobId,Stage,StartedUtc) VALUES($id,$job,$s,$u)", ("$id", id.ToString()), ("$job", job.Id.ToString()), ("$s", stage.ToString()), ("$u", DateTimeOffset.UtcNow.ToString("O"))); return id; }
    }
    public void EndAttempt(Guid attempt, string result, long bytes, string? error, int? spoolerId = null)
    {
        lock (sync) Execute("UPDATE Attempts SET FinishedUtc=$u,Result=$r,Bytes=$b,Error=$e,SpoolerId=$s WHERE Id=$id", ("$u", DateTimeOffset.UtcNow.ToString("O")), ("$r", result), ("$b", bytes), ("$e", error), ("$s", spoolerId), ("$id", attempt.ToString()));
    }
    public void RecordMove(PrintJob job, string source, string destination, string result)
    {
        lock (sync) Execute("INSERT INTO RemoteOperations VALUES($id,$s,$d,$r,$u) ON CONFLICT(JobId,Source,Destination) DO UPDATE SET Result=$r,UpdatedUtc=$u", ("$id", job.Id.ToString()), ("$s", source), ("$d", destination), ("$r", result), ("$u", DateTimeOffset.UtcNow.ToString("O")));
    }
    public void Audit(string actor, string action, string entity, string reason)
    {
        lock (sync) Execute("INSERT INTO AuditEvents(Utc,Actor,Action,Entity,Reason) VALUES($u,$a,$c,$e,$r)", ("$u", DateTimeOffset.UtcNow.ToString("O")), ("$a", actor), ("$c", action), ("$e", entity), ("$r", reason));
    }
    public T[] ListConfig<T>(string kind) => Query("SELECT Json FROM Config WHERE Kind=$k", r => Read<T>(r.GetString(0)), ("$k", kind)).ToArray();
    public Configuration Configuration() => new(ListConfig<ConnectionSettings>("connection"), ListConfig<PrinterSettings>("printer"), ListConfig<MappingSettings>("mapping"), ListConfig<GlobalSettings>("settings").FirstOrDefault() ?? new());
    public void SaveConfig<T>(string kind, string id, int revision, T value)
    {
        lock (sync)
        {
            var old = Scalar("SELECT Revision FROM Config WHERE Kind=$k AND Id=$id", ("$k", kind), ("$id", id));
            if ((old is null && revision != 0) || (old is not null && Convert.ToInt32(old) != revision)) throw new InvalidOperationException("Configuração alterada por outro usuário; atualize antes de salvar.");
            Execute("INSERT INTO Config VALUES($k,$id,$r,$j) ON CONFLICT(Kind,Id) DO UPDATE SET Revision=$r,Json=$j", ("$k", kind), ("$id", id), ("$r", revision + 1), ("$j", Json(value)));
        }
    }
    public JsonElement? CachedCommand(Guid id, string actor, string fingerprint)
    {
        var rows = Query("SELECT Actor,Fingerprint,Response FROM Commands WHERE Id=$id", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$id", id.ToString()));
        if (rows.Count == 0) return null;
        if (rows[0].Item1 != actor || rows[0].Item2 != fingerprint) throw new InvalidOperationException("ID de idempotência reutilizado com comando/identidade diferente.");
        return Read<JsonElement>(rows[0].Item3);
    }
    public void CacheCommand(Guid id, string actor, string fingerprint, JsonElement response)
    {
        lock (sync) Execute("INSERT INTO Commands VALUES($id,$a,$f,$r)", ("$id", id.ToString()), ("$a", actor), ("$f", fingerprint), ("$r", Json(response)));
    }
    public object Detail(Guid id) => new { Job = Find(id), Attempts = Query("SELECT Stage,StartedUtc,FinishedUtc,Result,Bytes,Error,SpoolerId FROM Attempts WHERE JobId=$id", r => Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)), ("$id", id.ToString())), Events = Query("SELECT Utc,Actor,Action,Reason FROM AuditEvents WHERE Entity=$id ORDER BY Id", r => Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.GetValue(i)), ("$id", id.ToString())), Moves = Query("SELECT Source,Destination,Result,UpdatedUtc FROM RemoteOperations WHERE JobId=$id", r => Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.GetValue(i)), ("$id", id.ToString())) };
    public void Backup(string target)
    {
        lock (sync) { using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()); destination.Open(); connection.BackupDatabase(destination); }
    }
    public void RestoreFrom(string source)
    {
        lock (sync)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            backup.Open();
            using var check = backup.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (Convert.ToString(check.ExecuteScalar()) != "ok") throw new InvalidDataException("Backup corrompido.");
            check.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(check.ExecuteScalar()) != 1) throw new InvalidDataException("Versão de backup incompatível.");
            backup.BackupDatabase(connection);
        }
    }
    public void PreserveCommandHistory(SqliteStore current)
    {
        var rows = current.Query("SELECT Id,Actor,Fingerprint,Response FROM Commands", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Atomic(() => { foreach (var row in rows) Execute("INSERT OR REPLACE INTO Commands VALUES($id,$a,$f,$r)", ("$id", row.Item1), ("$a", row.Item2), ("$f", row.Item3), ("$r", row.Item4)); return 0; });
    }
    public void RetainDetails(DateTimeOffset cutoff)
    {
        lock (sync) Execute("DELETE FROM Attempts WHERE FinishedUtc IS NOT NULL AND FinishedUtc < $cutoff AND JobId IN (SELECT Id FROM Jobs WHERE State IN ('Archived','Discarded'));", ("$cutoff", cutoff.ToString("O")));
    }
    public void Dispose() => connection.Dispose();
}
