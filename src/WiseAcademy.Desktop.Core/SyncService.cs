namespace WiseAcademy.Desktop.Core;

/// <summary>
/// Motor de sincronização. As marcações são sempre gravadas na fila local
/// (<see cref="LocalStore"/>). Quando há ligação, este serviço drena a fila
/// para o servidor (<c>POST /rh/ponto/bater</c> com o instante original) e
/// puxa o resumo do dia. Corre automaticamente quando a internet regressa.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly LocalStore _store;
    private readonly ApiClient _api;
    private readonly ConnectivityMonitor _net;
    private readonly AppConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Timers.Timer _periodic;

    public Session? Session { get; private set; }

    public event Action<SyncSnapshot>? Changed;
    public event Action<PunchDay>? DayUpdated;
    public event Action? SessionExpired;
    public event Action<string>? Log;

    public SyncService(LocalStore store, ApiClient api, ConnectivityMonitor net, AppConfig cfg)
    {
        _store = store;
        _api = api;
        _net = net;
        _cfg = cfg;
        _net.StatusChanged += OnNet;
        _periodic = new System.Timers.Timer(60_000) { AutoReset = true };
        _periodic.Elapsed += (_, _) => _ = SyncNowAsync();
    }

    public void Attach(Session session)
    {
        Session = session;
        _periodic.Start();
        Emit("Sessão iniciada.");
    }

    public void Detach()
    {
        _periodic.Stop();
        Session = null;
    }

    private void OnNet(bool online)
    {
        Emit(online ? "Ligação reposta — a sincronizar…" : "Sem ligação — a trabalhar offline.");
        if (online) _ = SyncNowAsync();
        else Emit();
    }

    /// <summary>Regista uma marcação: grava já localmente e tenta sincronizar.</summary>
    public LocalPunch Punch(PunchKind kind, string? note = null)
    {
        var p = new LocalPunch { Kind = kind, AtUtc = DateTime.UtcNow, Note = note };
        _store.Enqueue(p);
        Emit($"{kind.Label()} registada localmente.");
        _ = SyncNowAsync();
        return p;
    }

    public async Task SyncNowAsync()
    {
        if (Session is null) return;
        if (!await _gate.WaitAsync(0)) return;   // já há uma sincronização a decorrer
        try
        {
            if (!_net.IsOnline) { await _net.PokeAsync(); }
            if (!_net.IsOnline) { Emit(); return; }

            var pending = _store.GetPending();
            int done = 0;
            foreach (var p in pending)
            {
                try
                {
                    var remoteId = await _api.SendPunchAsync(Session!, p);
                    _store.MarkSynced(p.Id, remoteId);
                    done++;
                }
                catch (ApiUnavailableException)
                {
                    await _net.PokeAsync();      // caiu a meio — o resto fica pendente
                    Emit("Ligação perdida durante a sincronização.");
                    return;
                }
                catch (ApiAuthException ex)
                {
                    Emit(ex.Message);
                    SessionExpired?.Invoke();
                    return;
                }
                catch (ApiException ex)
                {
                    var attempts = p.Attempts + 1;
                    var permanent = attempts >= _cfg.MaxSyncAttempts;
                    _store.MarkFailed(p.Id, ex.Message, attempts, permanent);
                    Emit(permanent
                        ? $"Marcação recusada pela API: {ex.Message}"
                        : $"Falha temporária ({attempts}/{_cfg.MaxSyncAttempts}): {ex.Message}");
                }
            }

            _store.LastSyncUtc = DateTime.UtcNow;
            if (done > 0) Emit($"{done} marcação(ões) sincronizada(s).");

            try
            {
                var day = await _api.GetMyDayAsync(Session!, DateOnly.FromDateTime(DateTime.Now));
                if (day is not null) DayUpdated?.Invoke(day);
            }
            catch (ApiUnavailableException) { /* offline outra vez */ }
            catch (ApiAuthException) { SessionExpired?.Invoke(); return; }

            Emit();
        }
        finally { _gate.Release(); }
    }

    public SyncSnapshot Snapshot()
    {
        var (pending, failed) = _store.Counts();
        return new SyncSnapshot
        {
            Online = _net.IsOnline,
            Pending = pending,
            Failed = failed,
            LastSyncUtc = _store.LastSyncUtc,
        };
    }

    private void Emit(string? message = null)
    {
        var s = Snapshot();
        s.LastMessage = message;
        if (message is not null) Log?.Invoke(message);
        Changed?.Invoke(s);
    }

    public void Dispose()
    {
        _net.StatusChanged -= OnNet;
        _periodic.Dispose();
        _gate.Dispose();
    }
}
