using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>Autenticação. Guarda a sessão localmente. Se não houver ligação
/// mas existir uma sessão em cache, permite continuar offline.</summary>
public sealed class LoginForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ApiClient _api;
    private readonly LocalStore _store;

    private readonly TextBox _email = new() { Width = 300 };
    private readonly TextBox _pass = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly TextBox _slug = new() { Width = 300 };
    private readonly TextBox _server = new() { Width = 300 };
    private readonly Button _submit = new() { Text = "Entrar", Width = 300, Height = 38 };
    private readonly LinkLabel _offline = new() { Text = "Continuar offline com a última sessão", AutoSize = true, Visible = false };
    private readonly Label _msg = new() { AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Color.Firebrick };

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
        ClientSize = new Size(360, 430);
        Font = new Font("Segoe UI", 9.5f);

        _slug.Text = cfg.TenantSlug ?? "";
        _server.Text = cfg.ApiBaseUrl;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 24), ColumnCount = 1 };
        void Row(string label, Control c)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
            layout.Controls.Add(c);
        }
        layout.Controls.Add(new Label { Text = "Bem-vindo(a)", Font = new Font("Segoe UI", 15f, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Posto de registo de ponto — funciona offline.", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        Row("E-mail", _email);
        Row("Palavra-passe", _pass);
        Row("Instituição (slug)", _slug);
        Row("Servidor", _server);
        layout.Controls.Add(_submit);
        _submit.Margin = new Padding(0, 14, 0, 6);
        layout.Controls.Add(_offline);
        layout.Controls.Add(_msg);
        Controls.Add(layout);

        AcceptButton = _submit;
        _submit.Click += async (_, _) => await DoLoginAsync();
        _offline.LinkClicked += (_, _) => ContinueOffline();

        _offline.Visible = _store.GetSession() is not null;
    }

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
            _cfg.ApiBaseUrl = _server.Text.Trim();
            _cfg.TenantSlug = string.IsNullOrWhiteSpace(_slug.Text) ? null : _slug.Text.Trim();

            var s = await _api.LoginAsync(_email.Text.Trim(), _pass.Text, _cfg.TenantSlug);
            _store.SaveSession(s);
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
        UseWaitCursor = b;
    }
}
