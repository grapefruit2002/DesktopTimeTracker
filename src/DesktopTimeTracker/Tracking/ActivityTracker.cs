using System.Diagnostics;
using System.IO;
using DesktopTimeTracker.Data;
using DesktopTimeTracker.Models;
using DesktopTimeTracker.Native;
using Microsoft.Win32;

namespace DesktopTimeTracker.Tracking;

/// <summary>
/// Polls the foreground window on a dedicated MTA thread and turns it into app/web sessions.
/// A session is one continuous bout of real use: it ends when the foreground process changes,
/// when the user goes idle, or at midnight.
/// </summary>
public sealed class ActivityTracker : IDisposable
{
    private static readonly HashSet<string> LockProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "LockApp", "LogonUI", "ScreenSaver" };

    private static readonly HashSet<string> ShellNoise = new(StringComparer.OrdinalIgnoreCase)
        { "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "RuntimeBroker" };

    private sealed class OpenApp
    {
        public long Id;
        public uint Pid;
        public string ProcessName = "";
        public string ExePath = "";
        public string Title = "";
        public bool IsBrowser;
        public DateTime Start;
        public double Seconds;
    }

    private sealed class OpenWeb
    {
        public long Id;
        public string Key = "";
        public string Url = "";
        public string Domain = "";
        public string Title = "";
        public string Browser = "";
        public DateTime Start;
        public double Seconds;
    }

    private readonly TrackerDatabase _db;
    private readonly BrowserUrlReader _urlReader;
    private readonly object _statusGate = new();
    private readonly object _flushGate = new();

    private TrackerSettings _settings;
    private LiveStatus _status = LiveStatus.Idle;
    private volatile bool _running;
    private volatile bool _sessionLocked;
    private volatile bool _suspended;
    private volatile bool _resumePending;
    private volatile string? _lastError;

    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private OpenApp? _app;
    private OpenWeb? _web;
    private string _day = "";
    private double _active;
    private double _away;
    private double _continuousActive;
    private DateTime? _first;
    private DateTime? _last;
    private int _switches;
    private DateTime _lastFlush;

    private uint _resolvedPid;
    private string _resolvedName = "";
    private string _resolvedPath = "";
    private IntPtr _uwpHostWindow;
    private uint _uwpContentPid;

    public ActivityTracker(TrackerDatabase db, TrackerSettings settings)
    {
        _db = db;
        _settings = settings;
        _urlReader = new BrowserUrlReader(settings.UrlReadIntervalMs);
    }

    public LiveStatus Status
    {
        get { lock (_statusGate) return _status; }
    }

    public string? LastError => _lastError;
    public bool IsRunning => _running;
    public string CurrentDay => _day;

    public void Start()
    {
        if (_thread is not null) return;

        _db.PruneEmptySessions();
        LoadDay(TrackerDatabase.DayKey(DateTime.Now));
        _lastFlush = DateTime.Now;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _running = true;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token))
        {
            IsBackground = true,
            Name = "ActivityTracker",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public void SetRunning(bool running)
    {
        if (running == _running) return;
        if (!running)
        {
            lock (_flushGate)
            {
                CloseApp(DateTime.Now);
                CloseWeb(DateTime.Now);
                FlushDay();
            }
        }
        else
        {
            _urlReader.Invalidate();
        }
        _running = running;
        PublishStatus();
    }

    public void ApplySettings(TrackerSettings settings)
    {
        _settings = settings;
        _urlReader.UpdateInterval(settings.UrlReadIntervalMs);
        _urlReader.Invalidate();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Sleep must not be recorded as active or away. Close open sessions at the sleep
            // onset so their end_time is real, then stop ticking until resume. The process
            // stays resident (background thread); only recording pauses.
            _suspended = true;
            lock (_flushGate)
            {
                CloseApp(DateTime.Now);
                CloseWeb(DateTime.Now);
                FlushDay();
            }
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _suspended = false;
            _resumePending = true;
            _urlReader.Invalidate();
        }
        PublishStatus();
    }

    private void Loop(CancellationToken ct)
    {
        var lastTick = DateTime.Now;
        uint previousInputTick = ReadInputTick();
        double unconfirmedActive = 0;
        bool wasAway = true;

        // 后台线程的任何未捕获异常都会直接杀进程——整个循环包 try/catch
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int delay = _running ? _settings.PollIntervalMs : 500;
                if (ct.WaitHandle.WaitOne(delay)) break;
                if (!_running) { lastTick = DateTime.Now; continue; }
                if (_suspended) { lastTick = DateTime.Now; continue; }

                var now = DateTime.Now;
                double rawElapsed = (now - lastTick).TotalSeconds;
                lastTick = now;
                if (rawElapsed <= 0) continue;

                if (_resumePending)
                {
                    _resumePending = false;
                    unconfirmedActive = 0;
                    wasAway = true;
                    _urlReader.Invalidate();
                    continue;
                }

                double tolerance = Math.Max(5.0, _settings.PollIntervalMs / 1000.0 * 4);
                if (rawElapsed > tolerance)
                {
                    lock (_flushGate)
                    {
                        CloseApp(now);
                        CloseWeb(now);
                        FlushDay();
                    }
                    unconfirmedActive = 0;
                    wasAway = true;
                    _urlReader.Invalidate();
                    continue;
                }

                try
                {
                    Tick(now, rawElapsed, ref previousInputTick, ref unconfirmedActive, ref wasAway);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = $"{ex.GetType().Name}: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
            catch (Exception ex)
            {
                // 循环顶层异常：记录 + 跳过本 tick 继续跑
                _lastError = $"LoopTop: {ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(ex);
                // 防止异常风暴：让线程喘口气
                Thread.Sleep(1000);
            }
        }
    }

    private static uint ReadInputTick()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        return NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    private void Tick(DateTime now, double elapsed, ref uint previousInputTick, ref double unconfirmedActive, ref bool wasAway)
    {
        uint inputTick = ReadInputTick();
        if (inputTick != previousInputTick)
        {
            // Fresh input confirms everything accumulated so far was genuine use.
            unconfirmedActive = 0;
            previousInputTick = inputTick;
        }

        long idleMs = NativeMethods.GetIdleMilliseconds();
        IntPtr fg = NativeMethods.GetForegroundWindow();
        bool isShell = fg == IntPtr.Zero || fg == NativeMethods.GetShellWindow();

        uint pid = isShell ? 0 : NativeMethods.GetWindowProcessId(fg);
        if (!isShell)
        {
            ResolveProcess(pid);
            if (string.Equals(_resolvedName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (fg != _uwpHostWindow)
                {
                    _uwpHostWindow = fg;
                    _uwpContentPid = NativeMethods.GetUwpContentProcessId(fg);
                }
                if (_uwpContentPid != 0)
                {
                    pid = _uwpContentPid;
                    ResolveProcess(pid);
                }
            }
            else
            {
                _uwpHostWindow = IntPtr.Zero;
                _uwpContentPid = 0;
            }
        }

        string title = NativeMethods.GetWindowTitle(fg);
        bool idle = idleMs >= _settings.IdleThresholdSeconds * 1000L;
        bool locked = LockProcesses.Contains(_resolvedName);
        bool away = isShell || idle || locked || _sessionLocked;

        string today = TrackerDatabase.DayKey(now);

        lock (_flushGate)
        {
            if (today != _day) RollDay(today, now);

            if (away)
            {
                if (!wasAway)
                {
                    // The user actually stopped at the last input event, not at the moment the
                    // threshold was crossed. Give those seconds back to "away".
                    double giveBack = Math.Min(unconfirmedActive, _active);
                    _active -= giveBack;
                    _away += giveBack;
                    DateTime leftAt = idleMs > 0 ? now.AddMilliseconds(-idleMs) : now;
                    CloseApp(leftAt, -giveBack);
                    CloseWeb(leftAt, -giveBack);
                    unconfirmedActive = 0;
                    _urlReader.Invalidate();
                }
                _away += elapsed;
                _continuousActive = 0;
                wasAway = true;
            }
            else
            {
                _active += elapsed;
                _continuousActive += elapsed;
                unconfirmedActive += elapsed;
                _first ??= now;
                _last = now;
                wasAway = false;

                bool ignored = ShellNoise.Contains(_resolvedName)
                    || _settings.IgnoredProcesses.Contains(_resolvedName, StringComparer.OrdinalIgnoreCase);

                // The interval just elapsed belonged to whatever was foreground before this sample.
                if (_app is not null) _app.Seconds += elapsed;
                if (_web is not null) _web.Seconds += elapsed;
                bool hadApp = _app is not null;
                bool hadWeb = _web is not null;

                if (ignored)
                {
                    CloseApp(now);
                    CloseWeb(now);
                }
                else
                {
                    bool appContinued = hadApp && _app!.Pid == pid;
                    if (!appContinued)
                    {
                        CloseApp(now);
                        CloseWeb(now);
                        _switches++;
                        OpenAppSession(now, elapsed, hadApp, pid, title);
                    }
                    else
                    {
                        _app!.Title = title;
                    }

                    UpdateWeb(now, elapsed, fg, title, hadWeb, appContinued);
                }
            }

            if ((now - _lastFlush).TotalSeconds >= _settings.FlushIntervalSeconds) FlushDay();
        }

        PublishStatus();
    }

    private void UpdateWeb(DateTime now, double elapsed, IntPtr fg, string title, bool hadWeb, bool appContinued)
    {
        if (_app is null || !_app.IsBrowser || !_settings.TrackWebPages)
        {
            CloseWeb(now);
            return;
        }

        BrowserReading reading = _urlReader.Read(fg, title, _app.ProcessName);
        string key = reading.Incognito ? "\u0001incognito" : reading.Url;

        // A transient empty UIA read must not split one visit into two sessions.
        if (key.Length == 0 && _web is not null) return;

        if (_web is not null && string.Equals(_web.Key, key, StringComparison.Ordinal))
        {
            _web.Title = reading.PageTitle;
            return;
        }

        CloseWeb(now);
        if (key.Length == 0) return;

        // When the browser was already foreground we can backdate the new page by one interval.
        bool backdate = !hadWeb && appContinued;
        DateTime start = backdate ? now.AddSeconds(-elapsed) : now;
        _web = new OpenWeb
        {
            Id = _db.BeginWebSession(_day, _app.ProcessName, reading.Url, reading.Domain, reading.PageTitle, start),
            Key = key,
            Url = reading.Url,
            Domain = reading.Domain,
            Title = reading.PageTitle,
            Browser = BrowserUrlReader.BrowserLabel(_app.ProcessName),
            Start = start,
            Seconds = backdate ? elapsed : 0
        };
    }

    private void OpenAppSession(DateTime now, double elapsed, bool hadApp, uint pid, string title)
    {
        DateTime start = hadApp ? now : now.AddSeconds(-elapsed);
        bool isBrowser = BrowserUrlReader.IsBrowser(_resolvedName);
        _app = new OpenApp
        {
            Id = _db.BeginAppSession(_day, _resolvedName, _resolvedPath, title, start, isBrowser),
            Pid = pid,
            ProcessName = _resolvedName,
            ExePath = _resolvedPath,
            Title = title,
            IsBrowser = isBrowser,
            Start = start,
            Seconds = hadApp ? 0 : elapsed
        };
    }

    private void CloseApp(DateTime end, double correction = 0)
    {
        if (_app is null) return;
        double seconds = Math.Max(0, _app.Seconds + correction);
        _db.UpdateAppSession(_app.Id, end < _app.Start ? _app.Start : end, seconds, _app.Title);
        _app = null;
    }

    private void CloseWeb(DateTime end, double correction = 0)
    {
        if (_web is null) return;
        double seconds = Math.Max(0, _web.Seconds + correction);
        _db.UpdateWebSession(_web.Id, end < _web.Start ? _web.Start : end, seconds, _web.Title);
        _web = null;
    }

    private void ResolveProcess(uint pid)
    {
        if (pid == 0)
        {
            _resolvedPid = 0; _resolvedName = ""; _resolvedPath = "";
            return;
        }
        if (pid == _resolvedPid && _resolvedName.Length > 0) return;

        _resolvedPid = pid;
        _resolvedPath = NativeMethods.TryGetProcessPath(pid) ?? "";
        _resolvedName = _resolvedPath.Length > 0 ? Path.GetFileNameWithoutExtension(_resolvedPath) : "";

        if (_resolvedName.Length == 0)
        {
            try { _resolvedName = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { _resolvedName = $"pid_{pid}"; }
            catch (InvalidOperationException) { _resolvedName = $"pid_{pid}"; }
        }
    }

    private void RollDay(string today, DateTime now)
    {
        DateTime boundary = now.Date;
        double overshoot = Math.Min((now - boundary).TotalSeconds, 5);
        CloseApp(boundary, -overshoot);
        CloseWeb(boundary, -overshoot);
        _active = Math.Max(0, _active - overshoot);
        FlushDay();
        LoadDay(today);
        _urlReader.Invalidate();
    }

    private void LoadDay(string day)
    {
        DayTotals totals = _db.LoadDay(day);
        _day = day;
        _active = totals.ActiveSeconds;
        _away = totals.AwaySeconds;
        _first = totals.FirstStart;
        _last = totals.LastEnd;
        _switches = totals.AppSwitches;
    }

    private void FlushDay()
    {
        if (_day.Length == 0) return;
        DateTime now = DateTime.Now;
        _db.FlushBatch(
            _day,
            new DayTotals(_active, _away, _first, _last, _switches),
            _app is null ? null : new SessionFlush(_app.Id, false, _app.Start.AddSeconds(_app.Seconds), _app.Seconds, _app.Title),
            _web is null ? null : new SessionFlush(_web.Id, true, _web.Start.AddSeconds(_web.Seconds), _web.Seconds, _web.Title),
            now);
        _lastFlush = now;
    }

    private void PublishStatus()
    {
        OpenApp? app = _app;
        OpenWeb? web = _web;
        string state = !_running ? "已暂停"
            : _suspended ? "休眠中"
            : _sessionLocked ? "已锁定"
            : app is null && web is null ? "空闲 / 桌面"
            : "记录中";

        var status = new LiveStatus(
            _running,
            _running && app is null,
            app?.ProcessName ?? "",
            app?.Title ?? "",
            web?.Url ?? "",
            web?.Domain ?? "",
            app?.Seconds ?? 0,
            web?.Seconds ?? 0,
            _active,
            _away,
            _continuousActive,
            state);

        lock (_statusGate) _status = status;
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _thread?.Join(1500);
        _thread = null;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        lock (_flushGate)
        {
            CloseApp(DateTime.Now);
            CloseWeb(DateTime.Now);
            FlushDay();
        }
        _cts?.Dispose();
        _cts = null;
        PublishStatus();
    }

    public void Dispose() => Stop();
}