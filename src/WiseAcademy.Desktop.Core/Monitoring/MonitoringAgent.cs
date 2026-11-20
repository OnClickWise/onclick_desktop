using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WiseAcademy.Desktop.Core.Monitoring;

/// <summary>Configuração que a API devolve ao agente (<c>/monitoria/agente/config</c>).</summary>
public sealed class AgentConfig
{
    public bool Active { get; set; }
    public string? Reason { get; set; }
    public string? DeviceId { get; set; }
    public bool TrackApps { get; set; } = true;
    public bool TrackUrls { get; set; } = true;
    public bool TrackTitles { get; set; } = true;
    public bool TrackInput { get; set; } = true;
    public bool Screenshots { get; set; }
    public int ScreenshotMinutes { get; set; } = 10;
    public bool ScreenshotBlur { get; set; }
    public bool LiveView { get; set; }
    public int IdleMinutes { get; set; } = 5;
    public int HeartbeatSeconds { get; set; } = 60;
    public int FlushSeconds { get; set; } = 60;
    public bool WorkHoursOnly { get; set; }
    public string WorkStart { get; set; } = "08:00";
    public string WorkEnd { get; set; } = "18:00";
    public int[] WorkDays { get; set; } = [1, 2, 3, 4, 5];
    public string? Notice { get; set; }
}

/// <param name="Viewers">Quem está a ver o ecrã ao vivo agora (vazio = ninguém).</param>
public sealed record AgentStatus(bool Active, bool Connected, string? Reason, int Queued, bool Screenshots, string? Notice, DateTime? LastSentUtc, IReadOnlyList<string> Viewers);

