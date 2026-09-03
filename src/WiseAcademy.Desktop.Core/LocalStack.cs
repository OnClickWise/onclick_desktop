using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WiseAcademy.Desktop.Core;

/// <summary>
/// Pilha local do posto (modo desktop / offline). Arranca, em processos Node
/// filhos, a <b>mesma</b> API NestJS contra uma base Postgres embutida
/// (PGlite via <c>local-backend/server.mjs</c>) e o <b>mesmo</b> frontend
/// Next.js (build <c>standalone</c>). O WebView2 do posto passa então a
/// apontar para estes serviços locais — tudo o que se faz na web fica
/// disponível sem internet.
/// </summary>
public sealed class LocalStack : IDisposable
{
    private readonly AppConfig _cfg;
    private Process? _backend;
    private Process? _frontend;
    private readonly StringBuilder _tail = new();

    /// <summary>URL da API local (ex.: <c>http://127.0.0.1:4310</c>), disponível após <see cref="StartAsync"/>.</summary>
    public string? ApiUrl { get; private set; }

    /// <summary>URL do frontend local (ex.: <c>http://127.0.0.1:4311</c>), disponível após <see cref="StartAsync"/>.</summary>
    public string? WebUrl { get; private set; }

    public bool IsRunning => _backend is { HasExited: false } && _frontend is { HasExited: false };

    /// <summary>Últimas linhas de diagnóstico dos processos locais.</summary>
    public string Diagnostics => _tail.ToString();

    public event Action<string>? Log;

    public LocalStack(AppConfig cfg) => _cfg = cfg;

    /* ── localização dos artefactos ──────────────────────────────── */

    private static string FirstExisting(params string[] candidates)
        => candidates.FirstOrDefault(Directory.Exists) ?? candidates[^1];

