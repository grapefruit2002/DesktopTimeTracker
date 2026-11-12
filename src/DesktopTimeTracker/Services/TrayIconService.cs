using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DesktopTimeTracker.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _petItem;
    private readonly bool _appNotificationReady;

    public event Action? ShowRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    /// <summary>参数为切换后的桌宠可见状态。</summary>
    public event Action<bool>? PetToggleRequested;

    public TrayIconService()
    {
        _appNotificationReady = TryRegisterAppNotifications();

        Icon icon = LoadIcon();

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("打开主窗口(&O)");
        showItem.Click += (_, _) => ShowRequested?.Invoke();
        showItem.Font = new Font(showItem.Font, System.Drawing.FontStyle.Bold);
        _toggleItem = new ToolStripMenuItem("暂停记录(&P)");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();
        var exitItem = new ToolStripMenuItem("退出(&X)");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _petItem = new ToolStripMenuItem("显示桌宠(&D)") { CheckOnClick = true, Checked = true };
        _petItem.Click += (_, _) => PetToggleRequested?.Invoke(_petItem.Checked);

        menu.Items.Add(showItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_petItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "桌面时长记录器",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico"));
        if (resource is not null)
        {
            using var stream = resource.Stream;
            return new Icon(stream, new System.Drawing.Size(16, 16));
        }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void SetPaused(bool paused) =>
        _toggleItem.Text = paused ? "继续记录(&P)" : "暂停记录(&P)";

    public void SetPetVisible(bool visible) => _petItem.Checked = visible;

    public void SetTooltip(string text) =>
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;

    // 用 Windows App SDK 原生 Toast 通知替代旧式 ShowBalloonTip。
    // 旧式气泡在 Win10/11 被系统迁移成 Toast 时会丢失应用图标（显示为橙色占位符），
    // 原生 AppNotification 会正确带上 exe 内嵌图标（粉色动漫）。
    public void Balloon(string title, string text)
    {
        if (_appNotificationReady)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(text)
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
                return;
            }
            catch
            {
                // 原生 Toast 失败时回退到旧式气泡，保证通知不丢失
            }
        }

        _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);
    }

    private static bool TryRegisterAppNotifications()
    {
        try
        {
            // 对非打包 exe，Register() 会自动完成 COM 注册与 AUMID 等效处理，
            // Toast 通知即可正确显示应用图标。
            AppNotificationManager.Default.Register();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_appNotificationReady)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
        }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}