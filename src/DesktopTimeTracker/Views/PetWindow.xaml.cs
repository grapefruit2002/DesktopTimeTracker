using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopTimeTracker.Services;
using DesktopTimeTracker.Models;
using DesktopTimeTracker.Tracking;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopTimeTracker.Views;

/// <summary>
/// 立体桌面小宠物：WebView2 + three.js + three-vrm 渲染真实 VRM 模型。
/// 支持轻柔待机、眨眼、头发物理、点击对话与拖动移动。
/// </summary>
public partial class PetWindow : Window
{
    private readonly Func<string?> _statsTextProvider;
    private readonly Random _random = new();
    private WebView2CompositionControl? _web;
    private bool _ready;

    public event Action? OpenMainRequested;
    public event Action? HideRequested;

    private IReadOnlyList<string> _clickLines;
    private IReadOnlyList<string> _menuLines;
    private string _modelId;

    public PetWindow(Func<string?> statsTextProvider, TrackerSettings settings)
    {
        InitializeComponent();
        _statsTextProvider = statsTextProvider;
        _clickLines = settings.PetClickLines;
        _menuLines = settings.PetMenuLines;
        _modelId = settings.SelectedPetModelId;

        Loaded += OnLoaded;
    }

    public void ApplySettings(TrackerSettings settings)
    {
        _clickLines = settings.PetClickLines;
        _menuLines = settings.PetMenuLines;
        if (_modelId != settings.SelectedPetModelId)
        {
            _modelId = settings.SelectedPetModelId;
            NavigateModel();
        }
    }

    public event Action<string>? ModelStatusChanged;

    private void NavigateModel()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        _ready = false;
        string id = PetModel.IsImportedId(_modelId) ? _modelId : PetModel.DefaultId;
        core.Navigate("https://pet.local/index.html?model=" + id);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_web is not null) return;
        try
        {
            _web = new WebView2CompositionControl
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent
            };
            WebHost.Content = _web;

            await _web.EnsureCoreWebView2Async();
            CoreWebView2 core = _web.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            string webDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
            string avatarDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTimeTracker");

            core.SetVirtualHostNameToFolderMapping("pet.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("avatar.local", avatarDir, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, args) =>
                PetLog.Write($"桌宠 WebView2 进程失败: {args.ProcessFailedKind}");
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                    PetLog.Write($"桌宠页面加载失败: {args.WebErrorStatus}, {args.HttpStatusCode}");
            };
            NavigateModel();
        }
        catch (Exception ex)
        {
            PetLog.Write("WebView2 初始化失败: " + ex);
            MessageBox.Show(
                "桌宠需要 Microsoft Edge WebView2 运行时。\n请安装后重试（Windows 11 通常已自带）。\n\n" + ex.Message,
                "桌宠启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>由主程序调用：喝水/久坐等提醒时让宠物说出来。</summary>
    public void Speak(string text) => Say(text);

    private void Say(string text)
    {
        if (!_ready || _web?.CoreWebView2 is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            string json = JsonSerializer.Serialize(text);
            _ = _web.CoreWebView2.ExecuteScriptAsync($"window.petSay({json});");
        }
        catch (Exception ex)
        {
            PetLog.Write("桌宠说话失败: " + ex.Message);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Source != _web?.CoreWebView2?.Source) return;
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = doc.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";

            switch (type)
            {
                case "ready":
                    _ready = true;
                    break;
                case "modelLoaded":
                    string loadedId = root.GetProperty("id").GetString() ?? PetModel.DefaultId;
                    _modelId = PetModel.IsImportedId(loadedId) ? loadedId : PetModel.DefaultId;
                    ModelStatusChanged?.Invoke(root.GetProperty("message").GetString() ?? "人物模型已加载。");
                    break;
                case "modelError":
                    _modelId = "";
                    string message = root.GetProperty("message").GetString() ?? "模型加载失败。";
                    PetLog.Write(message);
                    ModelStatusChanged?.Invoke(message);
                    break;
                case "move":
                    if (root.TryGetProperty("dx", out JsonElement dx) && root.TryGetProperty("dy", out JsonElement dy))
                    {
                        double nextLeft = Left + dx.GetDouble();
                        double nextTop = Top + dy.GetDouble();
                        if (double.IsFinite(nextLeft) && double.IsFinite(nextTop))
                        {
                            Left = nextLeft;
                            Top = nextTop;
                        }
                    }
                    break;
                case "drag":
                    try { DragMove(); }
                    catch (InvalidOperationException) { }
                    break;
                case "open":
                    OpenMainRequested?.Invoke();
                    break;
                case "hide":
                    HideRequested?.Invoke();
                    break;
                case "menu":
                    ShowContextMenu();
                    break;
                case "tap":
                    Say(_clickLines[_random.Next(_clickLines.Count)]);
                    break;
            }
        }
        catch (Exception ex)
        {
            PetLog.Write("桌宠消息处理失败: " + ex.Message);
        }
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Mouse };

        var sayItem = new MenuItem { Header = "说句话" };
        sayItem.Click += (_, _) => Say(RandomLine());

        var openItem = new MenuItem { Header = "打开统计面板" };
        openItem.Click += (_, _) => OpenMainRequested?.Invoke();

        var hideItem = new MenuItem { Header = "隐藏桌宠" };
        hideItem.Click += (_, _) => HideRequested?.Invoke();

        menu.Items.Add(sayItem);
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(hideItem);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private string RandomLine()
    {
        if (_random.NextDouble() < 0.45 && _statsTextProvider() is string stats && stats.Length > 0)
            return stats;
        return _menuLines[_random.Next(_menuLines.Count)];
    }

    /// <summary>确保窗口位置在可见屏幕内。</summary>
    public void EnsureOnScreen()
    {
        double left = Left, top = Top;
        if (double.IsNaN(left) || left < -2000) left = SystemParameters.WorkArea.Right - Width - 40;
        if (double.IsNaN(top) || top < -2000) top = SystemParameters.WorkArea.Bottom - Height - 20;
        left = Math.Clamp(left, SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width);
        top = Math.Clamp(top, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height);
        Left = left;
        Top = top;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _web?.Dispose(); } catch (Exception) { }
        base.OnClosed(e);
    }
}
