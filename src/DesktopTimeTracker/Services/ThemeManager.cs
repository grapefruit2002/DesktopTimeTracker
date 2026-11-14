using System.Windows;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 主题（Brush 字典）切换器。
/// App.xaml 里 App.Resources.MergedDictionaries 的第二份就是当前主题。
/// 切换时替换那份字典即可——Theme.xaml（Style 骨架）不动。
/// </summary>
public static class ThemeManager
{
    /// <summary>可用的主题：文件名（不含扩展名）→ 显示名。</summary>
    public static IReadOnlyDictionary<string, string> Available { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pink"] = "樱花粉",
            ["LightBlue"] = "冰海蓝",
            ["Mint"] = "薄荷绿",
            ["LightPurple"] = "薰衣草",
        };

    private static readonly string[] Suffixes = ["Pink", "LightBlue", "Mint", "LightPurple"];

    /// <summary>切换到指定主题。找不到就切到 Pink。</summary>
    public static void Apply(string name)
    {
        if (!Available.ContainsKey(name)) name = "Pink";

        try
        {
            var app = (App)Application.Current;
            var merged = app.Resources.MergedDictionaries;

            // 找到 Theme.xaml 旁边的主题字典（Source 以 Pink.xaml / Dark.xaml / Mint.xaml 结尾）
            int themeIndex = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var dict = merged[i];
                string? source = dict.Source?.ToString();
                if (source is not null && Suffixes.Any(s => source.EndsWith($"{s}.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    themeIndex = i;
                    break;
                }
            }

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute)
            };

            if (themeIndex >= 0) merged[themeIndex] = newDict;
            else merged.Add(newDict);
        }
        catch
        {
            // 主题切换失败不影响程序，静默回落到 Pink
        }
    }
}
