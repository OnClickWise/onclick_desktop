using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>Painel nativo de registo de ponto — funciona sem ligação. As
/// marcações vão para a fila local e são sincronizadas quando a rede volta.</summary>
public sealed class OfflinePunchPanel : UserControl
{
    private readonly LocalStore _store;
    private readonly SyncService _sync;

    private readonly Label _clock = new() { Font = new Font("Segoe UI", 30f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(29, 78, 216) };
    private readonly Label _date = new() { Font = new Font("Segoe UI", 11f), AutoSize = true, ForeColor = Color.Gray };
    private const string DefaultHint = "Modo offline — as marcações são guardadas e sincronizadas quando a internet voltar.";
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Color.Gray, Text = DefaultHint };

    private readonly Label _cardEntrada = Value();
    private readonly Label _cardSaida = Value();
    private readonly Label _cardTrab = Value();
    private readonly Label _cardEstado = Value();

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
    };

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    public OfflinePunchPanel(LocalStore store, SyncService sync)
    {
        _store = store;
        _sync = sync;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        Build();

        _sync.Changed += _ => UI(RefreshGrid);
        _sync.DayUpdated += d => UI(() => RenderCard(d));
        _tick.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            _clock.Text = now.ToString("HH:mm:ss");
            _date.Text = now.ToString("dddd, dd 'de' MMMM 'de' yyyy");
        };
        _tick.Start();
        RefreshGrid();
    }

    private void Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        top.Controls.Add(_clock);
        top.Controls.Add(_date);
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(_hint, 0, 1);

        var btns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Height = 92, Margin = new Padding(0, 12, 0, 8) };
        for (int i = 0; i < 4; i++) btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        btns.Controls.Add(Btn("Entrada", PunchKind.In, Color.FromArgb(21, 128, 61)), 0, 0);
        btns.Controls.Add(Btn("Saída p/ almoço", PunchKind.BreakStart, Color.FromArgb(180, 83, 9)), 1, 0);
        btns.Controls.Add(Btn("Retorno do almoço", PunchKind.BreakEnd, Color.FromArgb(180, 83, 9)), 2, 0);
        btns.Controls.Add(Btn("Saída (fim jornada)", PunchKind.Out, Color.FromArgb(185, 28, 28)), 3, 0);
        root.Controls.Add(btns, 0, 2);

        var card = new GroupBox { Text = "Hoje", Dock = DockStyle.Fill, Padding = new Padding(10), AutoSize = true };
        var cg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, AutoSize = true };
        void Cell(string k, Label v)
        {
            cg.Controls.Add(new Label { Text = k, ForeColor = Color.Gray, AutoSize = true, Anchor = AnchorStyles.Left });
            cg.Controls.Add(v);
        }
        Cell("Entrada", _cardEntrada); Cell("Saída", _cardSaida); Cell("Trabalhado", _cardTrab); Cell("Estado", _cardEstado);
        card.Controls.Add(cg);
        root.Controls.Add(card, 0, 3);

        var gb = new GroupBox { Text = "Marcações do posto", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _grid.Columns.Add("hora", "Hora");
        _grid.Columns.Add("tipo", "Tipo");
        _grid.Columns.Add("estado", "Sincronização");
        _grid.Columns.Add("detalhe", "Detalhe");
        _grid.Columns[0].FillWeight = 18;
        _grid.Columns[1].FillWeight = 26;
        _grid.Columns[2].FillWeight = 20;
        _grid.Columns[3].FillWeight = 36;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        var menu = new ContextMenuStrip();
        var retry = new ToolStripMenuItem("Repetir sincronização");
        retry.Click += (_, _) => RetrySelected();
        menu.Items.Add(retry);
        _grid.ContextMenuStrip = menu;
        gb.Controls.Add(_grid);
        root.Controls.Add(gb, 0, 4);

        Controls.Add(root);
    }

    private static Label Value() => new() { Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = true, Text = "—", Margin = new Padding(2, 0, 18, 0) };

    private Button Btn(string text, PunchKind kind, Color color)
    {
        var b = new Button
        {
            Text = text, Dock = DockStyle.Fill, Margin = new Padding(4),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = color,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => { _sync.Punch(kind); RefreshGrid(); };
        return b;
    }

    private void UI(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    private void RenderCard(PunchDay d)
    {
        string hm(int? m) => m is null ? "—" : $"{m / 60}h{m % 60:00}";
        _cardEntrada.Text = d.Entrada ?? "—";
        _cardSaida.Text = d.Saida ?? "—";
        _cardTrab.Text = hm(d.Trabalhado);
        _cardEstado.Text = d.State switch
        {
            "working" => "A trabalhar",
            "on_break" => "Em intervalo",
            "finished" => "Concluiu",
            _ => "Sem registo",
        };
    }

    /// <summary>Mostra um aviso contextual no topo do painel (ou repõe o texto
    /// padrão quando <paramref name="text"/> é <c>null</c>).</summary>
    public void ShowNotice(string? text)
    {
        var t = string.IsNullOrWhiteSpace(text) ? DefaultHint : text!;
        if (_hint.Text == t) return;
        _hint.Text = t;
        _hint.ForeColor = string.IsNullOrWhiteSpace(text) ? Color.Gray : Color.FromArgb(180, 83, 9);
    }

    public void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var p in _store.GetRecent(60))
        {
            var (label, color) = p.State switch
            {
                SyncState.Synced => ("Sincronizada", Color.FromArgb(21, 128, 61)),
                SyncState.Failed => ("Erro", Color.FromArgb(185, 28, 28)),
                _ => ("Pendente", Color.FromArgb(180, 83, 9)),
            };
            var idx = _grid.Rows.Add(
                p.AtLocal.ToString("dd/MM HH:mm:ss"),
                p.Kind.Label(),
                label,
                p.LastError ?? (p.State == SyncState.Synced ? "OK" : "aguarda ligação"));
            _grid.Rows[idx].Cells[2].Style.ForeColor = color;
            _grid.Rows[idx].Tag = p.Id;
        }
    }

    private void RetrySelected()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.Tag is Guid id) _store.Retry(id);
        RefreshGrid();
        _ = _sync.SyncNowAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
