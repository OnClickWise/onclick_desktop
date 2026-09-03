using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var cfg = AppSettings.Load();
        var store = new LocalStore();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var api = new ApiClient(cfg, http);
        using var net = new ConnectivityMonitor(api, cfg.ConnectivityProbeSeconds);
        using var sync = new SyncService(store, api, net, cfg);
        net.Start();

        var session = store.GetSession();
        if (session is null)
        {
            using var login = new LoginForm(cfg, api, store);
            if (login.ShowDialog() != DialogResult.OK || login.Session is null)
                return;
            session = login.Session;
        }

        sync.Attach(session);
        Application.Run(new MainForm(cfg, api, store, sync, net, session));
    }
}
