using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace WiseAcademy.Desktop.Core;

/// <summary>
/// Armazenamento local (SQLite) — funciona 100% offline. Guarda a sessão
/// (token cifrado com DPAPI/utilizador) e a fila de marcações por sincronizar
/// (padrão <i>outbox</i>).
/// </summary>
public sealed class LocalStore
{
    private readonly string _connString;

    public string DbPath { get; }

    public LocalStore(string? dbPath = null)
    {
        DbPath = dbPath ?? DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
        Init();
    }

    public static string DefaultDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WiseAcademy", "Desktop", "local.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        c.Execute("""
            CREATE TABLE IF NOT EXISTS session (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                token_enc BLOB NOT NULL,
                user_id TEXT NOT NULL,
                user_name TEXT NOT NULL,
                email TEXT,
                tenant_slug TEXT,
                tenant_name TEXT,
                user_json TEXT,
                tenant_json TEXT,
                branding_json TEXT,
                saved_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox_punch (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                at_utc TEXT NOT NULL,
                note TEXT,
                state INTEGER NOT NULL DEFAULT 0,   -- 0 Pending | 1 Synced | 2 Failed
                remote_id TEXT,
                last_error TEXT,
                attempts INTEGER NOT NULL DEFAULT 0,
                created_at_utc TEXT NOT NULL,
                synced_at_utc TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_state ON outbox_punch(state, at_utc);

            CREATE TABLE IF NOT EXISTS meta (
                k TEXT PRIMARY KEY,
                v TEXT
            );
            """);

