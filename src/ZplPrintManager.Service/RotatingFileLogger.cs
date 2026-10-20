using Microsoft.Extensions.Logging;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Service;

public sealed class RotatingFileLogger(string directory, Func<GlobalSettings> settings) : ILoggerProvider
{
    private readonly object sync = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }
    private void Write(string category, LogLevel level, string message)
    {
        lock (sync)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"service-{DateTime.UtcNow:yyyyMMdd}.log");
            if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024) File.Move(path, Path.Combine(directory, $"service-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log"));
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{level}] {category}: {message}{Environment.NewLine}");
            var retention = settings();
            var logs = new DirectoryInfo(directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToArray(); long total = 0;
            foreach (var file in logs) { total += file.Length; if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-retention.LogRetentionDays) || total > retention.MaximumLogBytes) file.Delete(); }
        }
    }
    private sealed class Logger(RotatingFileLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { if (IsEnabled(level)) owner.Write(category, level, formatter(state, exception)); }
    }
}
