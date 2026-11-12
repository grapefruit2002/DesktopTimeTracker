namespace DesktopTimeTracker.Models;

public sealed record AppUsage(string Name, string ExePath, double Seconds, int Sessions, double Share);

public sealed record AppSession(
    DateTime Start,
    DateTime End,
    double Seconds,
    string ProcessName,
    string WindowTitle,
    string ExePath);

public sealed record WebUsage(string Domain, double Seconds, int Pages, double Share);

public sealed record WebSession(
    DateTime Start,
    DateTime End,
    double Seconds,
    string Browser,
    string Domain,
    string Title,
    string Url);

public sealed record DaySummary(
    string Day,
    double ActiveSeconds,
    double AwaySeconds,
    DateTime? FirstStart,
    DateTime? LastEnd,
    int AppSwitches,
    int AppCount,
    int WebCount);

public sealed record HourBucket(int Hour, double Seconds);

public sealed record ChartBar(string Label, double Value, double Ratio, string Text, string Day = "");

public sealed record CategoryUsage(string Category, double Seconds, double Share);

public sealed record TrendDay(string Day, double ActiveSeconds, double AwaySeconds, int AppSwitches);

public sealed record TimelineEntry(
    DateTime Start,
    DateTime End,
    double Seconds,
    bool IsWeb,
    string Title,
    string Detail);

public sealed record LiveStatus(
    bool IsRunning,
    bool IsAway,
    string CurrentApp,
    string CurrentTitle,
    string CurrentUrl,
    string CurrentDomain,
    double CurrentAppSeconds,
    double CurrentWebSeconds,
    double TodayActiveSeconds,
    double TodayAwaySeconds,
    double ContinuousActiveSeconds,
    string StateText)
{
    public static LiveStatus Idle { get; } =
        new(false, false, "", "", "", "", 0, 0, 0, 0, 0, "未开始");
}

public sealed record TodayOverview(
    double ActiveSeconds,
    double AwaySeconds,
    int AppCount,
    int WebCount,
    int AppSwitches,
    DateTime? FirstStart,
    DateTime? LastEnd,
    IReadOnlyList<AppUsage> TopApps,
    IReadOnlyList<WebUsage> TopSites,
    IReadOnlyList<HourBucket> Hours,
    IReadOnlyList<CategoryUsage> Categories,
    IReadOnlyList<TimelineEntry> Timeline);