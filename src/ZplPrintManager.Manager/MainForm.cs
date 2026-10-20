using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Manager;

public sealed class MainForm : Form
{
    private readonly ServiceClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(1050, 0), Padding = new(16) };
    private readonly DataGridView connections = Grid(), printers = Grid(), mappings = Grid(), jobs = Grid();
    private readonly TextBox filter = new() { Width = 220, PlaceholderText = "Arquivo, impressora, estado..." };
    private readonly DateTimePicker since = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddDays(-7) };
    private readonly DateTimePicker until = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddDays(1) };
    private Configuration config = new([], [], [], new());
    private JobSummary[] history = [];
    private bool refreshing;
    public MainForm()
    {
        Text = "ZPL Print Manager"; Width = 1180; Height = 790; MinimumSize = new(900, 650); StartPosition = FormStartPosition.CenterScreen; Font = new("Segoe UI", 10);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var dashboard = new TabPage("Painel"); dashboard.Controls.Add(status);
        var top = Bar(); top.Controls.Add(Button("Atualizar", RefreshAsync)); top.Controls.Add(Button("Pausar tudo", () => PauseAsync("global", Guid.Empty, true))); top.Controls.Add(Button("Retomar", () => PauseAsync("global", Guid.Empty, false))); top.Controls.Add(Button("Assistente inicial", SetupAsync)); top.Controls.Add(Button("Iniciar serviço", StartServiceAsync));
        dashboard.Controls.Add(top); status.Dock = DockStyle.Fill;
        tabs.TabPages.Add(dashboard);
        tabs.TabPages.Add(ConfigTab("Conexões", connections, () => new ConnectionSettings(), "SaveConnection", () => TestAsync(connections, "TestConnection")));
        tabs.TabPages.Add(ConfigTab("Impressoras", printers, () => new PrinterSettings(), "SavePrinter", () => TestAsync(printers, "TestPrinterConnectivity"), "printer"));
        tabs.TabPages.Add(ConfigTab("Mapeamentos", mappings, () => new MappingSettings(), "SaveMapping", null, "mapping"));
        var historyTab = new TabPage("Histórico"); var historyBar = Bar();
        historyBar.Height = 90; historyBar.WrapContents = true;
        foreach (var control in new Control[] { filter, new Label { Text = "De", AutoSize = true }, since, new Label { Text = "Até", AutoSize = true }, until, Button("Detalhes", DetailAsync), Button("Confirmar impressão", () => ResolveAsync("confirm")), Button("Descartar", () => ResolveAsync("discard")), Button("Repetir arquivamento", () => ResolveAsync("retryArchive")), Button("Reimprimir", ReprintAsync), Button("Exportar CSV", ExportCsvAsync) }) historyBar.Controls.Add(control);
        historyTab.Controls.Add(jobs); historyTab.Controls.Add(historyBar); tabs.TabPages.Add(historyTab);
        var admin = new TabPage("Administração"); var adminBar = Bar(); adminBar.Height = 110; adminBar.WrapContents = true;
        adminBar.Controls.Add(Button("Configurações gerais", () => EditAsync(config.Settings, "SaveSettings")));
        adminBar.Controls.Add(Button("Criar backup", () => ArtifactAsync("CreateBackup")));
        adminBar.Controls.Add(Button("Restaurar backup", RestoreAsync));
        adminBar.Controls.Add(Button("Exportar diagnóstico", () => ArtifactAsync("ExportDiagnostics")));
        adminBar.Controls.Add(Button("Etiqueta de teste", TestLabelAsync));
        admin.Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new(16, 125, 16, 16), Text = "Backup e diagnóstico são gravados pelo serviço em ProgramData\\ZPL Print Manager\\backups.\nAcesso aos arquivos é restrito a administradores.\n\nTransmissão TCP ou aceitação RAW não comprova impressão física.\nAs ações são autorizadas pelo serviço conforme seu grupo Windows.\n\nO histórico mostra até 1.000 trabalhos recentes. Detalhes exibem datas UTC; os filtros usam horário local.", AutoSize = false }); admin.Controls.Add(adminBar); tabs.TabPages.Add(admin);
        Controls.Add(tabs);
        filter.TextChanged += (_, _) => FilterHistory(); since.ValueChanged += (_, _) => FilterHistory(); until.ValueChanged += (_, _) => FilterHistory();
        Shown += async (_, _) => await RunAsync(RefreshAsync); timer.Tick += async (_, _) => await RunAsync(RefreshAsync, false); timer.Start();
        FormClosed += (_, _) => { timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BackgroundColor = SystemColors.Window, RowHeadersVisible = false };
    private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Top, Height = 55, Padding = new(8) };
    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Text = label, AutoSize = true, Height = 32 }; button.Click += async (_, _) => { button.Enabled = false; try { await RunAsync(action); } finally { button.Enabled = true; } }; return button;
    }
    private async Task RunAsync(Func<Task> action, bool alert = true)
    {
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { status.Text = "Serviço indisponível ou operação recusada. Verifique a instalação, seu grupo Windows e o estado do serviço.\n" + ex.Message; if (alert) MessageBox.Show(this, ex.Message, "ZPL Print Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private TabPage ConfigTab(string title, DataGridView grid, Func<object> create, string save, Func<Task>? test, string? scope = null)
    {
        var page = new TabPage(title); var bar = Bar(); bar.Controls.Add(Button("Novo", () => EditAsync(create(), save))); bar.Controls.Add(Button("Editar", () => EditAsync(Selected(grid), save)));
        if (test is not null) bar.Controls.Add(Button("Testar conexão", test));
        if (scope is not null) { bar.Controls.Add(Button("Pausar", () => PauseAsync(scope, Id(grid), true))); bar.Controls.Add(Button("Retomar", () => PauseAsync(scope, Id(grid), false))); }
        page.Controls.Add(grid); page.Controls.Add(bar); return page;
    }
    private static object Selected(DataGridView grid) => grid.CurrentRow?.DataBoundItem ?? throw new InvalidOperationException("Selecione um registro.");
    private static Guid Id(DataGridView grid) => (Guid)Selected(grid).GetType().GetProperty("Id")!.GetValue(Selected(grid))!;
    private async Task RefreshAsync()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            var snapshot = await client.GetAsync<StatusSnapshot>("GetStatus", new { }, lifetime.Token);
            config = await client.GetAsync<Configuration>("GetConfiguration", new { }, lifetime.Token);
            history = await client.GetAsync<JobSummary[]>("ListJobs", new { }, lifetime.Token);
            status.Text = $"{(snapshot.Paused ? "PROCESSAMENTO PAUSADO" : "SERVIÇO EM OPERAÇÃO")}\n\nÚltima atividade: {snapshot.HeartbeatUtc.ToLocalTime():g}  •  Perfil: {snapshot.Role}\nEspaço livre: {snapshot.FreeBytes / 1024d / 1024 / 1024:F1} GiB\n\n" + string.Join("\n", snapshot.Counts.Select(k => $"{k.Key}: {k.Value}")) + "\n\nÚltimas consultas FTP:\n" + string.Join("\n", snapshot.LastPollUtc.Select(k => $"{config.Connections.FirstOrDefault(c => c.Id == k.Key)?.Name}: {k.Value.ToLocalTime():g}")) + "\n\n" + string.Join("\n", snapshot.Alerts) + "\n\nArquivado após transmissão não significa impressão física confirmada.";
            Bind(connections, config.Connections); Bind(printers, config.Printers); Bind(mappings, config.Mappings); FilterHistory();
            if (connections.Columns.Contains("Password")) connections.Columns["Password"]!.Visible = false;
        }
        finally { refreshing = false; }
    }
    private static void Bind<T>(DataGridView grid, T[] values)
    {
        Guid? id = grid.CurrentRow?.DataBoundItem is { } item ? (Guid?)item.GetType().GetProperty("Id")?.GetValue(item) : null;
        grid.DataSource = values;
        if (id.HasValue) foreach (DataGridViewRow row in grid.Rows) if ((Guid?)row.DataBoundItem?.GetType().GetProperty("Id")?.GetValue(row.DataBoundItem) == id) { grid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(c => c.Visible); break; }
    }
    private void FilterHistory() => Bind(jobs, history.Where(j => j.CreatedUtc.LocalDateTime >= since.Value.Date && j.CreatedUtc.LocalDateTime < until.Value.Date.AddDays(1) && $"{j.File} {j.Printer} {j.State} {j.Id}".Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray());
    private async Task EditAsync(object value, string command)
    {
        object copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(value, Protocol.Json), value.GetType(), Protocol.Json)!;
        using var editor = new EditorForm(copy, config, async v => { await client.CallAsync(command, v, lifetime.Token); }); editor.ShowDialog(this); await RefreshAsync();
    }
    private async Task TestAsync(DataGridView grid, string command) { var result = await client.CallAsync(command, Id(grid), lifetime.Token); MessageBox.Show(this, result.GetProperty("message").GetString(), "Resultado do teste"); }
    private async Task PauseAsync(string scope, Guid id, bool paused) { await client.CallAsync(paused ? "Pause" : "Resume", new PauseAction(scope, id, paused), lifetime.Token); await RefreshAsync(); }
    private async Task SetupAsync()
    {
        MessageBox.Show(this, "1. Cadastre a conexão FTP/FTPS.\n2. Cadastre a impressora e teste a conectividade.\n3. Crie o mapeamento, confira os caminhos e ative-o.\n4. Envie uma etiqueta de teste quando autorizado.\n5. Retome o processamento no painel.\n\nO produtor deve publicar por rename de .uploading para .zpl com nomes únicos.", "Configuração inicial");
        await EditAsync(new ConnectionSettings(), "SaveConnection");
        if (config.Connections.Length == 0) return;
        await EditAsync(new PrinterSettings(), "SavePrinter");
        if (config.Printers.Length == 0) return;
        await EditAsync(new MappingSettings { ConnectionId = config.Connections.Last().Id, PrinterId = config.Printers.Last().Id }, "SaveMapping");
    }
    private Task StartServiceAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", "start ZplPrintManager") { UseShellExecute = true, Verb = "runas" }); return Task.CompletedTask;
    }
    private async Task DetailAsync()
    {
        var value = await client.CallAsync("GetJob", Id(jobs), lifetime.Token);
        using var form = new Form { Text = "Detalhes e tentativas — datas UTC", Width = 900, Height = 700, StartPosition = FormStartPosition.CenterParent };
        form.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10), Text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) }); form.ShowDialog(this);
    }
    private string? Reason(string title)
    {
        using var form = new Form { Text = title, Width = 600, Height = 240, StartPosition = FormStartPosition.CenterParent }; var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, MaxLength = 1000 }; var ok = new Button { Text = "Confirmar", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        form.Controls.Add(text); form.Controls.Add(new Label { Text = "Informe a justificativa. A ação registra sua identidade Windows.", Dock = DockStyle.Top, Height = 40 }); form.Controls.Add(ok);
        return form.ShowDialog(this) == DialogResult.OK ? text.Text : null;
    }
    private async Task ResolveAsync(string action) { Guid id = Id(jobs); string? reason = Reason(action == "confirm" ? "Confirmar impressão observada" : "Resolver trabalho"); if (reason is null) return; await client.CallAsync("ResolveJob", new JobAction(id, action, reason), lifetime.Token); await RefreshAsync(); }
    private async Task ReprintAsync() { Guid id = Id(jobs); string? reason = Reason("Reimpressão — pode gerar etiquetas duplicadas"); if (reason is null) return; await client.CallAsync("Reprint", new JobAction(id, "reprint", reason), lifetime.Token); await RefreshAsync(); }
    private async Task TestLabelAsync() { Guid id = Id(printers); string? reason = Reason("Etiqueta de teste — consome mídia na impressora selecionada"); if (reason is null) return; await client.CallAsync("PrintTestLabel", new TestLabelAction(id, reason), lifetime.Token); MessageBox.Show(this, "Etiqueta adicionada à fila. Se o processamento estiver pausado, retome pelo painel."); await RefreshAsync(); }
    private async Task RestoreAsync()
    {
        string? name = Reason("Cole o nome UUID da pasta de backup (32 caracteres)"); if (name is null) return;
        string? reason = Reason("Restauração: o serviço ficará pausado e pendências exigirão reconciliação"); if (reason is null) return;
        var result = await client.GetAsync<ArtifactResult>("RestoreBackup", new RestoreAction(name.Trim(), reason), lifetime.Token);
        MessageBox.Show(this, "Backup restaurado. Processamento pausado; reconcilie pendências e confira credenciais. Estado anterior preservado em:\n" + result.Path); await RefreshAsync();
    }
    private async Task ArtifactAsync(string command) { var result = await client.GetAsync<ArtifactResult>(command, new { }, lifetime.Token); MessageBox.Show(this, "Arquivo criado pelo serviço:\n" + result.Path, "Operação concluída"); }
    private async Task ExportCsvAsync() { using var dialog = new SaveFileDialog { Filter = "CSV UTF-8|*.csv", FileName = "historico-zpl.csv" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; string csv = await client.GetAsync<string>("ExportHistory", new { }, lifetime.Token); await File.WriteAllTextAsync(dialog.FileName, csv, new UTF8Encoding(true), lifetime.Token); }
}
