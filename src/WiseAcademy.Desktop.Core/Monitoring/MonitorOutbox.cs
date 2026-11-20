using Dapper;
using Microsoft.Data.Sqlite;

namespace WiseAcademy.Desktop.Core.Monitoring;

/// <summary>
/// Fila local (SQLite, mesma base do posto) dos lotes de atividade e das capturas por enviar.
/// Sem rede o agente continua a registar; quando a ligação volta tudo é enviado por ordem.
/// </summary>
public sealed class MonitorOutbox
{
    private readonly string _conn;
    public string ShotsDir { get; }
    private const int MaxRows = 20_000;

    public MonitorOutbox(string dbPath)
    {
        _conn = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        ShotsDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "monitoria");
        Directory.CreateDirectory(ShotsDir);
        using var c = Open();
        c.Execute("""
            CREATE TABLE IF NOT EXISTS outbox_monitor (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,           -- batch | shot
                payload TEXT NOT NULL,        -- JSON do lote | metadados da captura
                file_path TEXT,
                attempts INTEGER NOT NULL DEFAULT 0,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_monitor ON outbox_monitor(created_at_utc);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_conn); c.Open(); return c; }

    public sealed record Item(string Id, string Kind, string Payload, string? FilePath, long Attempts);

    public void Add(string kind, string payload, string? file = null)
    {
        using var c = Open();
        c.Execute("INSERT INTO outbox_monitor (id, kind, payload, file_path, created_at_utc) VALUES (@id, @kind, @payload, @file, @at)",
            new { id = Guid.NewGuid().ToString(), kind, payload, file, at = DateTime.UtcNow.ToString("O") });
        // limite de segurança: descarta o mais antigo (≈ 2 semanas de atividade)
        var n = c.ExecuteScalar<long>("SELECT count(*) FROM outbox_monitor");
        if (n > MaxRows)
        {
            foreach (var f in c.Query<string?>("SELECT file_path FROM outbox_monitor ORDER BY created_at_utc LIMIT @k", new { k = n - MaxRows }))
                if (f is not null) try { File.Delete(f); } catch { }
            c.Execute("DELETE FROM outbox_monitor WHERE id IN (SELECT id FROM outbox_monitor ORDER BY created_at_utc LIMIT @k)", new { k = n - MaxRows });
        }
    }

    public IReadOnlyList<Item> Next(int max = 20)
    {
        using var c = Open();
        return c.Query<Item>("SELECT id AS Id, kind AS Kind, payload AS Payload, file_path AS FilePath, attempts AS Attempts FROM outbox_monitor ORDER BY created_at_utc LIMIT @max", new { max }).ToList();
    }

    public void Done(Item i)
    {
        using var c = Open();
        c.Execute("DELETE FROM outbox_monitor WHERE id = @id", new { id = i.Id });
        if (i.FilePath is not null) try { File.Delete(i.FilePath); } catch { }
    }

    public void Fail(Item i)
    {
        using var c = Open();
        // recusado pela API muitas vezes (dados inválidos) → descartar para não bloquear a fila
        if (i.Attempts >= 9) { Done(i); return; }
        c.Execute("UPDATE outbox_monitor SET attempts = attempts + 1 WHERE id = @id", new { id = i.Id });
    }

    public int Count()
    {
        using var c = Open();
        return (int)c.ExecuteScalar<long>("SELECT count(*) FROM outbox_monitor");
    }
}