        // migrações defensivas para bases criadas por versões anteriores
        foreach (var col in new[] { "user_json", "tenant_json", "branding_json" })
            try { c.Execute($"ALTER TABLE session ADD COLUMN {col} TEXT"); } catch { /* já existe */ }
    }

    /* ── sessão ──────────────────────────────────────────────────────── */

    public void SaveSession(Session s)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO session (id, token_enc, user_id, user_name, email, tenant_slug, tenant_name,
                                 user_json, tenant_json, branding_json, saved_at_utc)
            VALUES (1, @tok, @uid, @name, @mail, @slug, @tname, @ujson, @tjson, @bjson, @saved)
            ON CONFLICT(id) DO UPDATE SET
                token_enc=@tok, user_id=@uid, user_name=@name, email=@mail,
                tenant_slug=@slug, tenant_name=@tname, user_json=@ujson,
                tenant_json=@tjson, branding_json=@bjson, saved_at_utc=@saved;
            """,
            new
            {
                tok = Protect(s.Token),
                uid = s.UserId,
                name = s.UserName,
                mail = s.Email,
                slug = s.TenantSlug,
                tname = s.TenantName,
                ujson = s.UserJson,
                tjson = s.TenantJson,
                bjson = s.BrandingJson,
                saved = s.SavedAtUtc.ToString("O"),
            });
    }

    public Session? GetSession()
    {
        using var c = Open();
        var row = c.QuerySingleOrDefault("SELECT * FROM session WHERE id = 1");
        if (row is null) return null;
        string token;
        try { token = Unprotect((byte[])row.token_enc); }
        catch { return null; } // cifra de outro utilizador/máquina — sessão inválida
        return new Session
        {
            Token = token,
            UserId = (string)row.user_id,
            UserName = (string)row.user_name,
            Email = row.email as string,
            TenantSlug = row.tenant_slug as string,
            TenantName = row.tenant_name as string,
            UserJson = row.user_json as string,
            TenantJson = row.tenant_json as string,
            BrandingJson = row.branding_json as string,
            SavedAtUtc = DateTime.Parse((string)row.saved_at_utc, null, System.Globalization.DateTimeStyles.RoundtripKind),
        };
    }

    public void ClearSession()
    {
        using var c = Open();
        c.Execute("DELETE FROM session WHERE id = 1");
    }

    /* ── outbox de marcações ─────────────────────────────────────────── */

    public void Enqueue(LocalPunch p)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO outbox_punch (id, kind, at_utc, note, state, attempts, created_at_utc)
            VALUES (@id, @kind, @at, @note, 0, 0, @created);
            """,
            new
            {
                id = p.Id.ToString(),
                kind = p.Kind.ToApi(),
                at = p.AtUtc.ToString("O"),
                note = p.Note,
                created = p.CreatedAtUtc.ToString("O"),
            });
    }

    public IReadOnlyList<LocalPunch> GetPending(int max = 200)
    {
        using var c = Open();
        return c.Query("SELECT * FROM outbox_punch WHERE state = 0 ORDER BY at_utc ASC LIMIT @max", new { max })
                .Select(Map).ToList();
    }

    public IReadOnlyList<LocalPunch> GetRecent(int take = 50)
    {
        using var c = Open();
        return c.Query("SELECT * FROM outbox_punch ORDER BY at_utc DESC LIMIT @take", new { take })
                .Select(Map).ToList();
    }

    public (int pending, int failed) Counts()
    {
        using var c = Open();
        var p = c.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox_punch WHERE state = 0");
        var f = c.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox_punch WHERE state = 2");
        return (p, f);
    }

    public void MarkSynced(Guid id, string? remoteId)
    {
        using var c = Open();
        c.Execute("""
            UPDATE outbox_punch SET state = 1, remote_id = @rid, last_error = NULL,
                synced_at_utc = @now WHERE id = @id;
            """,
            new { id = id.ToString(), rid = remoteId, now = DateTime.UtcNow.ToString("O") });
    }

    public void MarkFailed(Guid id, string error, int attempts, bool permanent)
    {
        using var c = Open();
        c.Execute("""
            UPDATE outbox_punch SET state = @state, last_error = @err, attempts = @att WHERE id = @id;
            """,
            new { id = id.ToString(), state = permanent ? 2 : 0, err = error, att = attempts });
    }

    /// <summary>Reenfileira uma marcação que ficou em erro permanente (ação do utilizador).</summary>
    public void Retry(Guid id)
    {
        using var c = Open();
        c.Execute("UPDATE outbox_punch SET state = 0, attempts = 0, last_error = NULL WHERE id = @id",
            new { id = id.ToString() });
    }

    /* ── meta / chave-valor ──────────────────────────────────────────── */

    public string? GetMeta(string key)
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("SELECT v FROM meta WHERE k = @key", new { key });
    }

    public void SetMeta(string key, string value)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO meta (k, v) VALUES (@key, @val)
            ON CONFLICT(k) DO UPDATE SET v = @val;
            """, new { key, val = value });
    }

    public DateTime? LastSyncUtc
    {
        get => DateTime.TryParse(GetMeta("last_sync_utc"), null,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        set { if (value is { } v) SetMeta("last_sync_utc", v.ToString("O")); }
    }

    /* ── helpers ─────────────────────────────────────────────────────── */

    private static LocalPunch Map(dynamic r) => new()
    {
        Id = Guid.Parse((string)r.id),
        Kind = FromApi((string)r.kind),
        AtUtc = DateTime.Parse((string)r.at_utc, null, System.Globalization.DateTimeStyles.RoundtripKind),
        Note = r.note as string,
        State = (SyncState)(int)(long)r.state,
        RemoteId = r.remote_id as string,
        LastError = r.last_error as string,
        Attempts = (int)(long)r.attempts,
        CreatedAtUtc = DateTime.Parse((string)r.created_at_utc, null, System.Globalization.DateTimeStyles.RoundtripKind),
        SyncedAtUtc = r.synced_at_utc is string s
            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : null,
    };

    private static PunchKind FromApi(string s) => s switch
    {
        "in" => PunchKind.In,
        "out" => PunchKind.Out,
        "break_start" => PunchKind.BreakStart,
        "break_end" => PunchKind.BreakEnd,
        _ => PunchKind.In,
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WiseAcademy.Desktop.v1");

    private static byte[] Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        try { return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch (PlatformNotSupportedException) { return data; } // fora do Windows (testes)
    }

    private static string Unprotect(byte[] blob)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)); }
        catch (PlatformNotSupportedException) { return Encoding.UTF8.GetString(blob); }
    }
}
