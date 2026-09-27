using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>Autenticação do posto. Só pede <b>e-mail</b> e <b>palavra-passe</b> —
/// a instituição (slug) é resolvida pelo servidor a partir do e-mail e o
/// endereço do servidor vem da configuração (editável em "Definições
/// avançadas"). Sem ligação, se existir uma sessão em cache, permite continuar
/// offline.</summary>
public sealed class LoginForm : Form
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private static readonly Color Ink = Color.FromArgb(17, 17, 17);
    private static readonly Color Muted = Color.FromArgb(113, 113, 122);
    private static readonly Color Line = Color.FromArgb(212, 212, 216);
    private static readonly Color LineFocus = Color.FromArgb(17, 17, 17);
    private static readonly Color Surface = Color.White;

    private readonly AppConfig _cfg;
    private readonly ApiClient _api;
    private readonly LocalStore _store;

    private readonly TextBox _email = Input("nome@instituicao.com");
    private readonly TextBox _pass = Input(null, password: true);
    private readonly TextBox _server = Input("https://servidor.exemplo.com");
    private readonly Button _submit = new()
    {
        Text = "Entrar", Height = 46, Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Ink,
        Font = new Font("Segoe UI Semibold", 11f), Cursor = Cursors.Hand,
    };
    private readonly LinkLabel _offline = new()
    {
        Text = "Continuar offline com a última sessão", AutoSize = true, Visible = false,
        LinkColor = Ink, ActiveLinkColor = Ink,
    };
    private readonly Label _msg = new()
    {
        AutoSize = true, MaximumSize = new Size(332, 0), ForeColor = Color.FromArgb(185, 28, 28),
        Font = new Font("Segoe UI", 9f),
    };
    private readonly LinkLabel _advToggle = new()
    {
        Text = "Definições avançadas", AutoSize = true, LinkColor = Muted, ActiveLinkColor = Ink,
        Font = new Font("Segoe UI", 8.5f),
    };
    private readonly Label _advLabel = FieldLabel("SERVIDOR");
    private readonly Panel _advHost;

    public Session? Session { get; private set; }

    public LoginForm(AppConfig cfg, ApiClient api, LocalStore store)
    {
        _cfg = cfg;
        _api = api;
        _store = store;

        Text = "WiseAcademy — Entrar";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Surface;
        ClientSize = new Size(424, 500);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        _server.Text = cfg.ApiBaseUrl;
        _advHost = FieldHost(_server);
        _advLabel.Visible = _advHost.Visible = false;

        _submit.FlatAppearance.BorderSize = 0;
        _submit.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 38, 38);
        _submit.FlatAppearance.MouseDownBackColor = Color.Black;
        _submit.SizeChanged += (_, _) => RoundRegion(_submit, 10);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(46, 42, 46, 24),
            BackColor = Surface,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(Control c, int top = 0, int bottom = 0)
        {
            c.Margin = new Padding(0, top, 0, bottom);
            if (c is not Label { AutoSize: true } and not LinkLabel) c.Dock = DockStyle.Fill;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }

        Row(new Label
        {
            Text = "WiseAcademy", Font = new Font("Segoe UI", 19f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true,
        });
        Row(new Label
        {
            Text = "Posto de trabalho — entre para continuar", ForeColor = Muted,
            AutoSize = true, Font = new Font("Segoe UI", 9.75f),
        }, top: 2, bottom: 20);

        Row(FieldLabel("E-MAIL"), bottom: 5);
        Row(FieldHost(_email));
        Row(FieldLabel("PALAVRA-PASSE"), top: 12, bottom: 5);
        Row(FieldHost(_pass));

        Row(_submit, top: 18);
        Row(_offline, top: 12);
        Row(_msg, top: 8);

        Row(_advLabel, top: 12, bottom: 5);
        Row(_advHost);

        // empurra o toggle para o fundo
        var filler = new Panel { Dock = DockStyle.Fill };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(filler);

        Row(_advToggle);

        Controls.Add(root);

        AcceptButton = _submit;
        _submit.Click += async (_, _) => await DoLoginAsync();
        _offline.LinkClicked += (_, _) => ContinueOffline();
        _advToggle.LinkClicked += (_, _) =>
        {
            var show = !_advHost.Visible;
            _advLabel.Visible = _advHost.Visible = show;
            _advToggle.Text = show ? "Ocultar definições avançadas" : "Definições avançadas";
        };

        _offline.Visible = _store.GetSession() is not null;
        Shown += (_, _) => _email.Focus();
    }

    /* ── construção de campos ────────────────────────────────────── */

    private static TextBox Input(string? cue, bool password = false)
    {
        var tb = new TextBox
        {
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11.25f),
            Dock = DockStyle.Fill, UseSystemPasswordChar = password,
        };
        if (!string.IsNullOrEmpty(cue))
            tb.HandleCreated += (_, _) => SendMessage(tb.Handle, EM_SETCUEBANNER, (IntPtr)1, cue);
        return tb;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted,
        Font = new Font("Segoe UI", 8f, FontStyle.Bold),
    };

    /// <summary>Envolve um <see cref="TextBox"/> numa "caixa" com moldura fina,
    /// cantos suaves e realce ao focar.</summary>
    private static Panel FieldHost(TextBox tb)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, Height = 46, BackColor = Surface,
            Padding = new Padding(14, 13, 14, 9),
        };
        var border = Line;
        host.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(border, 1.4f);
            using var path = RoundedRect(new Rectangle(0, 0, host.Width - 1, host.Height - 1), 10);
            e.Graphics.DrawPath(p, path);
        };
        tb.Enter += (_, _) => { border = LineFocus; host.Invalidate(); };
        tb.Leave += (_, _) => { border = Line; host.Invalidate(); };
        host.Controls.Add(tb);
        return host;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void RoundRegion(Control c, int radius)
    {
        if (c.Width <= 0 || c.Height <= 0) return;
        using var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
        c.Region = new Region(path);
    }

    /* ── ações ───────────────────────────────────────────────────── */

    private async Task DoLoginAsync()
    {
        _msg.Text = "";
        if (_email.Text.Trim().Length == 0 || _pass.Text.Length == 0)
        {
            _msg.Text = "Preencha o e-mail e a palavra-passe.";
            return;
        }

        SetBusy(true);
        try
        {
            _cfg.ApiBaseUrl = string.IsNullOrWhiteSpace(_server.Text) ? _cfg.ApiBaseUrl : _server.Text.Trim();

            // slug resolvido pelo servidor a partir do e-mail (retrocompat: usa o
            // último slug conhecido como atalho, se houver).
            var s = await _api.LoginAsync(_email.Text.Trim(), _pass.Text, _cfg.TenantSlug);
            _store.SaveSession(s);
            _cfg.TenantSlug = s.TenantSlug ?? _cfg.TenantSlug;
            AppSettings.Save(_cfg);
            Session = s;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ApiUnavailableException)
        {
            _msg.Text = _store.GetSession() is not null
                ? "Sem ligação ao servidor. Pode continuar offline com a última sessão."
                : "Sem ligação ao servidor. A primeira autenticação precisa de internet.";
            _offline.Visible = _store.GetSession() is not null;
        }
        catch (Exception ex)
        {
            _msg.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void ContinueOffline()
    {
        Session = _store.GetSession();
        if (Session is null) { _msg.Text = "Não há sessão guardada."; return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool b)
    {
        _submit.Enabled = !b;
        _submit.Text = b ? "A entrar…" : "Entrar";
        _email.Enabled = _pass.Enabled = !b;
        UseWaitCursor = b;
    }
}
