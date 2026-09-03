using WiseAcademy.Desktop.Core;

// Verificação ponta-a-ponta do modo offline + sincronização.
//   cd desktop && dotnet run --project tests/WiseAcademy.Desktop.SmokeTest -- <email> <pass> [apiUrl] [slug]
// Precisa da API a correr (por omissão http://localhost:3002).

// modo utilitário: --seed <email> <pass> [apiUrl] [slug] — grava a sessão na
// BD por omissão (para abrir a app já autenticada) e sai.
if (args.FirstOrDefault() == "--seed")
{
    var s = new AppConfig { ApiBaseUrl = args.ElementAtOrDefault(3) ?? "http://localhost:3002", TenantSlug = args.ElementAtOrDefault(4) ?? "wiseacademy-demo" };
    var st = new LocalStore();
    var ses = await new ApiClient(s).LoginAsync(args[1], args[2], s.TenantSlug);
    st.SaveSession(ses);
    Console.WriteLine($"sessão guardada em {LocalStore.DefaultDbPath()} para {ses.UserName}");
    return;
}

var email = args.ElementAtOrDefault(0) ?? "professor.teste@wiseacademy.com";
var pass = args.ElementAtOrDefault(1) ?? "teste2026";
var apiUrl = args.ElementAtOrDefault(2) ?? "http://localhost:3002";
var slug = args.ElementAtOrDefault(3) ?? "wiseacademy-demo";

int failures = 0;
void Check(string label, bool ok, object? got = null)
{
    Console.WriteLine($"  {(ok ? "✓" : "✗")} {label}{(ok ? "" : $"  — {got}")}");
    if (!ok) failures++;
}

var dbPath = Path.Combine(Path.GetTempPath(), $"wa-desktop-smoke-{Guid.NewGuid():N}.db");
var cfg = new AppConfig { ApiBaseUrl = apiUrl, TenantSlug = slug, Source = "desktop", DeviceName = "smoke-test", MaxSyncAttempts = 3 };
var store = new LocalStore(dbPath);

try
{
    Console.WriteLine("[armazenamento local]");
    var p1 = new LocalPunch { Kind = PunchKind.In, AtUtc = DateTime.UtcNow.AddMinutes(-30) };
    store.Enqueue(p1);
    var p2 = new LocalPunch { Kind = PunchKind.Out, AtUtc = DateTime.UtcNow.AddMinutes(-1) };
    store.Enqueue(p2);
    Check("2 marcações na fila (pendentes)", store.Counts() is (2, 0), store.Counts());

    Console.WriteLine("\n[login]");
    var api = new ApiClient(cfg);
    Session session;
    try { session = await api.LoginAsync(email, pass, slug); }
    catch (Exception ex) { Check($"login ({ex.GetType().Name})", false, ex.Message); throw; }
    Check("login devolve token + nome", session.Token.Length > 20 && session.UserName.Length > 0, session.UserName);
    store.SaveSession(session);
    Check("sessão persiste e relê", store.GetSession()?.UserId == session.UserId);

    Console.WriteLine("\n[sincronização — online]");
    using var net = new ConnectivityMonitor(api, 5);
    await net.PokeAsync();
    Check("monitor deteta servidor acessível", net.IsOnline, net.IsOnline);
    using var sync = new SyncService(store, api, net, cfg);
    sync.Log += m => Console.WriteLine($"    · {m}");
    sync.Attach(session);
    await sync.SyncNowAsync();
    var afterSync = store.Counts();
    Check("fila drenada (0 pendentes, 0 erros)", afterSync is (0, 0), afterSync);
    Check("marcações ficam 'Synced' com id remoto",
        store.GetRecent(10).Where(x => x.State == SyncState.Synced).All(x => !string.IsNullOrEmpty(x.RemoteId)));
    Check("last-sync gravado", store.LastSyncUtc is not null);

    Console.WriteLine("\n[sincronização — offline -> volta a online]");
    var offCfg = new AppConfig { ApiBaseUrl = "http://127.0.0.1:59999", TenantSlug = slug, MaxSyncAttempts = 3 };
    var offApi = new ApiClient(offCfg, new HttpClient { Timeout = TimeSpan.FromSeconds(2) });
    using var offNet = new ConnectivityMonitor(offApi, 5);
    await offNet.PokeAsync();
    Check("monitor deteta offline", !offNet.IsOnline);
    using var offSync = new SyncService(store, offApi, offNet, offCfg) ;
    offSync.Attach(session);
    var p3 = offSync.Punch(PunchKind.BreakStart);
    await Task.Delay(200);
    Check("marcação feita offline fica pendente", store.GetRecent(1)[0].State == SyncState.Pending);
    // volta a online: usa o cliente bom
    await sync.SyncNowAsync();
    Check("ao voltar a rede, a marcação offline sincroniza",
        store.GetRecent(5).First(x => x.Id == p3.Id).State == SyncState.Synced);

    Console.WriteLine("\n" + new string('=', 52));
    if (failures > 0) { Console.Error.WriteLine($"{failures} verificação(ões) falharam"); Environment.Exit(1); }
    Console.WriteLine("modo offline + sincronização do desktop está são");
}
finally
{
    try { File.Delete(dbPath); } catch { }
}
