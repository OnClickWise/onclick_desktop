using System.Net.NetworkInformation;

namespace WiseAcademy.Desktop.Core;

/// <summary>Vigia a ligação à internet e ao servidor WiseAcademy. Dispara
/// <see cref="StatusChanged"/> quando o estado muda.</summary>
public sealed class ConnectivityMonitor : IDisposable
{
    private readonly ApiClient _api;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public bool IsOnline { get; private set; }
    public event Action<bool>? StatusChanged;

    public ConnectivityMonitor(ApiClient api, int probeSeconds)
    {
        _api = api;
        _interval = TimeSpan.FromSeconds(Math.Max(5, probeSeconds));
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _ = PokeAsync();
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    public async Task PokeAsync()
    {
        var online = NetworkInterface.GetIsNetworkAvailable() && await _api.IsReachableAsync(_cts.Token);
        if (online != IsOnline)
        {
            IsOnline = online;
            try { StatusChanged?.Invoke(online); } catch { /* observador falhou */ }
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await PokeAsync(); } catch { /* ignora — nova tentativa no próximo ciclo */ }
            try { await Task.Delay(_interval, _cts.Token); } catch (TaskCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
