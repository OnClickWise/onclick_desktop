namespace WiseAcademy.Desktop.Core;

/// <summary>Modo de funcionamento do posto.</summary>
public enum AppMode
{
    /// <summary>Usa os servidores centrais (web + API online). Requer internet.</summary>
    Web,
    /// <summary>Pilha local completa (API + Postgres embutido + frontend) no próprio
    /// posto. Tudo funciona sem internet; sincroniza com o central quando volta.</summary>
    Desktop,
}

/// <summary>Configuração da aplicação (appsettings.json).</summary>
public sealed class AppConfig
{
    public string ApiBaseUrl { get; set; } = "http://localhost:3002";
    /// <summary>URL da aplicação web completa (carregada no WebView2 quando há ligação).</summary>
    public string WebAppUrl { get; set; } = "http://localhost:3001";
    public string? TenantSlug { get; set; }
    public string DeviceName { get; set; } = Environment.MachineName;
    public string Source { get; set; } = "desktop";
    /// <summary>Intervalo (s) entre sondas de conetividade.</summary>
    public int ConnectivityProbeSeconds { get; set; } = 15;
    /// <summary>Tentativas máximas por marcação antes de marcar como erro permanente.</summary>
    public int MaxSyncAttempts { get; set; } = 8;

    /* ── modo desktop / pilha local ──────────────────────────────── */

    /// <summary>Modo preferido ao arrancar. Sem internet o posto força <see cref="AppMode.Desktop"/>.</summary>
    public AppMode PreferredMode { get; set; } = AppMode.Web;
    /// <summary>Arrancar a pilha local automaticamente ao iniciar (evita espera na 1.ª troca para offline).</summary>
    public bool AutoStartLocalStack { get; set; } = false;

    /// <summary>Agente de monitoria (módulo Monitoria). Só recolhe se a organização o tiver ativo;
    /// false desliga-o neste posto independentemente da política.</summary>
    public bool EnableMonitoring { get; set; } = true;

    /// <summary>Caminho para o <c>node.exe</c>. Vazio = <c>runtime/node/node.exe</c> ao lado do executável, senão o do PATH.</summary>
    public string? NodePath { get; set; }
    /// <summary>Pasta da API (com <c>dist/</c> e <c>node_modules/</c>). Vazio = deteção automática.</summary>
    public string? LocalApiDir { get; set; }
    /// <summary>Pasta do <c>local-backend</c> (com <c>server.mjs</c>). Vazio = deteção automática.</summary>
    public string? LocalBackendDir { get; set; }
    /// <summary>Pasta do frontend <c>standalone</c> (com <c>server.js</c>). Vazio = deteção automática.</summary>
    public string? LocalFrontendDir { get; set; }

    public int LocalApiPort { get; set; } = 4310;
    public int LocalWebPort { get; set; } = 4311;
    public int LocalPgPort { get; set; } = 55432;
    /// <summary>Segredo JWT partilhado com o central (para os tokens centrais valerem na API local).</summary>
    public string? LocalJwtSecret { get; set; }
}

/// <summary>Sessão autenticada, guardada localmente (token cifrado com DPAPI).</summary>
public sealed class Session
{
    public required string Token { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public string? TenantSlug { get; init; }
    public string? TenantName { get; init; }
    /// <summary>JSON cru de <c>data.user</c> / <c>data.tenant</c> / <c>data.branding</c>
    /// devolvidos pelo <c>/auth/login</c> — injetados tal-qual no localStorage do
    /// WebView2 para autenticar a aplicação web sem novo login.</summary>
    public string? UserJson { get; init; }
    public string? TenantJson { get; init; }
    public string? BrandingJson { get; init; }
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum PunchKind { In, Out, BreakStart, BreakEnd }

public static class PunchKindMap
{
    public static string ToApi(this PunchKind k) => k switch
    {
        PunchKind.In => "in",
        PunchKind.Out => "out",
        PunchKind.BreakStart => "break_start",
        PunchKind.BreakEnd => "break_end",
        _ => "in",
    };

    public static string Label(this PunchKind k) => k switch
    {
        PunchKind.In => "Entrada",
        PunchKind.Out => "Saída (fim da jornada)",
        PunchKind.BreakStart => "Saída para o almoço",
        PunchKind.BreakEnd => "Retorno do almoço",
        _ => k.ToString(),
    };
}

public enum SyncState { Pending, Synced, Failed }

/// <summary>Marcação de ponto feita no posto — sempre gravada localmente primeiro.</summary>
public sealed class LocalPunch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PunchKind Kind { get; init; }
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    public string? Note { get; init; }
    public SyncState State { get; set; } = SyncState.Pending;
    public string? RemoteId { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? SyncedAtUtc { get; set; }

    public DateTime AtLocal => AtUtc.ToLocalTime();
}

/// <summary>Resumo do dia devolvido pelo servidor (GET /rh/ponto/meu).</summary>
public sealed class PunchDay
{
    public string? Name { get; set; }
    public string? Department { get; set; }
    public string Date { get; set; } = "";
    public string? Entrada { get; set; }
    public string? Saida { get; set; }
    public int JornadaPrevista { get; set; }
    public int? Trabalhado { get; set; }
    public int? Extra { get; set; }
    public int? Atraso { get; set; }
    public string? Status { get; set; }
    public string? State { get; set; }
}

public sealed class SyncSnapshot
{
    public bool Online { get; set; }
    public int Pending { get; set; }
    public int Failed { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastMessage { get; set; }
}
