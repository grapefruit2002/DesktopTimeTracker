using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopTimeTracker.Models;

namespace DesktopTimeTracker.Tracking;

public sealed class TrackerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string DatabasePath { get; set; } = @"D:\data.db";
    public int PollIntervalMs { get; set; } = 1000;
    public int IdleThresholdSeconds { get; set; } = 180;
    public int UrlReadIntervalMs { get; set; } = 3000;
    public int FlushIntervalSeconds { get; set; } = 5;
    public bool TrackWebPages { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AutoStartWithWindows { get; set; }
    // True once the user (or first-run default) has made an explicit auto-start choice,
    // so the first-run "enable by default" never overrides a later deliberate disable.
    public bool AutoStartConfigured { get; set; }
    public List<string> IgnoredProcesses { get; set; } = [];

    /// <summary>久坐提醒阈值（分钟），0 = 关闭。</summary>
    public int BreakReminderMinutes { get; set; } = 60;

    /// <summary>喝水提醒开关（按本地整点时间触发，与运行时长无关）。</summary>
    public bool WaterReminderEnabled { get; set; } = true;

    /// <summary>喝水提醒的整点小时列表，逗号分隔，例如 "10,11,14,15,16,17"。</summary>
    public string WaterReminderHours { get; set; } = "10,11,14,15,16,17";



    /// <summary>每日活跃目标（分钟），0 = 关闭。</summary>
    public int DailyGoalMinutes { get; set; } = 0;

    /// <summary>分类规则：key = 进程名或域名（小写），value = 分类名（工作/娱乐/学习…）。</summary>
    public Dictionary<string, string> CategoryRules { get; set; } = new();

    private static readonly string[] DefaultPetClickLines =
    [
        "记得多喝水呀～",
        "坐久了要起来活动一下哦。",
        "今天的专注时长又涨了呢！",
        "要不要休息一下眼睛？看看远处吧。",
        "我一直在这里陪着你～",
        "有我在，工作也不会太累啦。",
        "摸摸头，辛苦了！",
        "轻轻点我，我就陪你聊聊天。"
    ];

    private static readonly string[] DefaultPetMenuLines =
    [
        "记得多喝水呀～",
        "坐久了要起来活动一下哦。",
        "要不要休息一下眼睛？看看远处吧。",
        "我一直在这里陪着你～",
        "摸摸头，辛苦了！",
        "别熬夜太久哦，早点休息。"
    ];

    public List<string> PetClickLines { get; set; } = [.. DefaultPetClickLines];
    public List<string> PetMenuLines { get; set; } = [.. DefaultPetMenuLines];
    public List<PetModel> PetModels { get; set; } = [];
    public string SelectedPetModelId { get; set; } = PetModel.DefaultId;

    public List<BackgroundImage> BackgroundImages { get; set; } = [];
    public string SelectedBackgroundId { get; set; } = BackgroundImage.DefaultId;
    public int BackgroundMode { get; set; } = (int)Models.BackgroundMode.FillCover;
    public double BackgroundOpacity { get; set; } = 0.52;
    public string ThemeName { get; set; } = "Pink";

    /// <summary>是否显示桌面小宠物。</summary>
    public bool ShowPet { get; set; } = true;

    /// <summary>桌宠上次位置（-1 表示自动放在右下角）。</summary>
    public double PetLeft { get; set; } = -1;
    public double PetTop { get; set; } = -1;

    [JsonIgnore]
    public string SettingsPath { get; private set; } = "";

    /// <summary>
    /// 所有运行期数据（设置、数据库、桌宠模型、日志）的根目录。
    /// 放在程序集所在目录下，避免写入 C 盘用户目录，随 exe 一同迁移。
    /// </summary>
    public static string DefaultFolder => AppContext.BaseDirectory;

    public TrackerSettings Clamp()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 10000);
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 15, 3600);
        UrlReadIntervalMs = Math.Clamp(UrlReadIntervalMs, 500, 30000);
        FlushIntervalSeconds = Math.Clamp(FlushIntervalSeconds, 2, 300);
        if (string.IsNullOrWhiteSpace(DatabasePath)) DatabasePath = @"D:\data.db";
        BreakReminderMinutes = Math.Clamp(BreakReminderMinutes, 0, 480);
        DailyGoalMinutes = Math.Clamp(DailyGoalMinutes, 0, 960);
        WaterReminderHours = NormalizeHours(WaterReminderHours);
        PetClickLines = NormalizePetLines(PetClickLines, DefaultPetClickLines);
        PetMenuLines = NormalizePetLines(PetMenuLines, DefaultPetMenuLines);
        PetModels = (PetModels ?? [])
            .Where(model => model is not null && PetModel.IsImportedId(model.Id))
            .Select(model => new PetModel(model.Id.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(model.Name) ? "导入的人物" : model.Name.Trim(),
                model.FileName))
            .DistinctBy(model => model.Id)
            .ToList();
        SelectedPetModelId = PetModels.FirstOrDefault(model =>
            string.Equals(model.Id, SelectedPetModelId, StringComparison.OrdinalIgnoreCase))?.Id ?? PetModel.DefaultId;
        BackgroundImages = (BackgroundImages ?? [])
            .Where(bg => bg is not null && BackgroundImage.IsImportedId(bg.Id))
            .Select(bg => new BackgroundImage(bg.Id.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(bg.Name) ? "导入的背景" : bg.Name.Trim(),
                bg.FileName))
            .DistinctBy(bg => bg.Id)
            .ToList();
        BackgroundMode = Math.Clamp(BackgroundMode, (int)Models.BackgroundMode.CornerTopRight, (int)Models.BackgroundMode.FillUniform);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.05, 1.0);
        // SelectedBackgroundId 可能是 DefaultId / NoneId / 某个 ImportedId，这里不清洗，由 SyncFromFolder 后的 ViewModel 层负责校正
        CategoryRules = CategoryRules
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        IgnoredProcesses = IgnoredProcesses
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        return this;
    }

    private static List<string> NormalizePetLines(List<string>? lines, string[] defaults)
    {
        List<string> normalized = (lines ?? [])
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();
        return normalized.Count > 0 ? normalized : [.. defaults];
    }

    /// <summary>把 "10,11,14" 之类的输入解析成规范化的小时列表字符串（0-23，去重升序）。</summary>
    public static string NormalizeHours(string? raw)
    {
        var hours = new SortedSet<int>();
        foreach (string part in (raw ?? "").Split([',', '，', ';', '；', ' ', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int h) && h is >= 0 and < 24) hours.Add(h);
        }
        if (hours.Count == 0) foreach (int h in new[] { 10, 11, 14, 15, 16, 17 }) hours.Add(h);
        return string.Join(",", hours);
    }

    /// <summary>把小时列表字符串解析成整数集合。</summary>
    public static IReadOnlyList<int> ParseHours(string? raw)
    {
        var list = new List<int>();
        foreach (string part in (raw ?? "").Split([',', '，', ';', '；', ' ', '|'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out int h) && h is >= 0 and < 24 && !list.Contains(h)) list.Add(h);
        return list;
    }

    public static TrackerSettings Load()
    {
        string path = Path.Combine(DefaultFolder, "settings.json");
        TrackerSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(path), JsonOptions) ?? new TrackerSettings()
                : new TrackerSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new TrackerSettings();
        }
        settings.SettingsPath = path;
        return settings.Clamp();
    }

    public void Save()
    {
        Clamp();
        Directory.CreateDirectory(DefaultFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}