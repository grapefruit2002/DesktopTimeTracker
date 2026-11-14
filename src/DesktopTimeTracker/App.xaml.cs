using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTimeTracker.Converters;
using DesktopTimeTracker.Data;
using DesktopTimeTracker.Services;
using DesktopTimeTracker.Tracking;
using DesktopTimeTracker.ViewModels;
using Microsoft.Win32;

namespace DesktopTimeTracker;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIconService? _tray;
    private TrackerDatabase? _db;
    private ActivityTracker? _tracker;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private DispatcherTimer? _trayTimer;
    private bool _cleanedUp;
    private bool _hiddenNotified;

    private HwndSource? _hotkeySource;
    private const int HotkeyId = 0xB00B;
    private TrackerSettings? _cachedSettings;
    private int _settingsTick;
    private double _lastBreakNotifiedContinuous;
    private string? _goalNotifiedDay;
    private string? _waterNotifiedKey;
    private Views.PetWindow? _pet;
    private bool _petSyncGuard;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppIdentity.Initialize();

        if (!EnsureSingleInstance())
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        TrackerSettings settings = TrackerSettings.Load();
        ThemeManager.Apply(settings.ThemeName);
        EnsureAutoStartDefault(settings);
        _db = OpenDatabase(settings);
        if (_db is null)
        {
            Cleanup();
            Shutdown();
            return;
        }

        _tracker = new ActivityTracker(_db, settings);
        _viewModel = new MainViewModel(_db, _tracker, settings);
        _tracker.Start();

        _window = new MainWindow(_viewModel);
        MainWindow = _window;

        SetupGlobalHotKey();

        _tray = new TrayIconService();
        _tray.ShowRequested += ShowMainWindow;
        _tray.ToggleRequested += () => _viewModel?.ToggleTrackingCommand.Execute(null);
        _tray.ExitRequested += ExitApplication;
        _tray.PetToggleRequested += ApplyPetVisibility;

        CreatePet(settings);
        _viewModel.SettingsSaved += saved => _pet?.ApplySettings(saved);
        _viewModel.ShowPetChanged += visible =>
        {
            if (!_petSyncGuard) ApplyPetVisibility(visible);
        };

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _trayTimer.Tick += (_, _) =>
        {
            LiveStatusSnapshot s = Snapshot();
            _tray?.SetTooltip($"今日活跃 {DurationTextConverter.Format(s.Active, true)} · {s.State}");
            _tray?.SetPaused(!s.Running);

            _settingsTick++;
            if (_cachedSettings is null || _settingsTick % 12 == 0) _cachedSettings = TrackerSettings.Load();
            CheckBreakReminder(s, _cachedSettings);
            CheckGoalReminder(s, _cachedSettings);
            CheckWaterReminder(_cachedSettings);
        };
        _trayTimer.Start();

        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || settings.StartMinimized;
        if (minimized)
        {
            _tray.Balloon("桌面时长记录器", "已在后台开始记录，双击托盘图标查看统计。");
            _hiddenNotified = true;
        }
        else
        {
            _window.Show();
        }
    }

    private const string MutexName = @"Local\DesktopTimeTracker.SingleInstance";

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 单实例检查。若已有实例：
    /// 1) 有可见窗口 → 激活它，当前进程退出；
    /// 2) 没有可见窗口（多半是卡死在托盘/残留进程）→ 询问用户是否结束旧实例后继续启动。
    /// </summary>
    private bool EnsureSingleInstance()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            _singleInstance = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew) return true;

            _singleInstance.Dispose();
            _singleInstance = null;

            if (TryActivateExistingInstance()) return false;

            var others = Process.GetProcessesByName("DesktopTimeTracker")
                .Where(p => p.Id != Environment.ProcessId)
                .ToList();
            if (others.Count == 0)
            {
                // 互斥量残留但没有进程：等一下再试
                Thread.Sleep(300);
                continue;
            }

            MessageBoxResult answer = MessageBox.Show(
                "检测到桌面时长记录器仍在后台运行（可能已最小化到托盘或已卡住），无法再次启动。\n\n" +
                "是否结束旧实例并继续启动？",
                "旧实例仍在运行", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return false;

            foreach (Process p in others)
            {
                try { p.Kill(); p.WaitForExit(2000); } catch (Exception ex) { Log(ex); }
                finally { p.Dispose(); }
            }
            Thread.Sleep(400);
        }

        _singleInstance = new Mutex(true, MutexName, out bool created);
        return created;
    }

    private static bool TryActivateExistingInstance()
    {
        foreach (Process p in Process.GetProcessesByName("DesktopTimeTracker"))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return true;
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return false;
    }

    private readonly record struct LiveStatusSnapshot(double Active, string State, bool Running, double ContinuousActive);

    // Requirement: auto-start on boot by default. Only applied once; a later deliberate
    // disable in Settings sets AutoStartConfigured and is never overridden here.
    private static void EnsureAutoStartDefault(TrackerSettings settings)
    {
        try
        {
            // First-run default: turn auto-start on and remember the choice.
            if (!settings.AutoStartConfigured)
            {
                AutoStartService.Set(true);
                settings.AutoStartWithWindows = true;
                settings.AutoStartConfigured = true;
                settings.Save();
                return;
            }

            // Self-heal: the user chose to keep auto-start ON, but the registry entry
            // may point at a stale path (e.g. the folder was moved). Re-point it at the
            // current executable. Only done when the user's intent is "enabled", so a
            // deliberate disable is never resurrected.
            bool enabled = AutoStartService.IsEnabled();
            bool pointsAtCurrent = AutoStartService.IsPointingAtCurrentExecutable();
            LogInfo($"EnsureAutoStartDefault: AutoStartWithWindows={settings.AutoStartWithWindows}, " +
                    $"IsEnabled={enabled}, IsPointingAtCurrent={pointsAtCurrent}, " +
                    $"ProcessPath={Environment.ProcessPath}");
            if (settings.AutoStartWithWindows && enabled && !pointsAtCurrent)
            {
                AutoStartService.Set(true);
                LogInfo("EnsureAutoStartDefault: re-pointed registry to current executable.");
            }
        }
        catch (Exception ex) { Log(ex); }
    }

    private LiveStatusSnapshot Snapshot()
    {
        Models.LiveStatus s = _tracker?.Status ?? Models.LiveStatus.Idle;
        return new LiveStatusSnapshot(s.TodayActiveSeconds, s.StateText, s.IsRunning, s.ContinuousActiveSeconds);
    }

    // ---------- 全局快捷键 Ctrl+Alt+P ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_P = 0x50;
    private const int WM_HOTKEY = 0x0312;

    private void SetupGlobalHotKey()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(_window!).EnsureHandle();
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(WndProc);
            RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, VK_P);
        }
        catch (Exception ex) { Log(ex); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _viewModel?.ToggleTrackingCommand.Execute(null);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- 久坐 / 目标提醒 ----------

    private void CheckBreakReminder(LiveStatusSnapshot s, TrackerSettings st)
    {
        int minutes = st.BreakReminderMinutes;
        if (minutes <= 0) return;
        double threshold = minutes * 60.0;
        if (s.ContinuousActive >= threshold && s.ContinuousActive - _lastBreakNotifiedContinuous >= threshold)
        {
            _lastBreakNotifiedContinuous = s.ContinuousActive;
            _tray?.Balloon("起来活动一下吧 ♪", $"你已经连续专注 {minutes} 分钟啦，记得起身喝口水、远眺放松一下。");
            _pet?.Speak("已经专注好久啦，站起来伸展一下吧～");
        }
        if (s.ContinuousActive < 5) _lastBreakNotifiedContinuous = 0; // 离开后重置
    }

    /// <summary>
    /// 喝水提醒：完全按本地时钟的整点触发，与程序运行时长无关。
    /// 例如配置 "10,11,14,15,16,17" 就在这些整点各提醒一次。
    /// </summary>
    private void CheckWaterReminder(TrackerSettings st)
    {
        if (!st.WaterReminderEnabled) return;
        DateTime now = DateTime.Now;
        if (now.Minute >= 2) return; // 整点后的 2 分钟窗口内触发，避免计时器抖动漏掉

        string key = now.ToString("yyyy-MM-dd HH");
        if (_waterNotifiedKey == key) return; // 这个整点已经提醒过
        if (!TrackerSettings.ParseHours(st.WaterReminderHours).Contains(now.Hour)) return;

        _waterNotifiedKey = key;
        _tray?.Balloon("该喝水啦 ♪", $"{now.Hour} 点整啦，起身倒杯水，顺便活动一下肩颈吧。");
        _pet?.Speak($"{now.Hour} 点啦，该喝水了哦～");
    }

    private void CheckGoalReminder(LiveStatusSnapshot s, TrackerSettings st)
    {
        int minutes = st.DailyGoalMinutes;
        if (minutes <= 0) return;
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (s.Active >= minutes * 60.0 && _goalNotifiedDay != today)
        {
            _goalNotifiedDay = today;
            _tray?.Balloon("今日目标达成 ♪", $"已完成 {minutes / 60.0:0.#} 小时的活跃目标，干得漂亮！");
            _pet?.Speak("今日目标达成！你太棒啦～");
        }
    }

    private static TrackerDatabase? OpenDatabase(TrackerSettings settings)
    {
        while (true)
        {
            try
            {
                return new TrackerDatabase(settings.DatabasePath);
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBoxResult answer = MessageBox.Show(
                    $"无法打开数据库：\n{settings.DatabasePath}\n\n{ex.Message}\n\n是否改用其他位置？\n选择“否”将退出程序。",
                    "数据库不可用", MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (answer != MessageBoxResult.Yes) return null;

                var dialog = new SaveFileDialog
                {
                    Title = "选择数据库保存位置",
                    Filter = "SQLite 数据库 (*.db)|*.db",
                    FileName = "data.db",
                    AddExtension = true,
                    DefaultExt = ".db",
                    OverwritePrompt = false
                };
                if (dialog.ShowDialog() != true) return null;
                settings.DatabasePath = dialog.FileName;
                try { settings.Save(); } catch (Exception saveEx) { Log(saveEx); }
            }
        }
    }

    // ---------- 桌面小宠物 ----------

    private void CreatePet(TrackerSettings settings)
    {
        try
        {
            _pet = new Views.PetWindow(DescribeStats, settings);
            _pet.OpenMainRequested += ShowMainWindow;
            _pet.ModelStatusChanged += message => _viewModel?.ReportPetModelStatus(message);
            _pet.HideRequested += () => ApplyPetVisibility(false);

            if (settings.PetLeft > 0 || settings.PetTop > 0)
            {
                _pet.Left = settings.PetLeft;
                _pet.Top = settings.PetTop;
            }
            _pet.EnsureOnScreen();
            if (settings.ShowPet) _pet.Show();
            _tray?.SetPetVisible(settings.ShowPet);
        }
        catch (Exception ex) { Log(ex); }
    }

    private void ApplyPetVisibility(bool visible)
    {
        _petSyncGuard = true;
        try
        {
            if (_pet is not null)
            {
                if (visible)
                {
                    _pet.Show();
                    _pet.EnsureOnScreen();
                }
                else
                {
                    _pet.Hide();
                }
            }
            _tray?.SetPetVisible(visible);
            _viewModel?.SetShowPetQuiet(visible);
            _cachedSettings = null;
        }
        catch (Exception ex) { Log(ex); }
        finally { _petSyncGuard = false; }
    }

    /// <summary>供桌宠随机"说话"时描述今日状态。</summary>
    private string? DescribeStats()
    {
        Models.LiveStatus s = _tracker?.Status ?? Models.LiveStatus.Idle;
        if (!s.IsRunning) return "记录暂停中，记得回来继续哦。";
        if (s.TodayActiveSeconds < 60) return "今天还没开始多久呢，一起加油吧！";
        string text = DurationTextConverter.Format(s.TodayActiveSeconds, true);
        return s.IsAway
            ? $"你今天已经专注 {text} 啦，先休息一下吧～"
            : $"今天已经专注 {text} 了，好厉害！";
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _ = _viewModel?.RefreshDataAsync(true);
    }

    /// <summary>Called by MainWindow when the user hits the close button.</summary>
    public void HideToTray()
    {
        _window?.Hide();
        if (_hiddenNotified) return;
        _hiddenNotified = true;
        _tray?.Balloon("窗口已收起 ♪", "记录仍在后台安静地进行着，不会中断。双击托盘图标随时回来，右键图标可选择退出。");
    }

    public void ExitApplication()
    {
        _window?.PrepareForExit();
        Cleanup();
        Shutdown(0);
        // 兜底：若仍有后台线程（如卡在 UI Automation 调用）阻止进程退出，强制结束，
        // 避免出现"已退出但进程残留、无法再次启动"的情况。数据已在 Cleanup 中落盘。
        Environment.Exit(0);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => Cleanup();

    private void OnExit(object sender, ExitEventArgs e) => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _trayTimer?.Stop();
        _tray?.Dispose();

        try
        {
            if (_pet is not null)
            {
                var st = TrackerSettings.Load();
                st.PetLeft = _pet.Left;
                st.PetTop = _pet.Top;
                st.ShowPet = _pet.IsVisible;
                st.Save();
                _pet.Close();
                _pet = null;
            }
        }
        catch (Exception ex) { Log(ex); }
        try
        {
            if (_window is not null && _hotkeySource is not null)
            {
                IntPtr hwnd = new WindowInteropHelper(_window).Handle;
                UnregisterHotKey(hwnd, HotkeyId);
                _hotkeySource.RemoveHook(WndProc);
            }
        }
        catch (Exception ex) { Log(ex); }
        _viewModel?.Dispose();
        try { _tracker?.Stop(); } catch (Exception ex) { Log(ex); }
        try { _db?.Dispose(); } catch (Exception ex) { Log(ex); }

        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
            _singleInstance = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        // Keep recording alive: a UI fault must not silently stop the log.
        e.Handled = true;
        _viewModel?.ReportError($"界面异常：{e.Exception.Message}");
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(TrackerSettings.DefaultFolder);
            File.AppendAllText(
                Path.Combine(TrackerSettings.DefaultFolder, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void LogInfo(string message)
    {
        try
        {
            Directory.CreateDirectory(TrackerSettings.DefaultFolder);
            File.AppendAllText(
                Path.Combine(TrackerSettings.DefaultFolder, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {message}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}