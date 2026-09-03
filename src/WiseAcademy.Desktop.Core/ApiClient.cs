using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WiseAcademy.Desktop.Core;

public sealed class ApiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ApiAuthException(string message) : Exception(message);
public sealed class ApiException(string message, int status) : Exception(message) { public int Status { get; } = status; }

/// <summary>Cliente HTTP do WiseAcademy (mesmo backend do web). Distingue erros
/// de rede (offline) de erros da API (validação, sessão expirada).</summary>
public sealed class ApiClient(AppConfig cfg, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    private readonly AppConfig _cfg = cfg;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string Url(string path) => $"{_cfg.ApiBaseUrl.TrimEnd('/')}{path}";

    /// <summary>Sonda leve para o monitor de conetividade. Qualquer resposta HTTP = online.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url("/"));
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch { return false; }
    }

    public async Task<Session> LoginAsync(string email, string password, string? tenantSlug, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsJsonAsync(Url("/auth/login"),
                new { email, password, tenant_slug = string.IsNullOrWhiteSpace(tenantSlug) ? null : tenantSlug }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiUnavailableException("Sem ligação ao servidor.", ex);
        }

        var doc = await ReadJsonAsync(res, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
            throw new ApiAuthException(Message(doc) ?? "Credenciais inválidas.");
        if (!res.IsSuccessStatusCode)
            throw new ApiException(Message(doc) ?? $"Falha no login ({(int)res.StatusCode}).", (int)res.StatusCode);

        var data = doc.RootElement.GetProperty("data");
        if (data.TryGetProperty("needs_tenant_selection", out _))
            throw new ApiException("A conta existe em várias instituições — defina o slug em Definições.", 409);

        var user = data.GetProperty("user");
        string? tSlug = tenantSlug, tName = null, tJson = null, bJson = null;
        if (data.TryGetProperty("tenant", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            tSlug = t.TryGetProperty("slug", out var s) ? s.GetString() : tSlug;
            tName = t.TryGetProperty("name", out var n) ? n.GetString() : null;
            tJson = t.GetRawText();
        }
        if (data.TryGetProperty("branding", out var b) && b.ValueKind == JsonValueKind.Object)
            bJson = b.GetRawText();

        return new Session
        {
            Token = data.GetProperty("token").GetString()!,
            UserId = user.GetProperty("id").GetString()!,
            UserName = user.GetProperty("name").GetString() ?? email,
            Email = user.TryGetProperty("email", out var e) ? e.GetString() : email,
            TenantSlug = tSlug,
            TenantName = tName,
            UserJson = user.GetRawText(),
            TenantJson = tJson,
            BrandingJson = bJson,
        };
    }

    /// <summary>Envia uma marcação da fila local. Devolve o id remoto.</summary>
    public async Task<string?> SendPunchAsync(Session session, LocalPunch p, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("/rh/ponto/bater"));
        req.Headers.Authorization = new("Bearer", session.Token);
        req.Content = JsonContent.Create(new
        {
            kind = p.Kind.ToApi(),
            at = p.AtUtc.ToString("O"),           // o backend aceita `at` explícito
            source = _cfg.Source,
            device = _cfg.DeviceName,
            note = p.Note,
        });

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiUnavailableException("Sem ligação ao servidor.", ex);
        }

        var doc = await ReadJsonAsync(res, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
            throw new ApiAuthException("Sessão expirada. Inicie sessão novamente.");
        if (!res.IsSuccessStatusCode)
            throw new ApiException(Message(doc) ?? $"Erro da API ({(int)res.StatusCode}).", (int)res.StatusCode);

        return doc.RootElement.TryGetProperty("data", out var d)
            && d.TryGetProperty("punch", out var punch)
            && punch.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
    }

    public async Task<PunchDay?> GetMyDayAsync(Session session, DateOnly date, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url($"/rh/ponto/meu?date={date:yyyy-MM-dd}"));
        req.Headers.Authorization = new("Bearer", session.Token);

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiUnavailableException("Sem ligação ao servidor.", ex);
        }

        var doc = await ReadJsonAsync(res, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
            throw new ApiAuthException("Sessão expirada.");
        if (!res.IsSuccessStatusCode) return null;

        var data = doc.RootElement.GetProperty("data");
        var card = data.GetProperty("card");
        int? I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new PunchDay
        {
            Name = S(data, "name"),
            Department = S(data, "department"),
            Date = S(card, "date") ?? date.ToString("yyyy-MM-dd"),
            Entrada = S(card, "entrada"),
            Saida = S(card, "saida"),
            JornadaPrevista = I(card, "jornada_prevista") ?? 0,
            Trabalhado = I(card, "trabalhado"),
            Extra = I(card, "extra"),
            Atraso = I(card, "atraso"),
            Status = S(card, "status"),
            State = S(card, "state"),
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch { return JsonDocument.Parse("{}"); }
    }

    private static string? Message(JsonDocument doc)
        => doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
}
