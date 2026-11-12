using System.Globalization;
using System.IO;
using DesktopTimeTracker.Models;
using DesktopTimeTracker.Services;
using DesktopTimeTracker.Tracking;
using Microsoft.Data.Sqlite;

namespace DesktopTimeTracker.Data;

public sealed record DayTotals(double ActiveSeconds, double AwaySeconds, DateTime? FirstStart, DateTime? LastEnd, int AppSwitches);

public sealed record SessionFlush(long Id, bool IsWeb, DateTime End, double Seconds, string Title);

/// <summary>
/// SQLite store at a user chosen path (default D:\data.db). One long-lived write connection is
/// owned by the tracking loop; readers open short-lived pooled connections so the UI never blocks it.
/// </summary>
public sealed class TrackerDatabase : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SqliteConnection _write;
    private readonly string _readConnectionString;
    private readonly object _writeGate = new();

    public string DatabasePath { get; }

    /// <summary>Set when the previous run ended abruptly (power loss / force kill); null otherwise.</summary>
    public string? RecoveryNotice { get; private set; }

    public TrackerDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        _write = new SqliteConnection(builder.ConnectionString);
        _write.Open();

        builder.Pooling = true;
        builder.Mode = SqliteOpenMode.ReadWrite;
        _readConnectionString = builder.ConnectionString;

        using var pragma = _write.CreateCommand();
        // synchronous=FULL fsyncs the WAL on every commit so a sudden power loss cannot
        // drop already-acknowledged records (WAL+NORMAL can lose the last un-checkpointed frames).
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        Migrate();
        RecoveryNotice = BeginRuntime();
    }

    private void Migrate()
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_sessions (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                day              TEXT    NOT NULL,
                process_name     TEXT    NOT NULL,
                exe_path         TEXT    NOT NULL DEFAULT '',
                window_title     TEXT    NOT NULL DEFAULT '',
                start_time       TEXT    NOT NULL,
                end_time         TEXT    NOT NULL,
                duration_seconds REAL    NOT NULL DEFAULT 0,
                is_browser       INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_app_day      ON app_sessions(day);
            CREATE INDEX IF NOT EXISTS ix_app_day_proc ON app_sessions(day, process_name);
            CREATE INDEX IF NOT EXISTS ix_app_start    ON app_sessions(start_time);

            CREATE TABLE IF NOT EXISTS web_sessions (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                day              TEXT    NOT NULL,
                browser          TEXT    NOT NULL DEFAULT '',
                url              TEXT    NOT NULL DEFAULT '',
                domain           TEXT    NOT NULL DEFAULT '',
                page_title       TEXT    NOT NULL DEFAULT '',
                start_time       TEXT    NOT NULL,
                end_time         TEXT    NOT NULL,
                duration_seconds REAL    NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_web_day        ON web_sessions(day);
            CREATE INDEX IF NOT EXISTS ix_web_day_domain ON web_sessions(day, domain);
            CREATE INDEX IF NOT EXISTS ix_web_start      ON web_sessions(start_time);

            CREATE TABLE IF NOT EXISTS daily_summary (
                day            TEXT PRIMARY KEY,
                active_seconds REAL NOT NULL DEFAULT 0,
                away_seconds   REAL NOT NULL DEFAULT 0,
                first_start    TEXT,
                last_end       TEXT,
                app_switches   INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS tracker_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- runtime meta (crash / power-loss detection) ----------

    private string? BeginRuntime()
    {
        lock (_writeGate)
        {
            string? prevClean = GetMeta("clean_shutdown");
            string? prevHeartbeat = GetMeta("heartbeat");
            string? notice = null;
            if (prevClean == "0" && ParseTs(prevHeartbeat) is DateTime hb)
                notice = $"检测到上次未正常退出（断电或强制结束）。记录已保存至 {hb:yyyy-MM-dd HH:mm:ss}，未丢失已落库数据。";

            using var tx = _write.BeginTransaction();
            SetMeta(tx, "clean_shutdown", "0");
            SetMeta(tx, "heartbeat", Ts(DateTime.Now));
            tx.Commit();
            return notice;
        }
    }

    private string? GetMeta(string key)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT value FROM tracker_meta WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(SqliteTransaction tx, string key, string value)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tracker_meta(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Atomically persists the day totals, the in-progress session updates and a heartbeat in
    /// one transaction (one WAL fsync under synchronous=FULL). A power loss between flushes
    /// therefore loses at most one flush interval of an open session, never its identity.
    /// </summary>
    public void FlushBatch(string day, DayTotals totals, SessionFlush? app, SessionFlush? web, DateTime heartbeat)
    {
        lock (_writeGate)
        {
            using var tx = _write.BeginTransaction();
            using (var cmd = _write.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO daily_summary(day, active_seconds, away_seconds, first_start, last_end, app_switches)
                    VALUES ($day, $active, $away, $first, $last, $switches)
                    ON CONFLICT(day) DO UPDATE SET
                        active_seconds = excluded.active_seconds,
                        away_seconds   = excluded.away_seconds,
                        first_start    = excluded.first_start,
                        last_end       = excluded.last_end,
                        app_switches   = excluded.app_switches;
                    """;
                cmd.Parameters.AddWithValue("$day", day);
                cmd.Parameters.AddWithValue("$active", Math.Round(totals.ActiveSeconds, 3));
                cmd.Parameters.AddWithValue("$away", Math.Round(totals.AwaySeconds, 3));
                cmd.Parameters.AddWithValue("$first", totals.FirstStart.HasValue ? Ts(totals.FirstStart.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$last", totals.LastEnd.HasValue ? Ts(totals.LastEnd.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$switches", totals.AppSwitches);
                cmd.ExecuteNonQuery();
            }
            if (app is not null) WriteSessionFlush(tx, app, "app_sessions", "window_title");
            if (web is not null) WriteSessionFlush(tx, web, "web_sessions", "page_title");
            SetMeta(tx, "heartbeat", Ts(heartbeat));
            tx.Commit();
        }
    }

    private void WriteSessionFlush(SqliteTransaction tx, SessionFlush s, string table, string titleColumn)
    {
        if (s.Id <= 0) return;
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {table} SET end_time = $end, duration_seconds = $seconds, {titleColumn} = $title WHERE id = $id;";
        cmd.Parameters.AddWithValue("$end", Ts(s.End));
        cmd.Parameters.AddWithValue("$seconds", Math.Round(s.Seconds, 3));
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    internal static string Ts(DateTime value) => value.ToString(TimestampFormat, Inv);
    internal static string DayKey(DateTime value) => value.ToString("yyyy-MM-dd", Inv);

    internal static DateTime? ParseTs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, Inv, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    }

    // ---------- writes (tracking thread only) ----------

    public long BeginAppSession(string day, string processName, string exePath, string title, DateTime start, bool isBrowser)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_sessions(day, process_name, exe_path, window_title, start_time, end_time, duration_seconds, is_browser)
                VALUES ($day, $name, $path, $title, $start, $start, 0, $browser);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$name", processName);
            cmd.Parameters.AddWithValue("$path", exePath);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$start", Ts(start));
            cmd.Parameters.AddWithValue("$browser", isBrowser ? 1 : 0);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateAppSession(long id, DateTime end, double seconds, string title)
    {
        if (id <= 0) return;
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                UPDATE app_sessions
                SET end_time = $end, duration_seconds = $seconds, window_title = $title
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$end", Ts(end));
            cmd.Parameters.AddWithValue("$seconds", Math.Round(seconds, 3));
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public long BeginWebSession(string day, string browser, string url, string domain, string title, DateTime start)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                INSERT INTO web_sessions(day, browser, url, domain, page_title, start_time, end_time, duration_seconds)
                VALUES ($day, $browser, $url, $domain, $title, $start, $start, 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$browser", browser);
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$domain", domain);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$start", Ts(start));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateWebSession(long id, DateTime end, double seconds, string title)
    {
        if (id <= 0) return;
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                UPDATE web_sessions
                SET end_time = $end, duration_seconds = $seconds, page_title = $title
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$end", Ts(end));
            cmd.Parameters.AddWithValue("$seconds", Math.Round(seconds, 3));
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveDay(string day, DayTotals totals)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                INSERT INTO daily_summary(day, active_seconds, away_seconds, first_start, last_end, app_switches)
                VALUES ($day, $active, $away, $first, $last, $switches)
                ON CONFLICT(day) DO UPDATE SET
                    active_seconds = excluded.active_seconds,
                    away_seconds   = excluded.away_seconds,
                    first_start    = excluded.first_start,
                    last_end       = excluded.last_end,
                    app_switches   = excluded.app_switches;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$active", Math.Round(totals.ActiveSeconds, 3));
            cmd.Parameters.AddWithValue("$away", Math.Round(totals.AwaySeconds, 3));
            cmd.Parameters.AddWithValue("$first", totals.FirstStart.HasValue ? Ts(totals.FirstStart.Value) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$last", totals.LastEnd.HasValue ? Ts(totals.LastEnd.Value) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$switches", totals.AppSwitches);
            cmd.ExecuteNonQuery();
        }
    }

    public DayTotals LoadDay(string day)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "SELECT active_seconds, away_seconds, first_start, last_end, app_switches FROM daily_summary WHERE day = $day;";
            cmd.Parameters.AddWithValue("$day", day);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new DayTotals(0, 0, null, null, 0);
            return new DayTotals(
                r.GetDouble(0), r.GetDouble(1),
                ParseTs(r.IsDBNull(2) ? null : r.GetString(2)),
                ParseTs(r.IsDBNull(3) ? null : r.GetString(3)),
                r.GetInt32(4));
        }
    }

    /// <summary>Drops zero-length placeholder rows left behind by a crash mid-session.</summary>
    public int PruneEmptySessions()
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                DELETE FROM app_sessions WHERE duration_seconds <= 0.4;
                DELETE FROM web_sessions WHERE duration_seconds <= 0.4;
                """;
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------- reads (any thread) ----------

    // ---------- aggregation helpers ----------

    private sealed record RawSession(string Process, string ExePath, bool IsBrowser, DateTime Start, DateTime End);

    private List<RawSession> LoadRawAppSessions(SqliteConnection conn, string day, string? search = null)
    {
        var list = new List<RawSession>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, exe_path, is_browser, start_time, end_time
            FROM app_sessions
            WHERE day = $day AND duration_seconds > 0
              AND ($search = '' OR process_name LIKE '%' || $search || '%' OR exe_path LIKE '%' || $search || '%')
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$search", search ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (ParseTs(r.GetString(3)) is { } s && ParseTs(r.GetString(4)) is { } e && e > s)
                list.Add(new RawSession(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, s, e));
        }
        return list;
    }

    /// <summary>合并时间区间：重叠或间隙不超过 gapSeconds 的相邻区间合并为一段。</summary>
    internal static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals, double gapSeconds)
    {
        if (intervals.Count == 0) return [];
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(DateTime, DateTime)> { intervals[0] };
        foreach (var iv in intervals.Skip(1))
        {
            var (ls, le) = merged[^1];
            if ((iv.Start - le).TotalSeconds <= gapSeconds)
            {
                if (iv.End > le) merged[^1] = (ls, iv.End);
            }
            else merged.Add(iv);
        }
        return merged;
    }

    /// <summary>从一组区间中挖掉 union 覆盖的部分。</summary>
    private static List<(DateTime Start, DateTime End)> SubtractUnion(List<(DateTime, DateTime)> interval, List<(DateTime, DateTime)> union)
    {
        var result = new List<(DateTime, DateTime)>();
        foreach (var (s, e) in interval)
        {
            DateTime cur = s;
            foreach (var (ws, we) in union)
            {
                if (we <= cur) continue;
                if (ws >= e) break;
                if (ws > cur) result.Add((cur, ws < e ? ws : e));
                cur = we > cur ? (we < e ? we : e) : cur;
                if (cur >= e) break;
            }
            if (cur < e) result.Add((cur, e));
        }
        return result;
    }

    public TodayOverview GetOverview(string day, TrackerSettings settings)
    {
        using var conn = OpenRead();

        double active = 0, away = 0; int switches = 0;
        DateTime? first = null, last = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT active_seconds, away_seconds, first_start, last_end, app_switches FROM daily_summary WHERE day = $day;";
            cmd.Parameters.AddWithValue("$day", day);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                active = r.GetDouble(0); away = r.GetDouble(1);
                first = ParseTs(r.IsDBNull(2) ? null : r.GetString(2));
                last = ParseTs(r.IsDBNull(3) ? null : r.GetString(3));
                switches = r.GetInt32(4);
            }
        }

        var raw = LoadRawAppSessions(conn, day);

        // 应用排行：按进程合并相邻/重叠会话并丢弃 <2 秒的碎片，消除焦点闪烁噪音
        var apps = new List<AppUsage>();
        foreach (var group in raw.GroupBy(s => s.Process))
        {
            var merged = MergeIntervals(group.Select(s => (s.Start, s.End)).ToList(), 1.0)
                .Where(m => (m.End - m.Start).TotalSeconds >= 2)
                .ToList();
            double seconds = merged.Sum(m => (m.End - m.Start).TotalSeconds);
            string exe = group.Select(s => s.ExePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
            if (seconds > 0) apps.Add(new AppUsage(group.Key, exe, seconds, merged.Count, 0));
        }
        apps = apps.OrderByDescending(a => a.Seconds).ToList();
        double appTotal = apps.Sum(a => a.Seconds);
        apps = apps.Select(a => a with { Share = appTotal > 0 ? a.Seconds / appTotal : 0 }).ToList();

        var sites = new List<WebUsage>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(NULLIF(domain, ''), '(未识别)'), SUM(duration_seconds), COUNT(DISTINCT NULLIF(url, ''))
                FROM web_sessions WHERE day = $day AND duration_seconds > 0
                GROUP BY 1 ORDER BY 2 DESC;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            using var r = cmd.ExecuteReader();
            while (r.Read()) sites.Add(new WebUsage(r.GetString(0), r.GetDouble(1), r.GetInt32(2), 0));
        }
        double webTotal = sites.Sum(s => s.Seconds);
        sites = sites.Select(s => s with { Share = webTotal > 0 ? s.Seconds / webTotal : 0 }).ToList();

        // 24 小时分布：跨小时的会话按秒拆分归属到各小时
        var hours = new double[24];
        foreach (var s in raw)
        {
            DateTime cur = s.Start;
            while (cur < s.End)
            {
                DateTime boundary = cur.Date.AddHours(cur.Hour + 1);
                DateTime segEnd = boundary < s.End ? boundary : s.End;
                int h = cur.Hour;
                if (h is >= 0 and < 24) hours[h] += (segEnd - cur).TotalSeconds;
                cur = segEnd;
            }
        }

        // 分类占比
        var categories = ComputeCategories(conn, day, settings);

        var timeline = new List<TimelineEntry>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT start_time, end_time, duration_seconds, 0, process_name, window_title
                FROM app_sessions WHERE day = $day AND duration_seconds > 0
                UNION ALL
                SELECT start_time, end_time, duration_seconds, 1,
                       COALESCE(NULLIF(domain, ''), '(未识别)'),
                       COALESCE(NULLIF(page_title, ''), NULLIF(url, ''), '')
                FROM web_sessions WHERE day = $day AND duration_seconds > 0
                ORDER BY 1 DESC LIMIT 100;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                timeline.Add(new TimelineEntry(
                    ParseTs(r.GetString(0)) ?? default,
                    ParseTs(r.GetString(1)) ?? default,
                    r.GetDouble(2), r.GetInt32(3) == 1, r.GetString(4), r.GetString(5)));
            }
        }

        return new TodayOverview(
            active, away, apps.Count, sites.Count(s => s.Domain != "(未识别)"), switches,
            first, last,
            apps.Take(10).ToList(), sites.Take(10).ToList(),
            Enumerable.Range(0, 24).Select(h => new HourBucket(h, hours[h])).ToList(),
            categories,
            timeline);
    }

    public IReadOnlyList<AppSession> GetAppSessions(string day, string? search)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT start_time, end_time, duration_seconds, process_name, window_title, exe_path
            FROM app_sessions
            WHERE day = $day AND duration_seconds > 0
              AND ($search = '' OR process_name LIKE '%' || $search || '%'
                   OR window_title LIKE '%' || $search || '%' OR exe_path LIKE '%' || $search || '%')
            ORDER BY start_time DESC LIMIT 2000;
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$search", search ?? "");
        using var r = cmd.ExecuteReader();
        var list = new List<AppSession>();
        while (r.Read())
        {
            list.Add(new AppSession(
                ParseTs(r.GetString(0)) ?? default, ParseTs(r.GetString(1)) ?? default,
                r.GetDouble(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        return list;
    }

    public IReadOnlyList<AppUsage> GetAppUsage(string day, string? search)
    {
        using var conn = OpenRead();
        var raw = LoadRawAppSessions(conn, day, search);
        var list = new List<AppUsage>();
        foreach (var group in raw.GroupBy(s => s.Process))
        {
            var merged = MergeIntervals(group.Select(s => (s.Start, s.End)).ToList(), 1.0)
                .Where(m => (m.End - m.Start).TotalSeconds >= 2)
                .ToList();
            double seconds = merged.Sum(m => (m.End - m.Start).TotalSeconds);
            string exe = group.Select(s => s.ExePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
            if (seconds > 0) list.Add(new AppUsage(group.Key, exe, seconds, merged.Count, 0));
        }
        list = list.OrderByDescending(a => a.Seconds).ToList();
        double total = list.Sum(a => a.Seconds);
        return list.Select(a => a with { Share = total > 0 ? a.Seconds / total : 0 }).ToList();
    }

    /// <summary>把当天会话按分类聚合。浏览器时间先按域名分类，剩余时间归入浏览器的进程分类。</summary>
    private List<CategoryUsage> ComputeCategories(SqliteConnection conn, string day, TrackerSettings settings)
    {
        var catIntervals = new Dictionary<string, List<(DateTime, DateTime)>>(StringComparer.OrdinalIgnoreCase);
        var webUnion = new List<(DateTime, DateTime)>();

        void Add(string cat, (DateTime, DateTime) iv)
        {
            if (!catIntervals.TryGetValue(cat, out var list))
                catIntervals[cat] = list = [];
            list.Add(iv);
        }

        // 网页会话直接按域名分类
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT domain, start_time, end_time FROM web_sessions
                WHERE day = $day AND duration_seconds > 0
                """;
            cmd.Parameters.AddWithValue("$day", day);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (ParseTs(r.GetString(1)) is not { } s || ParseTs(r.GetString(2)) is not { } e || e <= s) continue;
                string cat = AppCategorizer.ForDomain(r.GetString(0), settings);
                Add(cat, (s, e));
                webUnion.Add((s, e));
            }
        }
        webUnion = MergeIntervals(webUnion, 0);

        // 应用会话：非浏览器整段归入进程分类；浏览器取"未被网页覆盖"的剩余时间
        foreach (var s in LoadRawAppSessions(conn, day))
        {
            string cat = AppCategorizer.ForProcess(s.Process, settings);
            if (!s.IsBrowser)
            {
                Add(cat, (s.Start, s.End));
            }
            else
            {
                foreach (var seg in SubtractUnion([(s.Start, s.End)], webUnion)) Add(cat, seg);
            }
        }

        var result = new List<CategoryUsage>();
        foreach (var kv in catIntervals)
        {
            double seconds = MergeIntervals(kv.Value, 0).Sum(m => (m.End - m.Start).TotalSeconds);
            if (seconds > 0) result.Add(new CategoryUsage(kv.Key, seconds, 0));
        }
        result = result.OrderByDescending(c => c.Seconds).ToList();
        double total = result.Sum(c => c.Seconds);
        return total > 0 ? result.Select(c => c with { Share = c.Seconds / total }).ToList() : result;
    }

    /// <summary>最近 N 天的每日活跃趋势。</summary>
    public IReadOnlyList<TrendDay> GetTrend(int daysBack)
    {
        string fromDay = DayKey(DateTime.Now.AddDays(-(daysBack - 1)));
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT day, active_seconds, away_seconds, app_switches FROM daily_summary
            WHERE day >= $from ORDER BY day;
            """;
        cmd.Parameters.AddWithValue("$from", fromDay);
        var list = new List<TrendDay>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TrendDay(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetInt32(3)));
        return list;
    }

    /// <summary>跨日期范围的应用排行（按天合并碎片，直接 SQL 聚合）。</summary>
    public IReadOnlyList<AppUsage> GetTopAppsRange(string fromDay, string toDay, int limit)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, MAX(exe_path), SUM(duration_seconds), COUNT(*)
            FROM app_sessions
            WHERE day >= $from AND day <= $to AND duration_seconds >= 2
            GROUP BY process_name ORDER BY 3 DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", fromDay);
        cmd.Parameters.AddWithValue("$to", toDay);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<AppUsage>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new AppUsage(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetInt32(3), 0));
        double total = list.Sum(a => a.Seconds);
        return list.Select(a => a with { Share = total > 0 ? a.Seconds / total : 0 }).ToList();
    }

    public IReadOnlyList<WebSession> GetWebSessions(string day, string? search)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT start_time, end_time, duration_seconds, browser, domain, page_title, url
            FROM web_sessions
            WHERE day = $day AND duration_seconds > 0
              AND ($search = '' OR url LIKE '%' || $search || '%'
                   OR domain LIKE '%' || $search || '%' OR page_title LIKE '%' || $search || '%')
            ORDER BY start_time DESC LIMIT 2000;
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$search", search ?? "");
        using var r = cmd.ExecuteReader();
        var list = new List<WebSession>();
        while (r.Read())
        {
            list.Add(new WebSession(
                ParseTs(r.GetString(0)) ?? default, ParseTs(r.GetString(1)) ?? default,
                r.GetDouble(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        }
        return list;
    }

    public IReadOnlyList<WebUsage> GetWebUsage(string day, string? search)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(NULLIF(domain, ''), '(未识别)'), SUM(duration_seconds), COUNT(DISTINCT NULLIF(url, '')), COUNT(*)
            FROM web_sessions
            WHERE day = $day AND duration_seconds > 0
              AND ($search = '' OR url LIKE '%' || $search || '%'
                   OR domain LIKE '%' || $search || '%' OR page_title LIKE '%' || $search || '%')
            GROUP BY 1 ORDER BY 2 DESC;
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$search", search ?? "");
        using var r = cmd.ExecuteReader();
        var list = new List<(string Domain, double Seconds, int Pages)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetDouble(1), r.GetInt32(2)));
        double total = list.Sum(a => a.Seconds);
        return list.Select(a => new WebUsage(a.Domain, a.Seconds, a.Pages, total > 0 ? a.Seconds / total : 0)).ToList();
    }

    public IReadOnlyList<DaySummary> GetDays(int limit)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH days AS (
                SELECT day FROM daily_summary
                UNION SELECT day FROM app_sessions
                UNION SELECT day FROM web_sessions
            )
            SELECT d.day,
                   COALESCE(s.active_seconds, 0), COALESCE(s.away_seconds, 0),
                   s.first_start, s.last_end, COALESCE(s.app_switches, 0),
                   (SELECT COUNT(DISTINCT process_name) FROM app_sessions a WHERE a.day = d.day AND a.duration_seconds > 0),
                   (SELECT COUNT(DISTINCT NULLIF(domain, '')) FROM web_sessions w WHERE w.day = d.day AND w.duration_seconds > 0)
            FROM days d LEFT JOIN daily_summary s ON s.day = d.day
            ORDER BY d.day DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<DaySummary>();
        while (r.Read())
        {
            list.Add(new DaySummary(
                r.GetString(0), r.GetDouble(1), r.GetDouble(2),
                ParseTs(r.IsDBNull(3) ? null : r.GetString(3)),
                ParseTs(r.IsDBNull(4) ? null : r.GetString(4)),
                r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)));
        }
        return list;
    }

    public int DeleteBefore(string day)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                DELETE FROM app_sessions WHERE day < $day;
                DELETE FROM web_sessions WHERE day < $day;
                DELETE FROM daily_summary WHERE day < $day;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Vacuum()
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            cmd.ExecuteNonQuery();
        }
    }

    public long GetSizeBytes()
    {
        long total = 0;
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string p = DatabasePath + suffix;
            if (File.Exists(p)) total += new FileInfo(p).Length;
        }
        return total;
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            try
            {
                using (var tx = _write.BeginTransaction())
                {
                    SetMeta(tx, "clean_shutdown", "1");
                    SetMeta(tx, "heartbeat", Ts(DateTime.Now));
                    tx.Commit();
                }
                using var cmd = _write.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { }
            _write.Dispose();
        }
        SqliteConnection.ClearPool(new SqliteConnection(_readConnectionString));
    }
}