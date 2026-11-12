using System.Diagnostics;
using System.Windows.Automation;

namespace DesktopTimeTracker.Tracking;

public readonly record struct BrowserReading(string Url, string Domain, string PageTitle, bool Incognito);

/// <summary>
/// Reads the omnibox of the foreground browser window through UI Automation.
/// Must be called from a background (MTA) thread: cross-process UIA calls can block
/// for hundreds of milliseconds and would stall the WPF dispatcher.
/// </summary>
public sealed class BrowserUrlReader
{
    private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "iexplore",
        "waterfox", "librewolf", "palemoon", "seamonkey", "tor", "whale", "yandex",
        "360se", "360chrome", "360se6", "qqbrowser", "sogouexplorer", "maxthon", "theworld",
        "liebao", "2345explorer", "centbrowser", "ucbrowser", "quark", "chrome.beta", "msedge.beta"
    };

    // Exact localized names of the address bar; UIA PropertyCondition cannot do substring matching.
    private static readonly string[] AddressBarNames =
    [
        "Address and search bar", "地址和搜索栏", "地址栏", "網址和搜尋列",
        "Search or enter address", "搜索或输入网址", "搜尋或輸入網址", "搜索或输入地址",
        "Address field", "Enter a search or address", "Search the web", "URL",
        "Adress- und Suchleiste", "Barre d'adresse et de recherche", "アドレスと検索バー", "주소 및 검색창"
    ];

    private static readonly (string Suffix, string Browser)[] TitleSuffixes =
    [
        (" - Google Chrome", "Chrome"), (" - Microsoft Edge", "Edge"), (" - Mozilla Firefox", "Firefox"),
        (" — Mozilla Firefox", "Firefox"), (" - Brave", "Brave"), (" - Opera", "Opera"),
        (" - Vivaldi", "Vivaldi"), (" - Chromium", "Chromium"), (" - 360安全浏览器", "360"),
        (" - 360极速浏览器", "360"), (" - QQ浏览器", "QQ浏览器"), (" - 搜狗浏览器", "搜狗浏览器")
    ];

    private static readonly string[] IncognitoMarkers =
        ["Incognito", "InPrivate", "Private Browsing", "无痕", "無痕", "隐私", "隱私", "隐身", "隱身"];

    private readonly Condition _addressBarCondition;
    private readonly Condition _anyEditCondition;
    private readonly Condition _selectedTabCondition;

    private IntPtr _cachedWindow;
    private string _cachedTitle = "";
    private BrowserReading _cachedReading;
    private long _lastReadAt;
    private int _readIntervalMs;
    private long _backoffUntil;

    public BrowserUrlReader(int readIntervalMs)
    {
        _readIntervalMs = readIntervalMs;
        var nameConditions = AddressBarNames
            .Select(n => new PropertyCondition(AutomationElement.NameProperty, n))
            .ToArray<Condition>();
        _addressBarCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new OrCondition(nameConditions));
        _anyEditCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        _selectedTabCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true));
    }

    public void UpdateInterval(int ms) => _readIntervalMs = Math.Clamp(ms, 500, 30000);

    public static bool IsBrowser(string processName) => KnownBrowsers.Contains(processName);

    public static string BrowserLabel(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" or "chromium" or "chrome.beta" => "Chrome",
        "msedge" or "msedge.beta" => "Edge",
        "firefox" or "waterfox" or "librewolf" or "palemoon" => "Firefox",
        "brave" => "Brave",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "iexplore" => "IE",
        "360se" or "360chrome" or "360se6" => "360浏览器",
        "qqbrowser" => "QQ浏览器",
        "sogouexplorer" => "搜狗浏览器",
        "maxthon" => "傲游",
        "liebao" => "猎豹",
        "whale" or "yandex" => processName,
        _ => processName
    };

    public void Invalidate()
    {
        _cachedWindow = IntPtr.Zero;
        _cachedTitle = "";
        _cachedReading = default;
        _lastReadAt = 0;
    }

    public BrowserReading Read(IntPtr hwnd, string windowTitle, string processName)
    {
        long now = Environment.TickCount64;
        bool sameTarget = hwnd == _cachedWindow && string.Equals(windowTitle, _cachedTitle, StringComparison.Ordinal);

        // A title change means navigation happened, so re-read immediately; otherwise throttle.
        if (sameTarget && now - _lastReadAt < _readIntervalMs) return _cachedReading;
        if (now < _backoffUntil) return _cachedReading;

        _lastReadAt = now;
        _cachedWindow = hwnd;
        _cachedTitle = windowTitle;

        var sw = Stopwatch.StartNew();
        BrowserReading reading;
        try
        {
            reading = ReadCore(hwnd, windowTitle, processName);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                   or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            reading = new BrowserReading("", "", CleanTitle(windowTitle, processName), false);
        }
        sw.Stop();

        // A slow UIA provider (busy renderer, huge a11y tree) must not be hammered every tick.
        if (sw.ElapsedMilliseconds > 1200) _backoffUntil = now + Math.Min(sw.ElapsedMilliseconds * 3, 15000);

        _cachedReading = reading;
        return reading;
    }

    private BrowserReading ReadCore(IntPtr hwnd, string windowTitle, string processName)
    {
        bool incognito = IncognitoMarkers.Any(m => windowTitle.Contains(m, StringComparison.OrdinalIgnoreCase));

        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return new BrowserReading("", "", CleanTitle(windowTitle, processName), incognito);

        // The selected tab's name is the clean page title; the window title carries
        // tab-group / profile / browser suffixes that are noise.
        string title = "";
        try
        {
            var tab = root.FindFirst(TreeScope.Descendants, _selectedTabCondition);
            if (tab is not null) title = CleanTabTitle(SafeName(tab));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }
        if (title.Length == 0) title = CleanTitle(windowTitle, processName);

        var element = root.FindFirst(TreeScope.Descendants, _addressBarCondition)
                      ?? root.FindFirst(TreeScope.Descendants, _anyEditCondition);
        if (element is null) return new BrowserReading("", "", title, incognito);

        string raw = ReadValue(element);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Some providers expose the URL through the element name instead of ValuePattern.
            string name = SafeName(element);
            if (LooksNavigable(name)) raw = name;
        }

        if (incognito) return new BrowserReading("", "(隐私模式)", title, true);

        string url = NormalizeUrl(raw.Trim());
        return new BrowserReading(url, ExtractDomain(url), title, false);
    }

    private static string ReadValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object p) && p is ValuePattern value)
                return value.Current.Value ?? "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object tp) && tp is TextPattern text)
                return text.DocumentRange.GetText(4096) ?? "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }

        return "";
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Current.Name ?? ""; }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static bool LooksNavigable(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && !text.Contains(' ')
        && (text.Contains('.') || text.Contains("://"));

    // Edge's efficiency mode appends " - 内存使用率 - 53.1 MB" to a tab's accessible name.
    private static readonly System.Text.RegularExpressions.Regex TabMemorySuffix = new(
        @"\s*-\s*(内存使用率|内存占用|Memory usage|Memory)\s*-\s*[\d.,]+\s*(MB|GB|KB|B)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string CleanTabTitle(string name)
    {
        string title = name
            .Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace("\uFEFF", "")
            .Trim();
        title = TabMemorySuffix.Replace(title, "").Trim();
        return title;
    }

    internal static string CleanTitle(string windowTitle, string processName)
    {
        // Edge embeds zero-width spaces in "Microsoft Edge", which defeats suffix matching.
        string title = windowTitle
            .Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace("\uFEFF", "")
            .Trim();
        title = System.Text.RegularExpressions.Regex.Replace(
            title, @"\s*(和另外|以及另外|and)\s*\d+\s*(个页面|个其他页面|more pages?|other pages?)", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        foreach (var (suffix, _) in TitleSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return title[..^suffix.Length].Trim();
        }
        // Chromium based browsers all end with " - <Browser Name>"; drop the last segment
        // only when it matches the running process, so document titles with dashes survive.
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string tail = title[(dash + 3)..].Trim();
            if (KnownBrowsers.Contains(tail) || tail.Equals(BrowserLabel(processName), StringComparison.OrdinalIgnoreCase))
                return title[..dash].Trim();
        }
        return title;
    }

    /// <summary>
    /// Chrome/Edge elide the scheme in the omnibox for secure sites, so a bare host is
    /// restored to https:// to keep stored URLs usable.
    /// </summary>
    internal static string NormalizeUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        if (raw.Contains("://", StringComparison.Ordinal)) return raw;
        if (raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return raw;
        if (raw.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase)) return raw;
        if (!LooksNavigable(raw)) return "";
        return "https://" + raw;
    }

    internal static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return "";
    }
}