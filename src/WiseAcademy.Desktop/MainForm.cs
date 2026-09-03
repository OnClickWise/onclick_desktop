using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>
/// Casca do posto. Com ligação mostra a <b>aplicação web completa</b> (WebView2,
/// autenticada automaticamente com a sessão local). Sem ligação, mostra o
/// painel nativo de registo de ponto; quando a internet volta, a fila local
/// é sincronizada e a aplicação web é recarregada.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ApiClient _api;
    private readonly LocalStore _store;
    private readonly SyncService _sync;
    private readonly ConnectivityMonitor _net;
    private Session _session;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly OfflinePunchPanel _offline;
    private bool _webReady;
    private bool _forceOffline;

    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripLabel _dot = new("● …");
    private readonly ToolStripButton _tabWeb = new("Aplicação web") { CheckOnClick = false };
    private readonly ToolStripButton _tabOffline = new("Posto offline") { CheckOnClick = false };
    private readonly ToolStripButton _syncBtn = new("Sincronizar agora");
    private readonly ToolStripLabel _pending = new();
    private readonly ToolStripLabel _who = new() { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _logout = new("Terminar sessão") { Alignment = ToolStripItemAlignment.Right };

    public MainForm(AppConfig cfg, ApiClient api, LocalStore store, SyncService sync, ConnectivityMonitor net, Session session)
    {
        _cfg = cfg; _api = api; _store = store; _sync = sync; _net = net; _session = session;
        _offline = new OfflinePunchPanel(store, sync) { Dock = DockStyle.Fill, Visible = true };

        Text = "WiseAcademy — Posto";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(900, 620);
        WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9.5f);

        _bar.Items.AddRange([
            _dot, new ToolStripSeparator(),
            _tabWeb, _tabOffline, new ToolStripSeparator(),
            _syncBtn, _pending,
            _logout, _who,
        ]);
        Controls.Add(_web);
        Controls.Add(_offline);
        Controls.Add(_bar);

        _who.Text = $"{session.UserName}  ·  {session.TenantName ?? session.TenantSlug ?? ""}";

        _tabWeb.Click += (_, _) => { _forceOffline = false; ApplyView(); };
        _tabOffline.Click += (_, _) => { _forceOffline = true; ApplyView(); };
        _syncBtn.Click += (_, _) => _ = _sync.SyncNowAsync();
        _logout.Click += (_, _) => Logout();

        _net.StatusChanged += _ => UI(ApplyView);
        _sync.Changed += s => UI(() => RenderStatus(s));
        _sync.SessionExpired += () => UI(ReAuth);

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        RenderStatus(_sync.Snapshot());
        try
        {
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WiseAcademy", "Desktop", "WebView2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udf);
            await _web.EnsureCoreWebView2Async(env);
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SeedScript());
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webReady = true;
        }
        catch (Exception ex)
        {
            _webReady = false;
            _tabWeb.Enabled = false;
            _dot.ToolTipText = "WebView2 indisponível: " + ex.Message;
        }

        await _net.PokeAsync();
        ApplyView();
        _ = _sync.SyncNowAsync();
    }

    /// <summary>Semeia o localStorage com a sessão local para autenticar a web
    /// sem novo login. Corre antes dos scripts de cada página.</summary>
    private string SeedScript()
    {
        static string J(string? raw) => string.IsNullOrWhiteSpace(raw) ? "null" : raw;
        var userJson = J(_session.UserJson);
        var tenantJson = J(_session.TenantJson);
        var brandingJson = J(_session.BrandingJson);
        var token = JsonSerializer.Serialize(_session.Token);
        return $$"""
            (function () {
              try {
                var t = {{token}};
                if (t) localStorage.setItem('wa_admin_token', t);
                var u = {{userJson}};   if (u) localStorage.setItem('wa_admin_user', JSON.stringify(u));
                var tn = {{tenantJson}}; if (tn) localStorage.setItem('wa_admin_tenant', JSON.stringify(tn));
                var b = {{brandingJson}}; if (b) localStorage.setItem('wa_branding', JSON.stringify(b));
              } catch (e) {}
            })();
            """;
    }

    private string RoleHome()
    {
        try
        {
            using var doc = JsonDocument.Parse(_session.UserJson ?? "{}");
            var root = doc.RootElement;
            var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
            var isGuardian = root.TryGetProperty("rbac_roles", out var rr) && rr.ValueKind == JsonValueKind.Array
                && rr.EnumerateArray().Any(x => x.GetString() == "guardian");
            var path = isGuardian ? "/encarregado"
                : role == "teacher" ? "/professor"
                : role == "student" ? "/aluno"
                : "/admin";
            return _cfg.WebAppUrl.TrimEnd('/') + path;
        }
        catch { return _cfg.WebAppUrl.TrimEnd('/') + "/admin"; }
    }

    private void ApplyView()
    {
        var showWeb = _webReady && _net.IsOnline && !_forceOffline;
        _web.Visible = showWeb;
        _offline.Visible = !showWeb;
        _tabWeb.Checked = showWeb;
        _tabOffline.Checked = !showWeb;

        if (showWeb)
        {
            var target = RoleHome();
            if (_web.Source is null || _web.Source.OriginalString == "about:blank"
                || !_web.Source.OriginalString.StartsWith(_cfg.WebAppUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                _web.CoreWebView2.Navigate(target);
        }
        else
        {
            _offline.RefreshGrid();
        }

        RenderStatus(_sync.Snapshot());
    }

    private void RenderStatus(SyncSnapshot s)
    {
        _dot.Text = s.Online ? "● Online" : "● Offline";
        _dot.ForeColor = s.Online ? Color.FromArgb(21, 128, 61) : Color.FromArgb(185, 28, 28);
        _pending.Text = s.Pending > 0 || s.Failed > 0
            ? $"Por sincronizar: {s.Pending}" + (s.Failed > 0 ? $"  ·  Erros: {s.Failed}" : "")
            : "Tudo sincronizado";
        _pending.ForeColor = s.Failed > 0 ? Color.FromArgb(185, 28, 28)
            : s.Pending > 0 ? Color.FromArgb(180, 83, 9) : Color.FromArgb(21, 128, 61);
        _pending.ToolTipText = s.LastSyncUtc is { } t ? $"Última sincronização: {t.ToLocalTime():dd/MM HH:mm}" : "Nunca sincronizado";
    }

    private void UI(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    private void ReAuth()
    {
        using var login = new LoginForm(_cfg, _api, _store);
        if (login.ShowDialog(this) == DialogResult.OK && login.Session is not null)
        {
            _session = login.Session;
            _sync.Detach();
            _sync.Attach(_session);
            _who.Text = $"{_session.UserName}  ·  {_session.TenantName ?? _session.TenantSlug ?? ""}";
            if (_webReady) _web.CoreWebView2.Navigate(RoleHome());
            _ = _sync.SyncNowAsync();
        }
    }

    private void Logout()
    {
        if (_store.Counts().pending > 0 &&
            MessageBox.Show(this, "Há marcações por sincronizar. Terminar sessão mesmo assim?",
                "WiseAcademy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try { _web.CoreWebView2?.ExecuteScriptAsync("localStorage.clear()"); } catch { }
        _store.ClearSession();
        _sync.Detach();
        Application.Restart();
    }
}
