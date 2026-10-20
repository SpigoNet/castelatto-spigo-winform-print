using System.ComponentModel;
using System.Reflection;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Manager;

public sealed class EditorForm : Form
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Id"] = "Identificador",
        ["Name"] = "Nome",
        ["Host"] = "Servidor / IP",
        ["Port"] = "Porta",
        ["Tls"] = "Proteção da conexão",
        ["User"] = "Usuário",
        ["Password"] = "Nova senha (vazio preserva)",
        ["Passive"] = "Modo passivo",
        ["Enabled"] = "Ativo",
        ["Revision"] = "Revisão",
        ["PhysicalId"] = "Identificador do equipamento físico",
        ["Kind"] = "Transporte",
        ["Queue"] = "Nome da fila Windows",
        ["Paused"] = "Pausado",
        ["ConnectionId"] = "Conexão",
        ["PrinterId"] = "Impressora",
        ["InputPath"] = "Pasta de entrada",
        ["ProcessingPath"] = "Pasta de processamento",
        ["ArchivePath"] = "Pasta de arquivo",
        ["ErrorPath"] = "Pasta de erros",
        ["Extension"] = "Extensão",
        ["Publication"] = "Publicação",
        ["PollSeconds"] = "Intervalo de consulta (segundos)",
        ["ValidateZpl"] = "Validar delimitadores ZPL",
        ["ConnectTimeoutSeconds"] = "Timeout de conexão (segundos)",
        ["ReadTimeoutSeconds"] = "Timeout de leitura (segundos)",
        ["OperationTimeoutSeconds"] = "Prazo total FTP (segundos)",
        ["MaximumFileBytes"] = "Tamanho máximo do arquivo (bytes)",
        ["MinimumFreeBytes"] = "Reserva mínima de disco (bytes)",
        ["MaximumPendingJobs"] = "Limite de trabalhos pendentes",
        ["MaximumAttempts"] = "Máximo de tentativas",
        ["SendTimeoutSeconds"] = "Prazo de envio (segundos)",
        ["PayloadRetentionDays"] = "Retenção de payloads encerrados (dias)",
        ["HistoryRetentionDays"] = "Retenção de detalhes (dias)",
        ["LogRetentionDays"] = "Retenção de logs (dias)",
        ["MaximumLogBytes"] = "Limite total de logs (bytes)"
    };
    public EditorForm(object value, Configuration config, Func<object, Task> save)
    {
        Text = "Configuração — ZPL Print Manager"; Width = 700; Height = 740; StartPosition = FormStartPosition.CenterParent; MinimumSize = new(600, 500);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new(16) };
        panel.ColumnStyles.Add(new(SizeType.Percent, 48)); panel.ColumnStyles.Add(new(SizeType.Percent, 52));
        var getters = new Dictionary<PropertyInfo, Func<object?>>();
        foreach (var property in value.GetType().GetProperties())
        {
            int row = panel.RowCount++; panel.RowStyles.Add(new(SizeType.AutoSize));
            var label = new Label { Text = Labels.GetValueOrDefault(property.Name, property.Name), AutoSize = true, Padding = new(0, 8, 8, 8) };
            panel.Controls.Add(label, 0, row); Control editor;
            bool readOnly = property.GetCustomAttribute<ReadOnlyAttribute>()?.IsReadOnly == true;
            if (readOnly) { editor = new TextBox { Text = property.GetValue(value)?.ToString(), ReadOnly = true }; }
            else if (property.PropertyType == typeof(bool))
            {
                var check = new CheckBox { Checked = (bool)property.GetValue(value)!, AutoSize = true }; editor = check; getters[property] = () => check.Checked;
            }
            else if (property.PropertyType.IsEnum)
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues(property.PropertyType) };
                combo.SelectedItem = property.GetValue(value); editor = combo; getters[property] = () => combo.SelectedItem;
            }
            else if (property.Name is "ConnectionId" or "PrinterId")
            {
                var choices = property.Name == "ConnectionId" ? config.Connections.Select(c => new Choice(c.Id, c.Name)).ToArray() : config.Printers.Select(p => new Choice(p.Id, p.Name)).ToArray();
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", ValueMember = "Id", DataSource = choices };
                if (choices.Any(c => c.Id == (Guid)property.GetValue(value)!)) combo.SelectedValue = (Guid)property.GetValue(value)!;
                editor = combo; getters[property] = () => combo.SelectedValue ?? Guid.Empty;
            }
            else if (property.PropertyType == typeof(int) || property.PropertyType == typeof(long))
            {
                var number = new NumericUpDown { Minimum = 0, Maximum = long.MaxValue, Value = Convert.ToDecimal(property.GetValue(value)), ThousandsSeparator = true }; editor = number;
                getters[property] = () => property.PropertyType == typeof(int) ? (object)decimal.ToInt32(number.Value) : decimal.ToInt64(number.Value);
            }
            else
            {
                var text = new TextBox { Text = property.GetValue(value)?.ToString(), UseSystemPasswordChar = property.Name == "Password" }; editor = text; getters[property] = () => text.Text;
            }
            editor.Dock = DockStyle.Top; editor.Margin = new(3, 5, 3, 5); panel.Controls.Add(editor, 1, row);
        }
        var note = new Label { AutoSize = true, MaximumSize = new(600, 0), Text = "FTPS Explicit é o padrão. None transmite credenciais sem criptografia. StableMetadata oferece garantia reduzida. O mesmo equipamento TCP/USB deve compartilhar o identificador físico. Ao ativar um mapeamento, o serviço testa as pastas e o rename." };
        int noteRow = panel.RowCount++; panel.Controls.Add(note, 0, noteRow); panel.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new(8) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Salvar", AutoSize = true };
        ok.Click += async (_, _) =>
        {
            ok.Enabled = false;
            try
            {
                foreach (var (property, get) in getters) property.SetValue(value, get());
                if (value is ConnectionSettings { Tls: TlsMode.None } && MessageBox.Show(this, "FTP sem TLS envia senha e etiquetas sem criptografia. Continuar?", "Conexão sem proteção", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                if (value is MappingSettings mapping && MessageBox.Show(this, $"Entrada: {mapping.InputPath}\nProcessamento: {mapping.ProcessingPath}/{mapping.Id:N}/<trabalho>.zpl\nArquivo: {mapping.ArchivePath}/{mapping.Id:N}/AAAA/MM/DD/<trabalho>.zpl\nErros: {mapping.ErrorPath}\n\nSalvar este mapeamento?", "Caminhos do mapeamento", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                await save(value); DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { ok.Enabled = true; }
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); Controls.Add(panel); Controls.Add(buttons); CancelButton = cancel;
    }
    private sealed record Choice(Guid Id, string Name);
}
