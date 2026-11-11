using System.Globalization;
using System.Text.Json;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using Microsoft.Data.Sqlite;

namespace HomelabMonitor.Store;

public sealed record SampleRow(DateTimeOffset At, string CheckKey, CheckStatus Status, long DurationMs, CheckSource Source, bool Maintenance);

public sealed record IncidentRecord(
    string Id, string CheckKey, string CheckId, Severity Severity, CheckState State,
    DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<string> Affected, string Message);

public sealed record OutboxMessage(
    long Id, string DedupeKey, string? IncidentId, string Kind, string Priority, string Tags, string Text,
    DateTimeOffset CreatedAt, string Status, int Attempts, DateTimeOffset NextAttemptAt);

public sealed record DiskSample(DateTimeOffset At, string Volume, double FreeGb, double TotalGb);

public sealed record StoredReport(string Type, DateTimeOffset GeneratedAt, DateTimeOffset PeriodEnd, string Summary);

public sealed record UptimeCounts(long Ok, long Warn, long Error, long Unknown, long Maintenance);

/// <summary>SQLite store (Microsoft.Data.Sqlite, no EF). Every operation uses its own short-lived connection.</summary>
public sealed class SqliteStore
{
    public const int SchemaVersion = 2;

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE samples (ts INTEGER NOT NULL, check_key TEXT NOT NULL, status TEXT NOT NULL, duration_ms INTEGER NOT NULL,
            source TEXT NOT NULL, maintenance INTEGER NOT NULL);
        CREATE INDEX ix_samples_check_ts ON samples(check_key, ts);
        CREATE INDEX ix_samples_ts ON samples(ts);
        CREATE TABLE rollups (check_key TEXT NOT NULL, hour INTEGER NOT NULL, ok INTEGER NOT NULL, warn INTEGER NOT NULL,
            error INTEGER NOT NULL, unknown INTEGER NOT NULL, maintenance INTEGER NOT NULL, PRIMARY KEY (check_key, hour));
        CREATE TABLE incidents (id TEXT PRIMARY KEY, check_key TEXT NOT NULL, check_id TEXT NOT NULL, severity TEXT NOT NULL,
            state TEXT NOT NULL, opened_at INTEGER NOT NULL, resolved_at INTEGER NULL, affected TEXT NOT NULL, message TEXT NOT NULL);
        CREATE INDEX ix_incidents_state ON incidents(state);
        CREATE TABLE incident_events (id INTEGER PRIMARY KEY AUTOINCREMENT, incident_id TEXT NOT NULL, ts INTEGER NOT NULL,
            check_key TEXT NOT NULL, kind TEXT NOT NULL, from_state TEXT NOT NULL, to_state TEXT NOT NULL, severity TEXT NOT NULL, message TEXT NOT NULL);
        CREATE INDEX ix_incident_events_inc ON incident_events(incident_id);
        CREATE TABLE outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, dedupe_key TEXT NOT NULL UNIQUE, incident_id TEXT NULL, kind TEXT NOT NULL,
            priority TEXT NOT NULL, tags TEXT NOT NULL, text TEXT NOT NULL, created_at INTEGER NOT NULL, status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at INTEGER NOT NULL, sent_at INTEGER NULL, last_error TEXT NULL);
        CREATE INDEX ix_outbox_status ON outbox(status, next_attempt_at);
        CREATE TABLE processed_heavy_runs (run_id TEXT PRIMARY KEY, started_at INTEGER NOT NULL, finished_at INTEGER NOT NULL,
            ingested_at INTEGER NOT NULL, applied INTEGER NOT NULL);
        CREATE TABLE check_state (check_key TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
        """
        CREATE TABLE disk_samples (ts INTEGER NOT NULL, volume TEXT NOT NULL, free_gb REAL NOT NULL, total_gb REAL NOT NULL,
            PRIMARY KEY (volume, ts));
        CREATE TABLE reports (id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, generated_at INTEGER NOT NULL,
            period_end INTEGER NOT NULL, summary TEXT NOT NULL, text TEXT NOT NULL);
        CREATE INDEX ix_reports_type ON reports(type, period_end);
        """,
    ];

    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly string _connectionString;

    public SqliteStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static long Ts(DateTimeOffset t) => t.ToUnixTimeSeconds();

    private static DateTimeOffset FromTs(long t) => DateTimeOffset.FromUnixTimeSeconds(t);

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    // ---- schema ----

    public int Migrate()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL)");
        var current = Convert.ToInt32(Scalar(c, "SELECT COALESCE(MAX(version),0) FROM schema_version"), CultureInfo.InvariantCulture);
        for (var v = current; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO schema_version(version, applied_at) VALUES ($v, $t)";
            cmd.Parameters.AddWithValue("$v", v + 1);
            cmd.Parameters.AddWithValue("$t", Ts(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        return Migrations.Length;
    }

    public int CurrentVersion()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COALESCE(MAX(version),0) FROM schema_version"), CultureInfo.InvariantCulture);
    }

    public bool CheckWritable(DateTimeOffset now)
    {
        try
        {
            SetMeta("db_write_probe", Ts(now).ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ---- meta ----

    public string? GetMeta(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT value FROM meta WHERE key=$k", ("$k", key)) as string;
    }

    public void SetMeta(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
    }

    // ---- samples ----

    public void InsertSamples(IEnumerable<SampleRow> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var s in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO samples(ts,check_key,status,duration_ms,source,maintenance) VALUES($t,$k,$s,$d,$src,$m)";
            cmd.Parameters.AddWithValue("$t", Ts(s.At));
            cmd.Parameters.AddWithValue("$k", s.CheckKey);
            cmd.Parameters.AddWithValue("$s", s.Status.ToText());
            cmd.Parameters.AddWithValue("$d", s.DurationMs);
            cmd.Parameters.AddWithValue("$src", s.Source == CheckSource.Light ? "light" : "heavy");
            cmd.Parameters.AddWithValue("$m", s.Maintenance ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public UptimeCounts GetUptimeCounts(string checkKey, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN maintenance=0 AND status='OK' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN maintenance=0 AND status='WARN' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN maintenance=0 AND status='ERROR' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN maintenance=0 AND status='UNKNOWN' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN maintenance=1 THEN 1 ELSE 0 END),0)
            FROM samples WHERE check_key=$k AND ts>=$f AND ts<$t
            """;
        cmd.Parameters.AddWithValue("$k", checkKey);
        cmd.Parameters.AddWithValue("$f", Ts(from));
        cmd.Parameters.AddWithValue("$t", Ts(to));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new UptimeCounts(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    public DateTimeOffset? LastSampleAt(CheckSource source)
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MAX(ts) FROM samples WHERE source=$s", ("$s", source == CheckSource.Light ? "light" : "heavy"));
        return v is null ? null : FromTs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    public void Rollup(DateTimeOffset now)
    {
        var from = Ts(now) / 3600 * 3600 - 3 * 3600;
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO rollups(check_key,hour,ok,warn,error,unknown,maintenance)
            SELECT check_key, ts/3600*3600,
                SUM(CASE WHEN maintenance=0 AND status='OK' THEN 1 ELSE 0 END),
                SUM(CASE WHEN maintenance=0 AND status='WARN' THEN 1 ELSE 0 END),
                SUM(CASE WHEN maintenance=0 AND status='ERROR' THEN 1 ELSE 0 END),
                SUM(CASE WHEN maintenance=0 AND status='UNKNOWN' THEN 1 ELSE 0 END),
                SUM(CASE WHEN maintenance=1 THEN 1 ELSE 0 END)
            FROM samples WHERE ts>=$f GROUP BY check_key, ts/3600*3600
            """, ("$f", from));
    }

    public void Purge(DateTimeOffset now)
    {
        using var c = Open();
        Exec(c, "DELETE FROM samples WHERE ts<$t", ("$t", Ts(now.AddDays(-30))));
        Exec(c, "DELETE FROM rollups WHERE hour<$t", ("$t", Ts(now.AddDays(-365))));
        Exec(c, "DELETE FROM disk_samples WHERE ts<$t", ("$t", Ts(now.AddDays(-365))));
        Exec(c, "DELETE FROM outbox WHERE status<>'pending' AND created_at<$t", ("$t", Ts(now.AddDays(-90))));
    }

    public IReadOnlyList<(string CheckKey, long Hour, UptimeCounts Counts)> GetRollups(string checkKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT check_key,hour,ok,warn,error,unknown,maintenance FROM rollups WHERE check_key=$k ORDER BY hour";
        cmd.Parameters.AddWithValue("$k", checkKey);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, long, UptimeCounts)>();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetInt64(1), new UptimeCounts(r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6))));
        }

        return list;
    }

    // ---- state ----

    public void SaveStates(IEnumerable<CheckRuntime> states)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var s in states)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO check_state(check_key,json) VALUES($k,$j) ON CONFLICT(check_key) DO UPDATE SET json=$j";
            cmd.Parameters.AddWithValue("$k", s.Key);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s, Json));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<CheckRuntime> LoadStates()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM check_state";
        using var r = cmd.ExecuteReader();
        var list = new List<CheckRuntime>();
        while (r.Read())
        {
            var s = JsonSerializer.Deserialize<CheckRuntime>(r.GetString(0), Json);
            if (s is not null)
            {
                list.Add(s);
            }
        }

        return list;
    }

    // ---- incidensek ----

    public void ApplyEvents(IEnumerable<EngineEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in events.Where(e => e.IncidentId is not null))
        {
            var sev = e.Severity.ToString().ToUpperInvariant();
            Exec(c, """
                INSERT INTO incident_events(incident_id,ts,check_key,kind,from_state,to_state,severity,message)
                VALUES($i,$t,$k,$kind,$f,$to,$s,$m)
                """, ("$i", e.IncidentId), ("$t", Ts(e.At)), ("$k", e.CheckKey), ("$kind", e.Kind.ToString()),
                ("$f", Name(e.From)), ("$to", Name(e.To)), ("$s", sev), ("$m", e.Message));

            var owns = e.CheckKey == e.OwnerKey;
            var affected = JsonSerializer.Serialize(e.Affected);
            if (e.Kind == EngineEventKind.Transition && owns && e.NewIncident)
            {
                Exec(c, """
                    INSERT OR REPLACE INTO incidents(id,check_key,check_id,severity,state,opened_at,resolved_at,affected,message)
                    VALUES($i,$k,$cid,$s,'OPEN',$o,NULL,$a,$m)
                    """, ("$i", e.IncidentId), ("$k", e.CheckKey), ("$cid", e.CheckId), ("$s", sev),
                    ("$o", Ts(e.Since ?? e.At)), ("$a", affected), ("$m", e.Message));
            }
            else if (e.Kind == EngineEventKind.Transition && owns)
            {
                var closed = e.To is CheckState.Resolved or CheckState.Suppressed;
                Exec(c, "UPDATE incidents SET state=$st, severity=$s, affected=$a, resolved_at=CASE WHEN $closed=1 THEN $t ELSE resolved_at END WHERE id=$i",
                    ("$st", Name(e.To)), ("$s", sev), ("$a", affected), ("$closed", closed ? 1 : 0), ("$t", Ts(e.At)), ("$i", e.IncidentId));
            }
            else if (e.Kind == EngineEventKind.Escalation)
            {
                Exec(c, "UPDATE incidents SET severity=$s WHERE id=$i", ("$s", sev), ("$i", e.IncidentId));
            }
            else if (e.Kind == EngineEventKind.Affected)
            {
                Exec(c, "UPDATE incidents SET affected=$a WHERE id=$i", ("$a", affected), ("$i", e.IncidentId));
            }
        }

        tx.Commit();
    }

    private static string Name(CheckState s) => s.ToString().ToUpperInvariant();

    public IReadOnlyList<IncidentRecord> GetIncidentsForPlanning(DateTimeOffset resolvedSince)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,check_key,check_id,severity,state,opened_at,resolved_at,affected,message FROM incidents
            WHERE state IN ('OPEN','RECOVERING','FLAPPING') OR (state IN ('RESOLVED','SUPPRESSED') AND resolved_at>=$s) ORDER BY opened_at
            """;
        cmd.Parameters.AddWithValue("$s", Ts(resolvedSince));
        return ReadIncidents(cmd);
    }

    public IReadOnlyList<IncidentRecord> GetOpenIncidents()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,check_key,check_id,severity,state,opened_at,resolved_at,affected,message FROM incidents
            WHERE state IN ('OPEN','RECOVERING','FLAPPING') ORDER BY opened_at
            """;
        return ReadIncidents(cmd);
    }

    private static List<IncidentRecord> ReadIncidents(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<IncidentRecord>();
        while (r.Read())
        {
            list.Add(new IncidentRecord(
                r.GetString(0), r.GetString(1), r.GetString(2),
                Enum.Parse<Severity>(r.GetString(3), ignoreCase: true),
                Enum.Parse<CheckState>(r.GetString(4), ignoreCase: true),
                FromTs(r.GetInt64(5)), r.IsDBNull(6) ? null : FromTs(r.GetInt64(6)),
                JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? [], r.GetString(8)));
        }

        return list;
    }

    public IReadOnlyList<string> GetAllIncidentIds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM incidents";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    // ---- outbox ----

    public bool OutboxKeyExists(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT 1 FROM outbox WHERE dedupe_key=$k", ("$k", key)) is not null;
    }

    /// <summary>The status of the outbox row with this dedupe key, or null if there is none.</summary>
    public string? OutboxStatus(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT status FROM outbox WHERE dedupe_key=$k", ("$k", key)) as string;
    }

    /// <summary>Once per dedupe key: false if it already exists.</summary>
    public bool TryEnqueue(string dedupeKey, string? incidentId, string kind, string priority, string tags, string text,
        DateTimeOffset now, string status = "pending")
    {
        using var c = Open();
        return Exec(c, """
            INSERT OR IGNORE INTO outbox(dedupe_key,incident_id,kind,priority,tags,text,created_at,status,attempts,next_attempt_at)
            VALUES($k,$i,$kind,$p,$tags,$text,$t,$s,0,$t)
            """, ("$k", dedupeKey), ("$i", incidentId), ("$kind", kind), ("$p", priority), ("$tags", tags), ("$text", text),
            ("$t", Ts(now)), ("$s", status)) > 0;
    }

    public int CountOutbox(string kind, DateTimeOffset createdSince)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM outbox WHERE kind=$k AND created_at>=$t", ("$k", kind), ("$t", Ts(createdSince))),
            CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<OutboxMessage> GetOutbox(string? kind = null, string? status = null, DateTimeOffset? createdSince = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id,dedupe_key,incident_id,kind,priority,tags,text,created_at,status,attempts,next_attempt_at FROM outbox
            WHERE ($k IS NULL OR kind=$k) AND ($s IS NULL OR status=$s) AND created_at>=$t ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$k", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", createdSince is null ? 0 : Ts(createdSince.Value));
        return ReadOutbox(cmd);
    }

    public IReadOnlyList<OutboxMessage> GetDue(DateTimeOffset now, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,dedupe_key,incident_id,kind,priority,tags,text,created_at,status,attempts,next_attempt_at FROM outbox
            WHERE status='pending' AND next_attempt_at<=$n ORDER BY id LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$n", Ts(now));
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadOutbox(cmd);
    }

    private static List<OutboxMessage> ReadOutbox(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxMessage>();
        while (r.Read())
        {
            list.Add(new OutboxMessage(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), FromTs(r.GetInt64(7)), r.GetString(8), r.GetInt32(9), FromTs(r.GetInt64(10))));
        }

        return list;
    }

    public void SetOutboxStatus(long id, string status)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET status=$s WHERE id=$i", ("$s", status), ("$i", id));
    }

    public void MarkSent(long id, DateTimeOffset now)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET status='sent', sent_at=$t, attempts=attempts+1 WHERE id=$i", ("$t", Ts(now)), ("$i", id));
    }

    public void MarkFailed(long id, DateTimeOffset nextAttempt, string error)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET attempts=attempts+1, next_attempt_at=$n, last_error=$e WHERE id=$i",
            ("$n", Ts(nextAttempt)), ("$e", error), ("$i", id));
    }

    public int CountSentSince(DateTimeOffset since)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM outbox WHERE status='sent' AND sent_at>=$t", ("$t", Ts(since))), CultureInfo.InvariantCulture);
    }

    public int ExpirePending(DateTimeOffset olderThan)
    {
        using var c = Open();
        return Exec(c, "UPDATE outbox SET status='expired' WHERE status='pending' AND created_at<$t", ("$t", Ts(olderThan)));
    }

    public DateTimeOffset? OldestDuePending(DateTimeOffset now)
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MIN(next_attempt_at) FROM outbox WHERE status='pending' AND next_attempt_at<=$n", ("$n", Ts(now)));
        return v is null ? null : FromTs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    // ---- heavy runs ----

    public bool IsRunProcessed(string runId)
    {
        using var c = Open();
        return Scalar(c, "SELECT 1 FROM processed_heavy_runs WHERE run_id=$r", ("$r", runId)) is not null;
    }

    public void MarkRunProcessed(string runId, DateTimeOffset startedAt, DateTimeOffset finishedAt, DateTimeOffset now, bool applied)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO processed_heavy_runs(run_id,started_at,finished_at,ingested_at,applied) VALUES($r,$s,$f,$i,$a)",
            ("$r", runId), ("$s", Ts(startedAt)), ("$f", Ts(finishedAt)), ("$i", Ts(now)), ("$a", applied ? 1 : 0));
    }

    public DateTimeOffset? LastHeavyFinishedAt()
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MAX(finished_at) FROM processed_heavy_runs");
        return v is null ? null : FromTs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    // ---- disk samples ----

    public void InsertDiskSample(DateTimeOffset at, string volume, double freeGb, double totalGb)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO disk_samples(ts,volume,free_gb,total_gb) VALUES($t,$v,$f,$tot)",
            ("$t", Ts(at)), ("$v", volume), ("$f", freeGb), ("$tot", totalGb));
    }

    public DateTimeOffset? LastDiskSampleAt()
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MAX(ts) FROM disk_samples");
        return v is null ? null : FromTs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<DiskSample> GetDiskSamples(string volume, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,free_gb,total_gb FROM disk_samples WHERE volume=$v AND ts>=$f AND ts<=$t ORDER BY ts";
        cmd.Parameters.AddWithValue("$v", volume);
        cmd.Parameters.AddWithValue("$f", Ts(from));
        cmd.Parameters.AddWithValue("$t", Ts(to));
        using var r = cmd.ExecuteReader();
        var list = new List<DiskSample>();
        while (r.Read())
        {
            list.Add(new DiskSample(FromTs(r.GetInt64(0)), volume, r.GetDouble(1), r.GetDouble(2)));
        }

        return list;
    }

    // ---- riportok (M3) ----

    public void InsertReport(string type, DateTimeOffset generatedAt, DateTimeOffset periodEnd, string summary, string text)
    {
        using var c = Open();
        Exec(c, "INSERT INTO reports(type,generated_at,period_end,summary,text) VALUES($ty,$g,$p,$s,$x)",
            ("$ty", type), ("$g", Ts(generatedAt)), ("$p", Ts(periodEnd)), ("$s", summary), ("$x", text));
    }

    public StoredReport? LastReport(string type)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type,generated_at,period_end,summary FROM reports WHERE type=$t ORDER BY period_end DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$t", type);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new StoredReport(r.GetString(0), FromTs(r.GetInt64(1)), FromTs(r.GetInt64(2)), r.GetString(3)) : null;
    }

    public DateTimeOffset? LastReportGeneratedAt()
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MAX(generated_at) FROM reports");
        return v is null ? null : FromTs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    /// <summary>Incidents opened or resolved in the period (without those closed by suppression).</summary>
    public IReadOnlyList<IncidentRecord> GetIncidentsInPeriod(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,check_key,check_id,severity,state,opened_at,resolved_at,affected,message FROM incidents
            WHERE state<>'SUPPRESSED' AND ((opened_at>=$f AND opened_at<$t) OR (resolved_at>=$f AND resolved_at<$t)) ORDER BY opened_at
            """;
        cmd.Parameters.AddWithValue("$f", Ts(from));
        cmd.Parameters.AddWithValue("$t", Ts(to));
        return ReadIncidents(cmd);
    }

    public IReadOnlyList<string> GetFlappingChecks(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT check_key FROM incident_events WHERE to_state='FLAPPING' AND ts>=$f AND ts<$t ORDER BY check_key";
        cmd.Parameters.AddWithValue("$f", Ts(from));
        cmd.Parameters.AddWithValue("$t", Ts(to));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public (DateTimeOffset At, CheckStatus Status)? GetLastSample(string checkKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,status FROM samples WHERE check_key=$k ORDER BY ts DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", checkKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (FromTs(r.GetInt64(0)), CheckStatusParser.Parse(r.GetString(1))) : null;
    }
}
