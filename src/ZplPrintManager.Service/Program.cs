using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZplPrintManager.Contracts;
using ZplPrintManager.Core;
using ZplPrintManager.Infrastructure;
using ZplPrintManager.Infrastructure.Windows;
using ZplPrintManager.Service;

if (args.Contains("--raw-helper") || args.Contains("--raw-probe")) return RawHelper.Run(args.Contains("--raw-probe"));
string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZPL Print Manager");
Directory.CreateDirectory(root);
// File sharing exclusion remains valid across asynchronous worker threads.
using var ownership = new FileStream(Path.Combine(root, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
foreach (string name in new[] { "data", "secrets", "spool", "quarantine", "logs", "backups" }) Directory.CreateDirectory(Path.Combine(root, name));
var store = new SqliteStore(Path.Combine(root, "data", "manager.db"));
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ZplPrintManager");
builder.Logging.AddProvider(new RotatingFileLogger(Path.Combine(root, "logs"), () => store.Configuration().Settings));
builder.Logging.AddEventLog(options => { options.SourceName = "ZplPrintManager"; options.LogName = "Application"; });
builder.Services.Configure<HostOptions>(options => { options.ShutdownTimeout = TimeSpan.FromSeconds(30); options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost; });
builder.Services.AddSingleton(store);
var dpapi = new DpapiSecrets(Path.Combine(root, "secrets")); dpapi.SelfTest();
builder.Services.AddSingleton<ISecretStore>(new SecretsAdapter(dpapi));
builder.Services.AddSingleton<IRemoteFactory, FtpRemoteFactory>();
builder.Services.AddSingleton<IPrinterTransport>(new PrinterRouter(new TcpPrinter(), new RawPrinter(Environment.ProcessPath!)));
builder.Services.AddSingleton(new PayloadFiles(Path.Combine(root, "spool"), Path.Combine(root, "quarantine")));
builder.Services.AddSingleton<ProcessingEngine>();
builder.Services.AddSingleton(sp => new Administration(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<IRemoteFactory>(), sp.GetRequiredService<IPrinterTransport>(), sp.GetRequiredService<PayloadFiles>(), root) { Engine = sp.GetRequiredService<ProcessingEngine>() });
builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<PipeWorker>();
try { await builder.Build().RunAsync(); return Environment.ExitCode; }
catch { Environment.ExitCode = 1; throw; }

internal sealed class SecretsAdapter(DpapiSecrets inner) : ISecretStore
{
    public string Read(Guid id) => inner.Read(id);
    public void Write(Guid id, string password) => inner.Write(id, password);
}
internal sealed class PrinterRouter(IPrinterTransport tcp, IPrinterTransport raw) : IPrinterTransport
{
    private IPrinterTransport Select(PrinterSettings settings) => settings.Kind == PrinterKind.Tcp ? tcp : raw;
    public Task TestAsync(PrinterSettings settings, CancellationToken token) => Select(settings).TestAsync(settings, token);
    public Task<SendReceipt> SendAsync(PrintJob job, Func<int, Task> record, CancellationToken token) => Select(job.Destination).SendAsync(job, record, token);
}