    private string ResolveNode()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.NodePath) && File.Exists(_cfg.NodePath))
            return _cfg.NodePath!;
        // node embarcado ao lado do executável (instalador), senão o do PATH
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "node", "node.exe");
        return File.Exists(bundled) ? bundled : "node";
    }

    private string ResolveApiDir() => FirstExisting(
        _cfg.LocalApiDir ?? "",
        Path.Combine(AppContext.BaseDirectory, "stack", "api"),
        Path.Combine(RepoRoot(), "api"));

    private string ResolveBackendScript()
    {
        var dir = FirstExisting(
            _cfg.LocalBackendDir ?? "",
            Path.Combine(AppContext.BaseDirectory, "stack", "local-backend"),
            Path.Combine(RepoRoot(), "desktop", "local-backend"));
        return Path.Combine(dir, "server.mjs");
    }

    private string ResolveFrontendStandalone() => FirstExisting(
        _cfg.LocalFrontendDir ?? "",
        Path.Combine(AppContext.BaseDirectory, "stack", "frontend"),
        Path.Combine(RepoRoot(), "wiseacademy_front", ".next", "standalone"));

    private string ResolveFrontendSource() => FirstExisting(
        Path.Combine(RepoRoot(), "wiseacademy_front"),
        Path.Combine(AppContext.BaseDirectory, "stack", "frontend"));

    private static string RepoRoot()
    {
        // dev: .../desktop/src/WiseAcademy.Desktop/bin/<cfg>/net9.0-windows/
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && d is not null; i++, d = d.Parent)
            if (d.GetDirectories("api").Length > 0 && d.GetDirectories("wiseacademy_front").Length > 0)
                return d.FullName;
        return AppContext.BaseDirectory;
    }

    private string DataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WiseAcademy", "Desktop", "stack-data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /* ── arranque ────────────────────────────────────────────────── */

    public async Task StartAsync(Session session, CancellationToken ct = default)
    {
        if (IsRunning) return;

        var node = ResolveNode();
        var script = ResolveBackendScript();
        var apiDir = ResolveApiDir();
        if (!File.Exists(script))
            throw new LocalStackException($"backend local não encontrado: {script}");
        if (!File.Exists(Path.Combine(apiDir, "dist", "src", "main.js")))
            throw new LocalStackException($"API não compilada em {apiDir} (falta dist/src/main.js — correr \"npm run build\").");

        var (tenantId, tenantSlug, tenantName) = TenantFrom(session);
        var userB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(session.UserJson ?? "{}"));

        Emit($"a arrancar backend local (API :{_cfg.LocalApiPort}, Postgres :{_cfg.LocalPgPort})…");
        _backend = SpawnNode(node, new[]
        {
            script,
            "--api-dir", apiDir,
            "--data-dir", DataDir(),
            "--pg-port", _cfg.LocalPgPort.ToString(),
            "--api-port", _cfg.LocalApiPort.ToString(),
            "--tenant-id", tenantId,
            "--tenant-slug", tenantSlug,
            "--tenant-name", tenantName,
            "--user-b64", userB64,
            "--jwt-secret", string.IsNullOrWhiteSpace(_cfg.LocalJwtSecret) ? "wiseacademy_secret_2024" : _cfg.LocalJwtSecret!,
        }, workDir: Path.GetDirectoryName(script)!, onStdout: line =>
        {
            if (line.StartsWith("READY ", StringComparison.Ordinal))
                ApiUrl = line["READY ".Length..].Trim();
        });

        // espera pelo READY do backend (migrações + API a responder)
        await WaitAsync(() => ApiUrl is not null, _backend, TimeSpan.FromSeconds(180),
            "o backend local não ficou pronto", ct);
        Emit($"backend local pronto: {ApiUrl}");

        await StartFrontendAsync(node, ct);
        Emit($"frontend local pronto: {WebUrl}");
    }

    private async Task StartFrontendAsync(string node, CancellationToken ct)
    {
        var standalone = ResolveFrontendStandalone();
        var serverJs = Path.Combine(standalone, "server.js");
        if (!File.Exists(serverJs))
            throw new LocalStackException(
                $"frontend \"standalone\" não encontrado: {serverJs} (correr \"npm run build\" em wiseacademy_front).");

        EnsureStandaloneAssets(standalone);

        var port = _cfg.LocalWebPort;
        WebUrl = $"http://127.0.0.1:{port}";
        Emit($"a arrancar frontend local em :{port}…");
        _frontend = SpawnNode(node, new[] { serverJs }, workDir: standalone, env: new()
        {
            ["PORT"] = port.ToString(),
            ["HOSTNAME"] = "127.0.0.1",
            ["NODE_ENV"] = "production",
            // a API é sempre injetada em runtime por window.__WA_API_URL;
            // este valor é apenas o fallback de SSR.
            ["NEXT_PUBLIC_API_URL"] = ApiUrl ?? $"http://127.0.0.1:{_cfg.LocalApiPort}",
        });

        await WaitAsync(async () => await HttpOkAsync(WebUrl!), _frontend, TimeSpan.FromSeconds(60),
            "o frontend local não respondeu", ct);
    }

    /// <summary>O build <c>standalone</c> do Next não inclui <c>.next/static</c>
    /// nem <c>public/</c>; em dev copiamo-los da árvore de origem.</summary>
    private void EnsureStandaloneAssets(string standalone)
    {
        try
        {
            var src = ResolveFrontendSource();
            var staticDst = Path.Combine(standalone, ".next", "static");
            var staticSrc = Path.Combine(src, ".next", "static");
            if (Directory.Exists(staticSrc) && !Directory.Exists(staticDst))
                CopyDir(staticSrc, staticDst);

            var pubDst = Path.Combine(standalone, "public");
            var pubSrc = Path.Combine(src, "public");
            if (Directory.Exists(pubSrc) && !Directory.Exists(pubDst))
                CopyDir(pubSrc, pubDst);
        }
        catch (Exception ex) { Emit("aviso: não foi possível preparar assets do frontend — " + ex.Message); }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /* ── infra de processos ──────────────────────────────────────── */

    private Process SpawnNode(string node, string[] argv, string workDir,
        Dictionary<string, string>? env = null, Action<string>? onStdout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = node,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; Tail(e.Data); onStdout?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Tail(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task WaitAsync(Func<bool> ready, Process proc, TimeSpan timeout, string failMsg, CancellationToken ct)
        => await WaitAsync(() => Task.FromResult(ready()), proc, timeout, failMsg, ct);

    private static async Task WaitAsync(Func<Task<bool>> ready, Process proc, TimeSpan timeout, string failMsg, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new LocalStackException($"{failMsg} (o processo terminou com código {proc.ExitCode}).");
            if (await ready()) return;
            await Task.Delay(400, ct);
        }
        throw new LocalStackException($"{failMsg} (tempo esgotado).");
    }

    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static async Task<bool> HttpOkAsync(string url)
    {
        try { using var r = await _probe.GetAsync(url); return true; }
        catch { return false; }
    }

    private static (string id, string slug, string name) TenantFrom(Session s)
    {
        string id = "", slug = s.TenantSlug ?? "posto", name = s.TenantName ?? s.TenantSlug ?? "Posto";
        try
        {
            if (!string.IsNullOrWhiteSpace(s.TenantJson))
            {
                using var doc = JsonDocument.Parse(s.TenantJson!);
                var r = doc.RootElement;
                if (r.TryGetProperty("id", out var v)) id = v.GetString() ?? "";
                if (r.TryGetProperty("slug", out var sv) && sv.GetString() is { Length: > 0 } ss) slug = ss;
                if (r.TryGetProperty("name", out var nv) && nv.GetString() is { Length: > 0 } nn) name = nn;
            }
        }
        catch { /* usa defaults */ }
        return (id, slug, name);
    }

    private void Tail(string line)
    {
        lock (_tail)
        {
            _tail.AppendLine(line);
            if (_tail.Length > 8000) _tail.Remove(0, _tail.Length - 8000);
        }
    }

    private void Emit(string m) { Tail("[local-stack] " + m); try { Log?.Invoke(m); } catch { } }

    /* ── paragem ─────────────────────────────────────────────────── */

    public void Stop()
    {
        foreach (var p in new[] { _frontend, _backend })
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }
        _frontend = _backend = null;
        ApiUrl = WebUrl = null;
    }

    public void Dispose() => Stop();
}

public sealed class LocalStackException(string message) : Exception(message);
