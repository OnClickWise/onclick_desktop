using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WiseAcademy.Desktop.Core;

public sealed record UpdateInfo(Version Version, string Url, string? Sha256, long Size, string? Notes);

/// <summary>
/// Atualização automática do posto (porte do AutoUpdateService do agente TimeZoon): lê o manifesto
/// publicado pela API (<c>/downloads/desktop/manifest.json</c>), descarrega o instalador, confirma o
/// SHA-256 e corre-o em modo silencioso (Inno Setup fecha e volta a abrir o posto).
/// </summary>
public sealed class UpdateChecker(AppConfig cfg)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = cfg.ApiBaseUrl.TrimEnd('/');
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{baseUrl}/downloads/desktop/manifest.json", ct));
            var r = doc.RootElement;
            if (!Version.TryParse(r.GetProperty("version").GetString(), out var v)) return null;
            if (v <= new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build))) return null;
            var path = r.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(path)) return null;
            return new UpdateInfo(v, path.StartsWith("http") ? path : baseUrl + path,
                r.TryGetProperty("sha256", out var h) ? h.GetString() : null,
                r.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                r.TryGetProperty("notes", out var n) ? n.GetString() : null);
        }
        catch { return null; } // offline ou manifesto inválido — tenta mais tarde
    }

    /// <summary>Descarrega, valida e arranca o instalador. Devolve true se o posto deve fechar.</summary>
    public async Task<bool> InstallAsync(UpdateInfo u, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"WiseAcademyPosto-Setup-{u.Version}.exe");
        using (var res = await _http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? u.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }
        if (!string.IsNullOrWhiteSpace(u.Sha256))
        {
            await using var fs = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidOperationException("O instalador descarregado não confere (SHA-256). Atualização cancelada.");
            }
        }
        Process.Start(new ProcessStartInfo(file, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART") { UseShellExecute = true });
        return true;
    }
}