/// <summary>
/// Agente de monitoria do posto (porte do agente TimeZoon para dentro do WiseAcademy Posto).
/// Regista a janela em foco (aplicativo, título, URL), a inatividade, contagens de teclado/rato,
/// capturas de ecrã periódicas e eventos do sistema; guarda tudo numa fila local e envia para
/// <c>/monitoria/agente/*</c> sempre que há ligação. Só recolhe quando a organização tem o módulo
/// Monitoria ativo e a política o permite — a decisão é sempre da API.
/// </summary>
public sealed class MonitoringAgent : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    private static readonly string Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
    private const int MaxSegmentSeconds = 300;

    private readonly AppConfig _cfg;
    private readonly LocalStore _store;
    private readonly MonitorOutbox _outbox;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly InputCounter _input = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _drain = new(1, 1);

    private Session? _session;
    private AgentConfig? _conf;
    private string? _deviceId;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _liveLoop;
    private IReadOnlyList<string> _viewers = [];
    private bool _connected;
    private bool _authFailed;
    private DateTime? _lastSentUtc;

    private Segment? _seg;
    private readonly List<object> _apps = [];
    private readonly List<object> _events = [];
    private DateTime _inputFrom = DateTime.UtcNow;
    private double _activeS, _idleS;
    private DateTime _lastTick = DateTime.UtcNow, _nextBeat = DateTime.MinValue, _nextFlush = DateTime.UtcNow.AddSeconds(60), _nextShot = DateTime.UtcNow.AddMinutes(1);
    private bool _idle;
    private string? _currentApp;

    private sealed class Segment
    {
        public required string Key, Process, App, Title;
        public string? Url;
        public DateTime Start;
        public double Idle;
    }

    public event Action<AgentStatus>? Changed;
    /// <summary>Erros internos do agente (diagnóstico).</summary>
    public event Action<string>? Log;
    /// <summary>A monitoria ficou ativa para este utilizador e o aviso ainda não foi mostrado (texto do aviso).</summary>
    public event Action<string>? NoticeRequired;

    public MonitoringAgent(AppConfig cfg, LocalStore store)
    {
        _cfg = cfg; _store = store;
        _outbox = new MonitorOutbox(store.DbPath);
    }

    public AgentStatus Snapshot() => new(_conf?.Active == true && !_authFailed, _connected, _authFailed ? "Sessão expirada" : _conf?.Reason, SafeCount(), _conf?.Screenshots == true, _conf?.Notice, _lastSentUtc, _viewers);
    private int SafeCount() { try { return _outbox.Count(); } catch { return 0; } }
    private string MetaKey(string k) => $"mon:{k}:{_cfg.ApiBaseUrl.TrimEnd('/')}:{_session?.TenantSlug}";
    private string Url(string path) => $"{_cfg.ApiBaseUrl.TrimEnd('/')}{path}";

    /* ── ciclo de vida ─────────────────────────────────────────────── */

    public void Attach(Session s)
    {
        Detach(sendOffline: false);
        _session = s;
        _authFailed = false;
        _deviceId = _store.GetMeta(MetaKey("device"));
        // última configuração conhecida — permite recolher offline desde o arranque
        var cached = _store.GetMeta(MetaKey("config:" + s.UserId));
        if (cached is not null) try { _conf = JsonSerializer.Deserialize<AgentConfig>(cached, Json); } catch { _conf = null; }
        _lastTick = DateTime.UtcNow;
        _inputFrom = DateTime.UtcNow;
        _nextBeat = DateTime.MinValue;
        _input.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionEnding += OnEnding;
        AddEvent("agent_start", DateTime.UtcNow);
        DetectBoot();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct), ct);
        _liveLoop = Task.Run(() => LiveLoopAsync(ct), ct);
    }

    public void Detach(bool sendOffline = true)
    {
        if (_cts is null) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionEnding -= OnEnding;
        AddEvent("agent_stop", DateTime.UtcNow);
        try { Flush(DateTime.UtcNow); } catch { }
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        try { _liveLoop?.Wait(2000); } catch { }
        _viewers = [];
        if (sendOffline && _session is not null && _deviceId is not null)
        {
            try { DrainAsync(CancellationToken.None).Wait(4000); } catch { }
            try { PostAsync("/monitoria/agente/offline", new { device_id = _deviceId }, CancellationToken.None).Wait(3000); } catch { }
        }
        _cts = null; _loop = null; _session = null;
    }

    public void Dispose()
    {
        Detach();
        _input.Dispose();
        _http.Dispose();
    }

    /* ── ciclo principal (1 s) ─────────────────────────────────────── */

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { try { Log?.Invoke(ex.GetType().Name + ": " + ex.Message); } catch { } } // nunca deixar o agente morrer por um erro pontual
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (elapsed > 90) { CloseSegment(now.AddSeconds(-elapsed)); } // o PC esteve suspenso
        elapsed = Math.Clamp(elapsed, 0, 5);

        if (now >= _nextBeat) { _nextBeat = now.AddSeconds(Math.Max(15, _conf?.HeartbeatSeconds ?? 60)); await HeartbeatAsync(ct); }

        var c = _conf;
        if (c is null || !c.Active || _authFailed || !InWorkHours(c))
        {
            CloseSegment(now); _input.Take(); _activeS = _idleS = 0; _inputFrom = now;
            lock (_lock) _apps.Clear();
            return;
        }

        _idle = Win32.IdleSeconds() >= Math.Max(1, c.IdleMinutes) * 60;
        if (_idle) _idleS += elapsed; else _activeS += elapsed;

        if (c.TrackApps)
        {
            var fg = Foreground.Read(c.TrackUrls);
            _currentApp = fg.Handle == IntPtr.Zero ? null : fg.AppName;
            var key = fg.Key;
            if (_seg is not null && (_seg.Key != key || (now - _seg.Start).TotalSeconds >= MaxSegmentSeconds)) CloseSegment(now);
            if (_seg is null && fg.Handle != IntPtr.Zero && fg.ProcessName.Length > 0)
                _seg = new Segment { Key = key, Process = fg.ProcessName, App = fg.AppName, Title = fg.Title, Url = fg.Url, Start = now };
            if (_seg is not null && _idle) _seg.Idle += elapsed;
        }

        if (now >= _nextFlush) { _nextFlush = now.AddSeconds(Math.Max(15, c.FlushSeconds)); Flush(now); await DrainAsync(ct); }

        if (c.Screenshots && now >= _nextShot)
        {
            _nextShot = now.AddMinutes(Math.Max(1, c.ScreenshotMinutes));
            if (!_idle) Capture(c, now);
        }
    }

    private static bool InWorkHours(AgentConfig c)
    {
        if (!c.WorkHoursOnly) return true;
        var n = DateTime.Now;
        var dow = n.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)n.DayOfWeek;
        var hm = n.ToString("HH:mm");
        return c.WorkDays.Contains(dow) && string.CompareOrdinal(hm, c.WorkStart) >= 0 && string.CompareOrdinal(hm, c.WorkEnd) < 0;
    }

    private void CloseSegment(DateTime end)
    {
        var s = _seg;
        _seg = null;
        if (s is null || (end - s.Start).TotalSeconds < 1) return;
        var c = _conf;
        lock (_lock)
            _apps.Add(new
            {
                id = Guid.NewGuid(), app_name = s.App, process_name = s.Process,
                window_title = c?.TrackTitles == false ? null : s.Title, url = c?.TrackUrls == false ? null : s.Url,
                started_at = s.Start.ToString("O"), ended_at = end.ToString("O"), idle_s = (int)Math.Round(s.Idle),
            });
    }

    /// <summary>Fecha o intervalo atual e guarda o lote na fila local.</summary>
    private void Flush(DateTime now)
    {
        if (_session is null) return;
        if (_seg is not null) { var keep = _seg; CloseSegment(now); _seg = new Segment { Key = keep.Key, Process = keep.Process, App = keep.App, Title = keep.Title, Url = keep.Url, Start = now }; }
        var counts = _input.Take();
        object[] input = [];
        if (_conf?.TrackInput != false && (_activeS + _idleS) >= 1)
            input = [new {
                id = Guid.NewGuid(), started_at = _inputFrom.ToString("O"), ended_at = now.ToString("O"), app_name = _currentApp,
                keystrokes = counts.Keystrokes, letters = counts.Letters, digits = counts.Digits, symbols = counts.Symbols, backspaces = counts.Backspaces,
                left_clicks = counts.LeftClicks, right_clicks = counts.RightClicks, middle_clicks = counts.MiddleClicks, scrolls = counts.Scrolls,
                mouse_px = (int)Math.Min(int.MaxValue, counts.MousePx), active_s = (int)Math.Round(_activeS), idle_s = (int)Math.Round(_idleS),
            }];
        _inputFrom = now; _activeS = _idleS = 0;
        object[] apps, events;
        lock (_lock) { apps = [.. _apps]; events = [.. _events]; _apps.Clear(); _events.Clear(); }
        if (apps.Length == 0 && input.Length == 0 && events.Length == 0) return;
        _outbox.Add("batch", JsonSerializer.Serialize(new { apps, input, events }));
    }

    /* ── capturas de ecrã ──────────────────────────────────────────── */

    /* ── ao vivo ────────────────────────────────────────────────────── */

    /// <summary>
    /// Consulta os pedidos a cada 3 s; enquanto houver alguém a ver, envia imagens do ecrã
    /// (só em memória no servidor — nada é gravado). O posto mostra quem está a ver.
    /// </summary>
    private async Task LiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = 3000;
            try
            {
                var c = _conf;
                if (_session is not null && _deviceId is not null && c is { Active: true, LiveView: true } && !_authFailed)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, Url($"/monitoria/agente/comandos?device_id={_deviceId}"));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
                    var d = await SendAsync(req, ct);
                    var live = d is { } x && x.TryGetProperty("live", out var l) && l.GetBoolean();
                    IReadOnlyList<string> viewers = live && d!.Value.TryGetProperty("viewers", out var v) ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
                    SetViewers(viewers);
                    if (live)
                    {
                        var fps = Math.Clamp(d!.Value.TryGetProperty("fps", out var f) ? f.GetInt32() : 1, 1, 4);
                        var jpg = CaptureJpeg(1280, 50L, false);
                        if (jpg is not null)
                        {
                            using var form = new MultipartFormDataContent();
                            form.Add(new StringContent(_deviceId), "device_id");
                            var file = new ByteArrayContent(jpg);
                            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                            form.Add(file, "file", "frame.jpg");
                            using var up = new HttpRequestMessage(HttpMethod.Post, Url("/monitoria/agente/ao-vivo")) { Content = form };
                            up.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
                            await SendAsync(up, ct);
                        }
                        wait = 1000 / fps;
                    }
                }
                else SetViewers([]);
            }
            catch (OperationCanceledException) { break; }
            catch { SetViewers([]); }
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetViewers(IReadOnlyList<string> v)
    {
        if (v.SequenceEqual(_viewers)) return;
        _viewers = v;
        Raise();
    }

    /// <summary>Ecrã inteiro (todos os monitores) em JPEG, reduzido a <paramref name="maxWidth"/>.</summary>
    private static byte[]? CaptureJpeg(int maxWidth, long quality, bool blur)
    {
        try
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            var scale = Math.Min(1.0, (double)maxWidth / vs.Width);
            var w = Math.Max(1, (int)(vs.Width * scale));
            var h = Math.Max(1, (int)(vs.Height * scale));
            using var outBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                if (blur)
                {
                    using var tiny = new Bitmap(Math.Max(1, w / 12), Math.Max(1, h / 12));
                    using (var gt = Graphics.FromImage(tiny)) { gt.InterpolationMode = InterpolationMode.HighQualityBilinear; gt.DrawImage(full, 0, 0, tiny.Width, tiny.Height); }
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(tiny, 0, 0, w, h);
                }
                else { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, w, h); }
            }
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            using var ms = new MemoryStream();
            outBmp.Save(ms, enc, ps);
            return ms.ToArray();
        }
        catch { return null; } // ecrã bloqueado / sem ambiente gráfico
    }

    private void Capture(AgentConfig c, DateTime now)
    {
        var jpg = CaptureJpeg(1600, 60L, c.ScreenshotBlur);
        if (jpg is null) return;
        var id = Guid.NewGuid();
        var file = Path.Combine(_outbox.ShotsDir, id + ".jpg");
        File.WriteAllBytes(file, jpg);
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        var scale = Math.Min(1.0, 1600.0 / vs.Width);
        _outbox.Add("shot", JsonSerializer.Serialize(new { id, captured_at = now.ToString("O"), app_name = _currentApp, window_title = c.TrackTitles ? _seg?.Title : null, width = (int)(vs.Width * scale), height = (int)(vs.Height * scale) }), file);
    }

    /* ── eventos do sistema ────────────────────────────────────────── */

    private void AddEvent(string type, DateTime atUtc)
    {
        lock (_lock) _events.Add(new { id = Guid.NewGuid(), type, occurred_at = atUtc.ToString("O") });
    }

    private void DetectBoot()
    {
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var mark = boot.ToString("yyyy-MM-ddTHH:mm");
        if (_store.GetMeta("mon:boot") == mark) return;
        _store.SetMeta("mon:boot", mark);
        AddEvent("boot", boot);
    }

    private void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        var t = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "lock", SessionSwitchReason.SessionUnlock => "unlock",
            SessionSwitchReason.SessionLogon => "logon", SessionSwitchReason.SessionLogoff => "logoff", _ => null,
        };
        if (t is not null) AddEvent(t, DateTime.UtcNow);
        if (t == "lock") CloseSegment(DateTime.UtcNow);
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { AddEvent("sleep", DateTime.UtcNow); CloseSegment(DateTime.UtcNow); try { Flush(DateTime.UtcNow); } catch { } }
        else if (e.Mode == PowerModes.Resume) { AddEvent("wake", DateTime.UtcNow); _lastTick = DateTime.UtcNow; }
    }

    private void OnEnding(object? s, SessionEndingEventArgs e)
    {
        AddEvent(e.Reason == SessionEndReasons.SystemShutdown ? "shutdown" : "logoff", DateTime.UtcNow);
        CloseSegment(DateTime.UtcNow);
        try { Flush(DateTime.UtcNow); } catch { }
    }

    /* ── API ───────────────────────────────────────────────────────── */

    private async Task<JsonElement?> PostAsync(string path, object body, CancellationToken ct)
    {
        if (_session is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
        return await SendAsync(req, ct);
    }

    /// <summary>null = sem rede; lança <see cref="ApiException"/> para respostas de erro.</summary>
    private async Task<JsonElement?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            SetConnected(false);
            return null;
        }
        using (res)
        {
            SetConnected(true);
            JsonElement? data = null;
            try { using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)); if (doc.RootElement.TryGetProperty("data", out var d)) data = d.Clone(); } catch { }
            if (res.StatusCode == HttpStatusCode.Unauthorized) { _authFailed = true; Raise(); throw new ApiAuthException("Sessão expirada."); }
            if (!res.IsSuccessStatusCode) throw new ApiException($"Erro da API ({(int)res.StatusCode}).", (int)res.StatusCode);
            return data ?? default(JsonElement);
        }
    }

    private void SetConnected(bool v) { if (_connected != v) { _connected = v; Raise(); } }
    private void Raise() { try { Changed?.Invoke(Snapshot()); } catch { } }

    private async Task<bool> RegisterAsync(CancellationToken ct)
    {
        var data = await PostAsync("/monitoria/agente/dispositivos", new
        {
            machine_id = MachineId(), hostname = Environment.MachineName, os = RuntimeInformation.OSDescription, agent_version = Version,
        }, ct);
        if (data is not { } d || !d.TryGetProperty("device", out var dev)) return false;
        _deviceId = dev.GetProperty("id").GetString();
        if (_deviceId is not null) _store.SetMeta(MetaKey("device"), _deviceId);
        if (d.TryGetProperty("config", out var cfg)) ApplyConfig(cfg);
        return _deviceId is not null;
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (_session is null) return;
        try
        {
            if (_deviceId is null && !await RegisterAsync(ct)) return;
            var data = await PostAsync("/monitoria/agente/heartbeat", new { device_id = _deviceId, current_app = _currentApp, idle = _idle, agent_version = Version }, ct);
            if (data is { } d) ApplyConfig(d);
        }
        catch (ApiException ex) when (ex.Status is 400 or 404) { _deviceId = null; } // dispositivo removido → registar de novo
        catch (ApiException ex) when (ex.Status == 403)
        {
            // módulo Monitoria desligado para a organização
            _conf = new AgentConfig { Active = false, Reason = "Módulo Monitoria inativo" };
            Raise();
        }
        catch (ApiAuthException) { }
    }

    private void ApplyConfig(JsonElement d)
    {
        var next = d.Deserialize<AgentConfig>(Json);
        if (next is null) return;
        var wasActive = _conf?.Active == true;
        _conf = next;
        if (_session is not null) _store.SetMeta(MetaKey("config:" + _session.UserId), JsonSerializer.Serialize(next, Json));
        if (next.Active && !wasActive) _nextShot = DateTime.UtcNow.AddMinutes(Math.Min(2, next.ScreenshotMinutes));
        if (next.Active && _session is not null && !string.IsNullOrWhiteSpace(next.Notice))
        {
            var ack = MetaKey("notice:" + _session.UserId);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(next.Notice)))[..16];
            if (_store.GetMeta(ack) != hash) { _store.SetMeta(ack, hash); try { NoticeRequired?.Invoke(next.Notice); } catch { } }
        }
        Raise();
    }

    /// <summary>Envia a fila local por ordem; para à primeira falha de rede.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        if (_session is null || _authFailed || !await _drain.WaitAsync(0, ct)) return;
        try
        {
            if (_deviceId is null && !await RegisterAsync(ct)) return;
            while (!ct.IsCancellationRequested)
            {
                var items = _outbox.Next();
                if (items.Count == 0) break;
                foreach (var it in items)
                {
                    JsonElement? res;
                    try
                    {
                        if (it.Kind == "batch")
                        {
                            using var doc = JsonDocument.Parse(it.Payload);
                            var body = new Dictionary<string, object?> { ["device_id"] = _deviceId };
                            foreach (var p in doc.RootElement.EnumerateObject()) body[p.Name] = p.Value.Clone();
                            res = await PostAsync("/monitoria/agente/atividade", body, ct);
                        }
                        else res = await SendShotAsync(it, ct);
                    }
                    catch (ApiException ex) when (ex.Status == 404) { _deviceId = null; _store.SetMeta(MetaKey("device"), ""); return; }
                    catch (ApiException) { _outbox.Fail(it); continue; }
                    if (res is null) return; // sem rede — tenta no próximo ciclo
                    _outbox.Done(it);
                    _lastSentUtc = DateTime.UtcNow;
                }
                Raise();
            }
        }
        catch (ApiAuthException) { }
        finally { _drain.Release(); }
    }

    private async Task<JsonElement?> SendShotAsync(MonitorOutbox.Item it, CancellationToken ct)
    {
        if (it.FilePath is null || !File.Exists(it.FilePath)) return default(JsonElement);
        using var doc = JsonDocument.Parse(it.Payload);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_deviceId!), "device_id");
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind != JsonValueKind.Null) form.Add(new StringContent(p.Value.ToString()), p.Name);
        var bytes = await File.ReadAllBytesAsync(it.FilePath, ct);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", Path.GetFileName(it.FilePath));
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("/monitoria/agente/capturas")) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session!.Token);
        try { return await SendAsync(req, ct); }
        catch (ApiException ex) when (ex.Status == 403) { return default(JsonElement); } // capturas desligadas entretanto → descartar
    }

    /// <summary>Identificador estável da máquina (MachineGuid do Windows).</summary>
    private static string MachineId()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g && g.Length > 0) return g;
        }
        catch { }
        return "host-" + Environment.MachineName;
    }
}
