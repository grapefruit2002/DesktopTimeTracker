using System.Text;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
string path = args.Length > 0 ? args[0] : @"D:\data.db";

// --sql "<statement>" runs a single write statement (dev/verification helper).
if (args.Length > 2 && args[1] == "--sql")
{
    using var w = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite
    }.ConnectionString);
    w.Open();
    using var cmd = w.CreateCommand();
    cmd.CommandText = args[2];
    int affected = cmd.ExecuteNonQuery();
    Console.WriteLine($"executed, affected={affected}");
    return 0;
}

if (!File.Exists(path))
{
    Console.WriteLine($"NOT FOUND: {path}");
    return 1;
}

using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = path,
    Mode = SqliteOpenMode.ReadOnly
}.ConnectionString);
conn.Open();

void Query(string title, string sql)
{
    Console.WriteLine($"\n=== {title} ===");
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    int cols = r.FieldCount;
    var headers = Enumerable.Range(0, cols).Select(r.GetName).ToArray();
    Console.WriteLine(string.Join(" | ", headers));
    int rows = 0;
    while (r.Read())
    {
        Console.WriteLine(string.Join(" | ", Enumerable.Range(0, cols).Select(i => r.IsDBNull(i) ? "<null>" : r.GetValue(i)?.ToString())));
        rows++;
    }
    Console.WriteLine($"({rows} rows)");
}

Query("tables", "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
Query("journal mode", "PRAGMA journal_mode");
Query("tracker_meta", "SELECT key, value FROM tracker_meta ORDER BY key");
Query("daily_summary", "SELECT * FROM daily_summary ORDER BY day DESC LIMIT 10");
Query("app_sessions (latest)", "SELECT id, day, process_name, start_time, end_time, ROUND(duration_seconds,1) AS secs, is_browser, substr(window_title,1,50) AS title FROM app_sessions ORDER BY id DESC LIMIT 15");
Query("web_sessions (latest)", "SELECT id, day, browser, domain, start_time, end_time, ROUND(duration_seconds,1) AS secs, substr(page_title,1,40) AS title, substr(url,1,70) AS url FROM web_sessions ORDER BY id DESC LIMIT 15");
Query("app totals", "SELECT process_name, COUNT(*) AS n, ROUND(SUM(duration_seconds),1) AS secs FROM app_sessions GROUP BY process_name ORDER BY secs DESC LIMIT 15");
Query("web totals", "SELECT domain, COUNT(*) AS n, ROUND(SUM(duration_seconds),1) AS secs FROM web_sessions GROUP BY domain ORDER BY secs DESC LIMIT 15");
return 0;