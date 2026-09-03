using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>
/// Casca do posto. Funciona em dois modos:
/// <list type="bullet">
///   <item><b>Modo Web</b> — usa os servidores centrais (precisa de internet).</item>
///   <item><b>Modo Desktop</b> — pilha local completa (API + Postgres embutido +
///   frontend) no próprio posto; <b>tudo</b> o que se faz na web funciona sem
///   internet. Sincroniza com o central quando a ligação volta.</item>
/// </list>
/// Sem ligação, o posto muda sozinho para Modo Desktop. Se nem a web local
/// arrancar, mostra o painel nativo de registo de ponto como último recurso.
/// </summary>
public sealed class MainForm : Form
{
    private enum Boot { Idle, Starting, Ready, Failed }

    private readonly AppConfig _cfg;
    private readonly ApiClient _api;
    private readonly LocalStore _store;
    private readonly SyncService _sync;
    private readonly ConnectivityMonitor _net;
    private readonly LocalStack _stack;
    private Session _session;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly OfflinePunchPanel _offline;
    private bool _webReady;

    private AppMode _mode;
    private bool _autoDesktop;          // mudámos p/ Desktop por falta de rede — voltar a Web quando voltar
    private Boot _boot = Boot.Idle;
    private Task? _bootTask;
    private string? _apiScriptId;       // script window.__WA_API_URL registado no WebView2
    private string? _injectedApi;
    private string _navBase = "";       // base do URL atualmente carregado

    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripLabel _dot = new("● …");
    private readonly ToolStripButton _modeWeb = new("Modo Web") { CheckOnClick = false };
    private readonly ToolStripButton _modeDesktop = new("Modo Desktop") { CheckOnClick = false };
    private readonly ToolStripButton _syncBtn = new("Sincronizar agora");
    private readonly ToolStripLabel _pending = new();
    private readonly ToolStripLabel _boot_lbl = new() { Visible = false };
    private readonly ToolStripLabel _who = new() { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _logout = new("Terminar sessão") { Alignment = ToolStripItemAlignment.Right };

    public MainForm(AppConfig cfg, ApiClient api, LocalStore store, SyncService sync, ConnectivityMonitor net, Session session)
    {
        _cfg = cfg; _api = api; _store = store; _sync = sync; _net = net; _session = session;
        _stack = new LocalStack(cfg);
        _mode = cfg.PreferredMode;
        _offline = new OfflinePunchPanel(store, sync) { Dock = DockStyle.Fill, Visible = true };

        Text = "WiseAcademy — Posto";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(900, 620);
        WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9.5f);

        _modeWeb.ToolTipText = "Servidores centrais (precisa de internet)";
        _modeDesktop.ToolTipText = "Pilha local no posto — funciona sem internet";

        _bar.Items.AddRange([
            _dot, new ToolStripSeparator(),
            _modeWeb, _modeDesktop, new ToolStripSeparator(),
            _syncBtn, _pending, _boot_lbl,
            _logout, _who,
        ]);
        Controls.Add(_web);
        Controls.Add(_offline);
        Controls.Add(_bar);

        _who.Text = $"{session.UserName}  ·  {session.TenantName ?? session.TenantSlug ?? ""}";

        _modeWeb.Click += (_, _) => SwitchMode(AppMode.Web, manual: true);
        _modeDesktop.Click += (_, _) => SwitchMode(AppMode.Desktop, manual: true);
        _syncBtn.Click += (_, _) => _ = _sync.SyncNowAsync();
        _logout.Click += (_, _) => Logout();

        _stack.Log += m => UI(() => { _boot_lbl.Text = "Modo Desktop: " + m; });
        _net.StatusChanged += online => UI(() => OnConnectivity(online));
        _sync.Changed += s => UI(() => RenderStatus(s));
        _sync.SessionExpired += () => UI(ReAuth);

        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) => { try { _stack.Dispose(); } catch { } };
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
            _dot.ToolTipText = "WebView2 indisponível: " + ex.Message;
        }

        await _net.PokeAsync();

        // sem internet ao arrancar → Modo Desktop
        if (!_net.IsOnline && _mode == AppMode.Web) { _mode = AppMode.Desktop; _autoDesktop = true; }
        if (_mode == AppMode.Desktop || _cfg.AutoStartLocalStack) BeginBoot();

        ApplyView();
        _ = _sync.SyncNowAsync();
    }

    /* ── modo ────────────────────────────────────────────────────── */

    private void SwitchMode(AppMode m, bool manual)
    {
        if (manual) _autoDesktop = false;
        if (_mode == m) { ApplyView(); return; }
        _mode = m;
        if (manual) { _cfg.PreferredMode = m; AppSettings.Save(_cfg); }
        if (m == AppMode.Desktop) BeginBoot();
        ApplyView();
    }

    private void OnConnectivity(bool online)
    {
        if (!online && _mode == AppMode.Web)
        {
            _autoDesktop = true;
            _mode = AppMode.Desktop;
            BeginBoot();
        }
        else if (online && _autoDesktop && _mode == AppMode.Desktop)
        {
            _autoDesktop = false;
            _mode = AppMode.Web;
        }
        ApplyView();
    }

    /* ── arranque da pilha local ─────────────────────────────────── */

    private void BeginBoot()
    {
        if (_boot is Boot.Starting or Boot.Ready) return;
        _boot = Boot.Starting;
        _boot_lbl.Visible = true;
        _boot_lbl.Text = "Modo Desktop: a preparar…";
        _bootTask = Task.Run(async () =>
        {
            try
            {
                await _stack.StartAsync(_session);
                UI(() => { _boot = Boot.Ready; _boot_lbl.Text = "Modo Desktop: pronto"; ApplyView(); });
            }
            catch (Exception ex)
            {
                UI(() =>
                {
                    _boot = Boot.Failed;
                    _boot_lbl.Text = "Modo Desktop indisponível";
                    _boot_lbl.ToolTipText = ex.Message;
                    ApplyView();
                });
            }
        });
    }

    /* ── autenticação da web (localStorage) ──────────────────────── */

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

    /// <summary>Regista/atualiza o script que injeta <c>window.__WA_API_URL</c>
    /// (lido por <c>src/lib/api.ts</c>) para a web local falar com a API local.</summary>
    private async Task SetApiOverrideAsync(string? apiUrl)
    {
        if (!_webReady || apiUrl == _injectedApi) return;
        try
        {
            if (_apiScriptId is not null)
                _web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_apiScriptId);
            var js = apiUrl is null
                ? "try{delete window.__WA_API_URL;}catch(e){}"
                : $"window.__WA_API_URL = {JsonSerializer.Serialize(apiUrl)};";
            _apiScriptId = await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);
            _injectedApi = apiUrl;
        }
        catch { /* o WebView2 pode ainda não estar pronto — próxima navegação repõe */ }
    }

    private string RoleHome(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
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
            return b + path;
        }
        catch { return b + "/admin"; }
    }

    /* ── vista ───────────────────────────────────────────────────── */

    private void ApplyView()
    {
        // 1) o que conseguimos mostrar agora?
        string? webBase = null, apiOverride = null;
        if (_webReady)
        {
            if (_mode == AppMode.Web && _net.IsOnline)
            {
                webBase = _cfg.WebAppUrl;
                apiOverride = null; // usa o NEXT_PUBLIC_API_URL do build central
            }
            else if (_mode == AppMode.Desktop && _boot == Boot.Ready && _stack.WebUrl is { } wu)
            {
                webBase = wu;
                apiOverride = _stack.ApiUrl;
            }
        }

        var showWeb = webBase is not null;
        _web.Visible = showWeb;
        _offline.Visible = !showWeb;

        _modeWeb.Checked = _mode == AppMode.Web;
        _modeDesktop.Checked = _mode == AppMode.Desktop;

        if (showWeb)
        {
            _ = NavigateAsync(webBase!, apiOverride);
        }
        else
        {
            _offline.RefreshGrid();
            if (_mode == AppMode.Desktop && _boot == Boot.Starting)
                _offline.ShowNotice("A preparar o Modo Desktop (API + base de dados locais)… a primeira vez pode demorar.");
            else if (_mode == AppMode.Desktop && _boot == Boot.Failed)
                _offline.ShowNotice("Modo Desktop indisponível neste posto. Registo de ponto continua a funcionar offline.");
            else if (_mode == AppMode.Web && !_net.IsOnline)
                _offline.ShowNotice("Sem ligação. Ative o Modo Desktop para usar todo o sistema offline.");
            else
                _offline.ShowNotice(null);
        }

        RenderStatus(_sync.Snapshot());
    }

    private async Task NavigateAsync(string webBase, string? apiOverride)
    {
        await SetApiOverrideAsync(apiOverride);
        var b = webBase.TrimEnd('/');
        var cur = _web.Source?.OriginalString ?? "";
        if (_navBase != b || cur is "" or "about:blank" || !cur.StartsWith(b, StringComparison.OrdinalIgnoreCase))
        {
            _navBase = b;
            _web.CoreWebView2.Navigate(RoleHome(b));
        }
    }

    private void RenderStatus(SyncSnapshot s)
    {
        var mode = _mode == AppMode.Desktop ? "Desktop" : "Web";
        _dot.Text = (s.Online ? "● Online" : "● Offline") + $"  ·  {mode}";
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
            _navBase = "";
            ApplyView();
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
        try { _stack.Dispose(); } catch { }
        Application.Restart();
    }
}
