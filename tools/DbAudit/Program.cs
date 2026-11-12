using Microsoft.Data.Sqlite;

string dbPath = args.Length > 0 ? args[0] : @"D:\Data.db";
string day = DateTime.Now.ToString("yyyy-MM-dd");

var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
using var conn = new SqliteConnection(cs);
conn.Open();

double Sum(SqliteConnection c, string sql, string day) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$day", day); var r = cmd.ExecuteScalar(); return r is null or DBNull ? 0 : Convert.ToDouble(r); }

Console.WriteLine($"===== 核对报告：{dbPath} · {day} · 生成时间 {DateTime.Now:HH:mm:ss} =====\n");

// 1. daily_summary
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT active_seconds, away_seconds, first_start, last_end, app_switches FROM daily_summary WHERE day=$day";
    cmd.Parameters.AddWithValue("$day", day);
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        Console.WriteLine($"[每日汇总] 活跃 {Fmt(r.GetDouble(0))} | 离开 {Fmt(r.GetDouble(1))} | 首次 {r.GetString(2)} | 最后 {r.GetString(3)} | 切换 {r.GetInt64(4)} 次");
    }
    else Console.WriteLine("[每日汇总] 今日无记录");
}

// 2. app_sessions 总量
double appSum = Sum(conn, "SELECT SUM(duration_seconds) FROM app_sessions WHERE day=$day", day);
long appCnt = Count(conn, "SELECT COUNT(*) FROM app_sessions WHERE day=$day");
double appPos = Sum(conn, "SELECT SUM(duration_seconds) FROM app_sessions WHERE day=$day AND duration_seconds>0", day);
double appNeg = Sum(conn, "SELECT SUM(duration_seconds) FROM app_sessions WHERE day=$day AND duration_seconds<0", day);
Console.WriteLine($"\n[应用会话] 总条数 {appCnt} | 正时长合计 {Fmt(appPos)} | 负时长合计 {Fmt(appNeg)} | 净合计 {Fmt(appSum)}");

// 3. 分进程
Console.WriteLine("\n[分进程 TOP15]");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT process_name, SUM(duration_seconds), COUNT(*) FROM app_sessions WHERE day=$day AND duration_seconds>0 GROUP BY process_name ORDER BY 2 DESC LIMIT 15";
    cmd.Parameters.AddWithValue("$day", day);
    using var r = cmd.ExecuteReader();
    while (r.Read()) Console.WriteLine($"  {r.GetString(0),-20} {Fmt(r.GetDouble(1)),10}  ({r.GetInt64(2)} 段)");
}

// 4. 网站会话
double webSum = Sum(conn, "SELECT SUM(duration_seconds) FROM web_sessions WHERE day=$day", day);
double browserApp = Sum(conn, "SELECT SUM(duration_seconds) FROM app_sessions WHERE day=$day AND is_browser=1 AND duration_seconds>0", day);
Console.WriteLine($"\n[网站会话] 合计 {Fmt(webSum)} | 浏览器应用时长 {Fmt(browserApp)} (差值={Fmt(browserApp - webSum)})");

// 5. 时长字段与 end-start 不一致的会话
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        SELECT COUNT(*), SUM(ABS((julianday(end_time)-julianday(start_time))*86400 - duration_seconds))
        FROM app_sessions WHERE day=$day AND ABS((julianday(end_time)-julianday(start_time))*86400 - duration_seconds) > 5
        """;
    cmd.Parameters.AddWithValue("$day", day);
    using var r = cmd.ExecuteReader();
    if (r.Read()) Console.WriteLine($"\n[一致性] app_sessions 中 |end-start 与 duration_seconds| 差>5秒 的条数: {r.GetInt64(0)}, 总偏差 {Fmt(r.IsDBNull(1)?0:r.GetDouble(1))}");
}

// 6. 重叠检测（app_sessions 自身重叠）
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        SELECT COUNT(*), SUM(MIN(a.end_time,b.end_time)-MAX(a.start_time,b.start_time))
        FROM app_sessions a JOIN app_sessions b
          ON a.day=b.day AND a.id<b.id AND b.start_time < a.end_time AND b.end_time > a.start_time
        WHERE a.day=$day
        """;
    cmd.Parameters.AddWithValue("$day", day);
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        long pairs = r.GetInt64(0);
        double overlapSec = r.IsDBNull(1) ? 0 : (Convert.ToDouble(r.GetValue(1)));
        Console.WriteLine($"[重叠检测] 相互重叠的会话对: {pairs} 对, 重叠总量约 {overlapSec:F1} 秒");
    }
}

// 7. 时间线覆盖：把 app_sessions 区间合并，找空洞
var intervals = new List<(DateTime s, DateTime e)>();
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT start_time, end_time FROM app_sessions WHERE day=$day AND duration_seconds>0";
    cmd.Parameters.AddWithValue("$day", day);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        if (DateTime.TryParse(r.GetString(0), out var s) && DateTime.TryParse(r.GetString(1), out var e) && e > s)
            intervals.Add((s, e));
    }
}
if (intervals.Count > 0)
{
    intervals.Sort((x, y) => x.s.CompareTo(y.s));
    var merged = new List<(DateTime s, DateTime e)> { intervals[0] };
    foreach (var iv in intervals.Skip(1))
    {
        var last = merged[^1];
        if (iv.s <= last.e) merged[^1] = (last.s, last.e > iv.e ? last.e : iv.e);
        else merged.Add(iv);
    }
    DateTime first = merged[0].s, lastEnd = merged[^1].e;
    double span = (lastEnd - first).TotalSeconds;
    double covered = merged.Sum(m => (m.e - m.s).TotalSeconds);
    double gaps = span - covered;
    Console.WriteLine($"\n[时间线覆盖] 记录区间 {first:HH:mm:ss} ~ {lastEnd:HH:mm:ss}，共 {Fmt(span)}");
    Console.WriteLine($"  其中有会话覆盖 {Fmt(covered)}，空洞 {Fmt(gaps)} ({merged.Count - 1} 处)");
    var bigGaps = new List<(DateTime s, DateTime e)>();
    for (int i = 1; i < merged.Count; i++)
        if ((merged[i].s - merged[i - 1].e).TotalSeconds > 60) bigGaps.Add((merged[i - 1].e, merged[i].s));
    Console.WriteLine($"  >1分钟 的空洞 {bigGaps.Count} 处:");
    foreach (var g in bigGaps.Take(12)) Console.WriteLine($"    {g.s:HH:mm:ss} ~ {g.e:HH:mm:ss}  ({(g.e - g.s).TotalMinutes:F0} 分钟)");

    double sinceFirst = (DateTime.Now - first).TotalSeconds;
    Console.WriteLine($"\n[对照] 从今天首次记录到现在 {Fmt(sinceFirst)}，其中会话覆盖 {Fmt(covered)}，未覆盖 {Fmt(sinceFirst - covered)} (程序未运行/离开/锁屏)");
}

static long Count(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$day", DateTime.Now.ToString("yyyy-MM-dd")); return (long)cmd.ExecuteScalar()!; }
static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)) is var t ? $"{(int)t.TotalHours}时{t.Minutes}分{t.Seconds}秒" : "";
