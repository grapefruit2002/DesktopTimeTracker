using DesktopTimeTracker.Tracking;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 把进程名 / 域名归入一个分类（工作、沟通、娱乐…）。
/// 用户在设置里写的规则（settings.json 的 CategoryRules）优先，
/// 内置默认规则兜底，都没有则归入"其他"。
/// </summary>
internal static class AppCategorizer
{
    public const string Other = "其他";
    public const string WebOnly = "上网";

    public static readonly string[] KnownCategories = ["工作", "沟通", "上网", "娱乐", "学习", "其他"];

    /// <summary>内置默认规则，可被用户规则覆盖。</summary>
    private static readonly Dictionary<string, string> DefaultRules = new(StringComparer.OrdinalIgnoreCase)
    {
        // 工作类进程
        ["idea64"] = "工作", ["devenv"] = "工作", ["code"] = "工作", ["rider64"] = "工作",
        ["wps"] = "工作", ["wpp"] = "工作", ["et"] = "工作",
        ["winword"] = "工作", ["excel"] = "工作", ["powerpnt"] = "工作", ["outlook"] = "工作",
        ["notepad"] = "工作", ["notepad++"] = "工作", ["notepad3"] = "工作", ["sublime_text"] = "工作",
        ["ssms"] = "工作", ["postman"] = "工作", ["navicat"] = "工作", ["dbeaver"] = "工作",
        ["cmd"] = "工作", ["powershell"] = "工作", ["windowsterminal"] = "工作",
        // 沟通类进程
        ["qq"] = "沟通", ["weixin"] = "沟通", ["wechat"] = "沟通", ["wechatappex"] = "沟通",
        ["dingtalk"] = "沟通", ["feishu"] = "沟通", ["telegram"] = "沟通", ["slack"] = "沟通",
        ["teams"] = "沟通", ["wxwork"] = "沟通",
        // 浏览器默认
        ["msedge"] = "上网", ["chrome"] = "上网", ["firefox"] = "上网", ["brave"] = "上网",
        ["opera"] = "上网", ["360se"] = "上网", ["qqbrowser"] = "上网",
        // 娱乐类进程
        ["thunder"] = "娱乐", ["cloudmusic"] = "娱乐", ["qqmusic"] = "娱乐",
        ["steam"] = "娱乐", ["bilibili"] = "娱乐", ["douyin"] = "娱乐",
        // 常见域名
        ["github.com"] = "工作", ["stackoverflow.com"] = "工作", ["learn.microsoft.com"] = "工作",
        ["docs.microsoft.com"] = "工作", ["npmjs.com"] = "工作", ["nuget.org"] = "工作",
        ["csdn.net"] = "学习", ["cnblogs.com"] = "学习", ["juejin.cn"] = "学习",
        ["zhihu.com"] = "学习", ["w3school.com.cn"] = "学习", ["runoob.com"] = "学习",
        ["bilibili.com"] = "娱乐", ["youtube.com"] = "娱乐", ["douyin.com"] = "娱乐",
        ["iqiyi.com"] = "娱乐", ["youku.com"] = "娱乐",
        ["taobao.com"] = "娱乐", ["jd.com"] = "娱乐", ["pinduoduo.com"] = "娱乐",
        ["weibo.com"] = "娱乐", ["x.com"] = "娱乐", ["twitter.com"] = "娱乐",
    };

    /// <summary>进程名 → 分类。用户规则优先于默认规则。</summary>
    public static string ForProcess(string processName, TrackerSettings settings)
    {
        string key = (processName ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return Other;
        if (settings.CategoryRules.TryGetValue(key, out string? user)) return user;
        return DefaultRules.TryGetValue(key, out string? def) ? def : Other;
    }

    /// <summary>域名 → 分类。支持按后缀匹配（如 mail.google.com 命中 google.com 规则）。</summary>
    public static string ForDomain(string domain, TrackerSettings settings)
    {
        string key = (domain ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return Other;
        if (settings.CategoryRules.TryGetValue(key, out string? user)) return user;
        if (DefaultRules.TryGetValue(key, out string? def)) return def;
        foreach (var kv in settings.CategoryRules)
            if (key.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        foreach (var kv in DefaultRules)
            if (key.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return WebOnly; // 未识别的网址归入"上网"
    }
}
