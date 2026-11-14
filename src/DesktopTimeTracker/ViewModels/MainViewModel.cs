using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DesktopTimeTracker.Converters;
using DesktopTimeTracker.Data;
using DesktopTimeTracker.Models;
using DesktopTimeTracker.Services;
using DesktopTimeTracker.Tracking;
using Microsoft.Win32;

namespace DesktopTimeTracker.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly TrackerDatabase _db;
    private readonly ActivityTracker _tracker;
    private readonly DispatcherTimer _timer;
    private TrackerSettings _settings;
    public TrackerSettings Settings => _settings;

    private int _refreshBusy;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _disposed;

    public MainViewModel(TrackerDatabase db, ActivityTracker tracker, TrackerSettings settings)
    {
        _db = db;
        _tracker = tracker;
        _settings = settings;

        ToggleTrackingCommand = new RelayCommand(() => _tracker.SetRunning(!_tracker.IsRunning));
        RefreshCommand = new RelayCommand(() => _ = RefreshDataAsync(true));
        GoTodayCommand = new RelayCommand(() => SelectedDay = TrackerDatabase.DayKey(DateTime.Now));
        PrevDayCommand = new RelayCommand(() => ShiftDay(-1));
        NextDayCommand = new RelayCommand(() => ShiftDay(1));
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => !IsImportingPetModel);
        ImportPetModelCommand = new RelayCommand(ImportPetModel, () => !IsImportingPetModel);
        RenamePetModelCommand = new RelayCommand(RenamePetModel, () => !IsImportingPetModel);
        OpenModelsFolderCommand = new RelayCommand(OpenModelsFolder);
        OpenBackgroundFolderCommand = new RelayCommand(OpenBackgroundFolder);
        OpenDatabaseFolderCommand = new RelayCommand(OpenDatabaseFolder);
        ChangeDatabasePathCommand = new RelayCommand(ChangeDatabasePath);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        VacuumCommand = new RelayCommand(Vacuum);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ImportBackgroundCommand = new RelayCommand(ImportBackground);
        RenameBackgroundCommand = new RelayCommand(RenameBackground);
        DeleteBackgroundCommand = new RelayCommand(DeleteBackground);
        SyncBackgroundsCommand = new RelayCommand(() => SyncBackgrounds());
        NavigateToSettingsCommand = new RelayCommand(p => NavigateToSettings(p as string));
        SelectDayCommand = new RelayCommand(p =>
        {
            if (p is string day && day.Length > 0) SelectedDay = day;
            SelectedTabIndex = 0;
        });

        foreach (string day in _db.GetDays(400).Select(d => d.Day)) AvailableDays.Add(day);
        string today = TrackerDatabase.DayKey(DateTime.Now);
        if (!AvailableDays.Contains(today)) AvailableDays.Insert(0, today);
        _selectedDay = today;

        _idleThresholdSeconds = settings.IdleThresholdSeconds;
        _pollIntervalMs = settings.PollIntervalMs;
        _trackWebPages = settings.TrackWebPages;
        _startMinimized = settings.StartMinimized;
        _autoStart = AutoStartService.IsEnabled();
        _ignoredProcessesText = string.Join(", ", settings.IgnoredProcesses);
        _breakReminderMinutes = settings.BreakReminderMinutes;
        _dailyGoalMinutes = settings.DailyGoalMinutes;
        _waterReminderEnabled = settings.WaterReminderEnabled;
        _waterReminderHoursText = settings.WaterReminderHours;
        _showPet = settings.ShowPet;
        _petClickLinesText = string.Join(Environment.NewLine, settings.PetClickLines);
        _petMenuLinesText = string.Join(Environment.NewLine, settings.PetMenuLines);
        foreach (PetModel model in settings.PetModels) PetModelChoices.Add(model);
        _selectedPetModelId = settings.SelectedPetModelId;
        SyncPetModels();
        foreach (BackgroundImage bg in settings.BackgroundImages) BackgroundChoices.Add(bg);
        _selectedBackgroundId = string.IsNullOrWhiteSpace(settings.SelectedBackgroundId) ? BackgroundImage.DefaultId : settings.SelectedBackgroundId;
        _selectedBackgroundMode = (BackgroundMode)settings.BackgroundMode;
        _backgroundOpacity = settings.BackgroundOpacity;
        _backgroundOpacityText = _backgroundOpacity.ToString("0.##");
        SyncBackgrounds();
        _categoryRulesText = string.Join(Environment.NewLine,
            settings.CategoryRules.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        _flushIntervalText = $"数据每 {settings.FlushIntervalSeconds} 秒落盘一次 · 关闭窗口不会停止记录";

        if (_db.RecoveryNotice is string recovery)
        {
            NoticeText = recovery;
            HasNotice = true;
        }
        DismissNoticeCommand = new RelayCommand(() => HasNotice = false);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        UpdateLive();
        _ = RefreshDataAsync(true);
        _ = LoadTrendAsync();
    }

    // ---------- collections ----------

    public ObservableCollection<string> AvailableDays { get; } = [];
    public ObservableCollection<AppUsage> TopApps { get; } = [];
    public ObservableCollection<WebUsage> TopSites { get; } = [];
    public ObservableCollection<ChartBar> HourBars { get; } = [];
    public ObservableCollection<TimelineEntry> Timeline { get; } = [];
    public ObservableCollection<AppSession> AppSessions { get; } = [];
    public ObservableCollection<AppUsage> AppUsageRows { get; } = [];
    public ObservableCollection<WebSession> WebSessions { get; } = [];
    public ObservableCollection<WebUsage> WebUsageRows { get; } = [];
    public ObservableCollection<DaySummary> Days { get; } = [];
    public ObservableCollection<ChartBar> DayBars { get; } = [];
    public ObservableCollection<CategoryUsage> Categories { get; } = [];
    public ObservableCollection<ChartBar> TrendBars { get; } = [];
    public ObservableCollection<AppUsage> TrendTopApps { get; } = [];

    // ---------- live header ----------

    private string _stateText = "启动中";
    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }

    private bool _isRecording;
    public bool IsRecording { get => _isRecording; private set => SetField(ref _isRecording, value); }

    private bool _isAway;
    public bool IsAway { get => _isAway; private set => SetField(ref _isAway, value); }

    private string _todayActiveText = "0秒";
    public string TodayActiveText { get => _todayActiveText; private set => SetField(ref _todayActiveText, value); }

    private string _todayAwayText = "0秒";
    public string TodayAwayText { get => _todayAwayText; private set => SetField(ref _todayAwayText, value); }

    private string _currentAppText = "—";
    public string CurrentAppText { get => _currentAppText; private set => SetField(ref _currentAppText, value); }

    private string _currentTitleText = "";
    public string CurrentTitleText { get => _currentTitleText; private set => SetField(ref _currentTitleText, value); }

    private string _currentUrlText = "";
    public string CurrentUrlText { get => _currentUrlText; private set => SetField(ref _currentUrlText, value); }

    private bool _hasCurrentUrl;
    public bool HasCurrentUrl { get => _hasCurrentUrl; private set => SetField(ref _hasCurrentUrl, value); }

    private string _currentAppDurationText = "0秒";
    public string CurrentAppDurationText { get => _currentAppDurationText; private set => SetField(ref _currentAppDurationText, value); }

    private string _currentWebDurationText = "0秒";
    public string CurrentWebDurationText { get => _currentWebDurationText; private set => SetField(ref _currentWebDurationText, value); }

    private string _toggleButtonText = "暂停记录";
    public string ToggleButtonText { get => _toggleButtonText; private set => SetField(ref _toggleButtonText, value); }

    private string _errorText = "";
    public string ErrorText { get => _errorText; private set => SetField(ref _errorText, value); }

    private bool _hasError;
    public bool HasError { get => _hasError; private set => SetField(ref _hasError, value); }

    private string _noticeText = "";
    public string NoticeText { get => _noticeText; private set => SetField(ref _noticeText, value); }

    private bool _hasNotice;
    public bool HasNotice { get => _hasNotice; private set => SetField(ref _hasNotice, value); }

    // ---------- overview ----------

    private string _overviewActiveText = "0秒";
    public string OverviewActiveText { get => _overviewActiveText; private set => SetField(ref _overviewActiveText, value); }

    private string _overviewAwayText = "0秒";
    public string OverviewAwayText { get => _overviewAwayText; private set => SetField(ref _overviewAwayText, value); }

    private string _overviewSpanText = "—";
    public string OverviewSpanText { get => _overviewSpanText; private set => SetField(ref _overviewSpanText, value); }

    private int _appCount;
    public int AppCount { get => _appCount; private set => SetField(ref _appCount, value); }

    private int _webCount;
    public int WebCount { get => _webCount; private set => SetField(ref _webCount, value); }

    private int _switchCount;
    public int SwitchCount { get => _switchCount; private set => SetField(ref _switchCount, value); }

    private bool _hasTopApps;
    public bool HasTopApps { get => _hasTopApps; private set => SetField(ref _hasTopApps, value); }

    private bool _hasTopSites;
    public bool HasTopSites { get => _hasTopSites; private set => SetField(ref _hasTopSites, value); }

    private bool _hasTimeline;
    public bool HasTimeline { get => _hasTimeline; private set => SetField(ref _hasTimeline, value); }

    // ---------- filters ----------

    private string _selectedDay = "";
    public string SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!SetField(ref _selectedDay, value)) return;
            RaisePropertyChanged(nameof(IsTodaySelected));
            _ = RefreshDataAsync(true);
        }
    }

    public bool IsTodaySelected => _selectedDay == TrackerDatabase.DayKey(DateTime.Now);

    private string _appSearch = "";
    public string AppSearch
    {
        get => _appSearch;
        set { if (SetField(ref _appSearch, value)) _ = RefreshDataAsync(true); }
    }

    private string _webSearch = "";
    public string WebSearch
    {
        get => _webSearch;
        set { if (SetField(ref _webSearch, value)) _ = RefreshDataAsync(true); }
    }

    private bool _appGrouped;
    public bool AppGrouped
    {
        get => _appGrouped;
        set { if (SetField(ref _appGrouped, value)) _ = RefreshDataAsync(true); }
    }

    private bool _webGrouped;
    public bool WebGrouped
    {
        get => _webGrouped;
        set { if (SetField(ref _webGrouped, value)) _ = RefreshDataAsync(true); }
    }

    private string _appRowsHint = "";
    public string AppRowsHint { get => _appRowsHint; private set => SetField(ref _appRowsHint, value); }

    private string _webRowsHint = "";
    public string WebRowsHint { get => _webRowsHint; private set => SetField(ref _webRowsHint, value); }

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetField(ref _selectedTabIndex, value); }

    // ---------- 趋势 / 目标 ----------

    private int _trendRangeDays = 7;
    public int TrendRangeDays
    {
        get => _trendRangeDays;
        set { if (SetField(ref _trendRangeDays, value)) _ = LoadTrendAsync(); }
    }

    private string _trendTotalText = "0秒";
    public string TrendTotalText { get => _trendTotalText; private set => SetField(ref _trendTotalText, value); }

    private string _trendAvgText = "0秒";
    public string TrendAvgText { get => _trendAvgText; private set => SetField(ref _trendAvgText, value); }

    private string _trendBestText = "—";
    public string TrendBestText { get => _trendBestText; private set => SetField(ref _trendBestText, value); }

    private string _goalText = "";
    public string GoalText { get => _goalText; private set => SetField(ref _goalText, value); }

    private double _goalProgress;
    public double GoalProgress { get => _goalProgress; private set => SetField(ref _goalProgress, value); }

    private bool _hasGoal;
    public bool HasGoal { get => _hasGoal; private set => SetField(ref _hasGoal, value); }

    // ---------- settings ----------

    public string DatabasePath => _db.DatabasePath;

    private string _databaseSizeText = "";
    public string DatabaseSizeText { get => _databaseSizeText; private set => SetField(ref _databaseSizeText, value); }

    private int _idleThresholdSeconds;
    public int IdleThresholdSeconds { get => _idleThresholdSeconds; set => SetField(ref _idleThresholdSeconds, value); }

    private int _pollIntervalMs;
    public int PollIntervalMs { get => _pollIntervalMs; set => SetField(ref _pollIntervalMs, value); }

    public sealed record OptionItem(int Value, string Label)
    {
        // 兜底：若控件模板未取到 ItemTemplate，ComboBox 会回退到 ToString()，
        // 返回 Label 可避免界面出现 "OptionItem { Value = ... }"。
        public override string ToString() => Label;
    }

    public OptionItem[] IdleChoices { get; } =
    [
        new(30, "30 秒"), new(60, "1 分钟"), new(120, "2 分钟"),
        new(180, "3 分钟"), new(300, "5 分钟"), new(600, "10 分钟"), new(900, "15 分钟")
    ];

    public OptionItem[] PollChoices { get; } =
    [
        new(500, "0.5 秒"), new(1000, "1 秒"), new(2000, "2 秒"), new(5000, "5 秒")
    ];

    private bool _trackWebPages;
    public bool TrackWebPages { get => _trackWebPages; set => SetField(ref _trackWebPages, value); }

    private bool _startMinimized;
    public bool StartMinimized { get => _startMinimized; set => SetField(ref _startMinimized, value); }

    private bool _autoStart;
    public bool AutoStartWithWindows { get => _autoStart; set => SetField(ref _autoStart, value); }

    private string _ignoredProcessesText = "";
    public string IgnoredProcessesText { get => _ignoredProcessesText; set => SetField(ref _ignoredProcessesText, value); }

    private string _settingsStatusText = "";
    public string SettingsStatusText { get => _settingsStatusText; private set => SetField(ref _settingsStatusText, value); }

    public string SettingsFilePath => _settings.SettingsPath;

    private string _flushIntervalText = "";
    public string FlushIntervalText { get => _flushIntervalText; private set => SetField(ref _flushIntervalText, value); }

    private int _breakReminderMinutes;
    public int BreakReminderMinutes { get => _breakReminderMinutes; set => SetField(ref _breakReminderMinutes, value); }

    private int _dailyGoalMinutes;
    public int DailyGoalMinutes { get => _dailyGoalMinutes; set => SetField(ref _dailyGoalMinutes, value); }

    private string _categoryRulesText = "";
    public string CategoryRulesText { get => _categoryRulesText; set => SetField(ref _categoryRulesText, value); }

    private bool _waterReminderEnabled = true;
    public bool WaterReminderEnabled { get => _waterReminderEnabled; set => SetField(ref _waterReminderEnabled, value); }

    private string _petClickLinesText = "";
    public string PetClickLinesText { get => _petClickLinesText; set => SetField(ref _petClickLinesText, value); }

    private string _petMenuLinesText = "";
    public string PetMenuLinesText { get => _petMenuLinesText; set => SetField(ref _petMenuLinesText, value); }

    public ObservableCollection<PetModel> PetModelChoices { get; } = [PetModel.Default];

    private string _selectedPetModelId = PetModel.DefaultId;
    public string SelectedPetModelId
    {
        get => _selectedPetModelId;
        set
        {
            if (value is not null && SetField(ref _selectedPetModelId, value))
                PetModelStatusText = "点击下方“保存设置”应用所选人物。";
        }
    }

    private bool _isImportingPetModel;
    public bool IsImportingPetModel
    {
        get => _isImportingPetModel;
        private set
        {
            SetField(ref _isImportingPetModel, value);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _petModelStatusText = "";
    public string PetModelStatusText { get => _petModelStatusText; private set => SetField(ref _petModelStatusText, value); }

    private string _newPetModelName = "";
    public string NewPetModelName { get => _newPetModelName; set => SetField(ref _newPetModelName, value); }

    public ObservableCollection<BackgroundImage> BackgroundChoices { get; } = [BackgroundImage.None];

    private string _selectedBackgroundId = BackgroundImage.DefaultId;
    public string SelectedBackgroundId
    {
        get => _selectedBackgroundId;
        set
        {
            if (!SetField(ref _selectedBackgroundId, value)) return;
            BackgroundStatusText = "已切换背景。";
        }
    }

    private BackgroundMode _selectedBackgroundMode = BackgroundMode.FillCover;
    public BackgroundMode SelectedBackgroundMode
    {
        get => _selectedBackgroundMode;
        set
        {
            if (!SetField(ref _selectedBackgroundMode, value)) return;
            BackgroundStatusText = "已切换布局方式。";
        }
    }

    public int SelectedBackgroundModeIndex
    {
        get => _selectedBackgroundMode switch
        {
            BackgroundMode.FillCover => 0,
            BackgroundMode.FillUniform => 1,
            BackgroundMode.CornerTopRight => 2,
            BackgroundMode.CornerBottomLeft => 3,
            _ => 0,
        };
        set
        {
            SelectedBackgroundMode = value switch
            {
                0 => BackgroundMode.FillCover,
                1 => BackgroundMode.FillUniform,
                2 => BackgroundMode.CornerTopRight,
                3 => BackgroundMode.CornerBottomLeft,
                _ => BackgroundMode.FillCover,
            };
        }
    }

    private double _backgroundOpacity = 0.52;
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            if (!SetField(ref _backgroundOpacity, value)) return;
            BackgroundStatusText = "点击下方“保存设置”应用所选透明度。";
        }
    }

    private string _backgroundOpacityText = "0.52";
    public string BackgroundOpacityText
    {
        get => _backgroundOpacityText;
        set
        {
            if (!SetField(ref _backgroundOpacityText, value)) return;
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                BackgroundOpacity = Math.Clamp(d, 0.05, 1.0);
        }
    }

    private string _newBackgroundName = "";
    public string NewBackgroundName { get => _newBackgroundName; set => SetField(ref _newBackgroundName, value); }

    private string _backgroundStatusText = "";
    public string BackgroundStatusText { get => _backgroundStatusText; private set => SetField(ref _backgroundStatusText, value); }

    public event Action<TrackerSettings>? SettingsSaved;
    public event Action<bool>? ShowPetChanged;
    public event Action? BackgroundChanged;

    private bool _showPet = true;
    public bool ShowPet
    {
        get => _showPet;
        set
        {
            if (!SetField(ref _showPet, value)) return;
            _settings.ShowPet = value;
            try { _settings.Save(); } catch (Exception) { }
            ShowPetChanged?.Invoke(value);
        }
    }

    /// <summary>由主程序同步开关状态，不触发事件（避免来回循环）。</summary>
    public void SetShowPetQuiet(bool value)
    {
        if (_showPet == value) return;
        _showPet = value;
        RaisePropertyChanged(nameof(ShowPet));
    }

    private string _waterReminderHoursText = "";
    public string WaterReminderHoursText { get => _waterReminderHoursText; set => SetField(ref _waterReminderHoursText, value); }



    // ---------- commands ----------

    public RelayCommand ToggleTrackingCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand GoTodayCommand { get; }
    public RelayCommand PrevDayCommand { get; }
    public RelayCommand NextDayCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ImportPetModelCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand OpenDatabaseFolderCommand { get; }
    public RelayCommand ChangeDatabasePathCommand { get; }
    public RelayCommand OpenUrlCommand { get; }
    public RelayCommand VacuumCommand { get; }
    public RelayCommand SelectDayCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand RenamePetModelCommand { get; }
    public RelayCommand OpenModelsFolderCommand { get; }
    public RelayCommand OpenBackgroundFolderCommand { get; }
    public RelayCommand ImportBackgroundCommand { get; }
    public RelayCommand RenameBackgroundCommand { get; }
    public RelayCommand DeleteBackgroundCommand { get; }
    public RelayCommand SyncBackgroundsCommand { get; }
    public RelayCommand NavigateToSettingsCommand { get; }

    public void ReportError(string message)
    {
        ErrorText = message;
        HasError = true;
    }

    // ---------- refresh ----------

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        UpdateLive();
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(10)) return;
        await RefreshDataAsync(false);
    }

    private void UpdateLive()
    {
        LiveStatus s = _tracker.Status;
        StateText = s.StateText;
        IsRecording = s.IsRunning && !s.IsAway;
        IsAway = s.IsAway;
        TodayActiveText = DurationTextConverter.Format(s.TodayActiveSeconds);
        TodayAwayText = DurationTextConverter.Format(s.TodayAwaySeconds);
        ToggleButtonText = s.IsRunning ? "暂停记录" : "继续记录";

        CurrentAppText = s.CurrentApp.Length > 0 ? s.CurrentApp : (s.IsRunning ? "桌面 / 无前台窗口" : "—");
        CurrentTitleText = s.CurrentTitle;
        CurrentUrlText = s.CurrentUrl.Length > 0 ? s.CurrentUrl : s.CurrentDomain;
        HasCurrentUrl = s.CurrentUrl.Length > 0;
        CurrentAppDurationText = DurationTextConverter.Format(s.CurrentAppSeconds, true);
        CurrentWebDurationText = DurationTextConverter.Format(s.CurrentWebSeconds, true);

        int goalMinutes = _settings.DailyGoalMinutes;
        HasGoal = goalMinutes > 0;
        if (HasGoal)
        {
            double goal = goalMinutes * 60.0;
            GoalProgress = goal > 0 ? Math.Clamp(s.TodayActiveSeconds / goal, 0, 1) : 0;
            GoalText = $"目标 {goalMinutes / 60.0:0.#} 小时 · 已完成 {DurationTextConverter.Format(s.TodayActiveSeconds)} ({GoalProgress:P0})";
        }

        string? error = _tracker.LastError;
        HasError = !string.IsNullOrEmpty(error);
        ErrorText = error ?? "";
    }

    public async Task RefreshDataAsync(bool force)
    {
        if (_disposed) return;
        if (!force && DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(2)) return;
        if (Interlocked.CompareExchange(ref _refreshBusy, 1, 0) != 0) return;

        string day = _selectedDay;
        string appSearch = _appSearch.Trim();
        string webSearch = _webSearch.Trim();
        bool appGrouped = _appGrouped;
        bool webGrouped = _webGrouped;

        try
        {
            TodayOverview overview = await Task.Run(() => _db.GetOverview(day, _settings)).ConfigureAwait(true);
            IReadOnlyList<DaySummary> days = await Task.Run(() => _db.GetDays(400)).ConfigureAwait(true);
            IReadOnlyList<AppSession> appSessions = appGrouped ? [] : await Task.Run(() => _db.GetAppSessions(day, appSearch)).ConfigureAwait(true);
            IReadOnlyList<AppUsage> appUsage = appGrouped ? await Task.Run(() => _db.GetAppUsage(day, appSearch)).ConfigureAwait(true) : [];
            IReadOnlyList<WebSession> webSessions = webGrouped ? [] : await Task.Run(() => _db.GetWebSessions(day, webSearch)).ConfigureAwait(true);
            IReadOnlyList<WebUsage> webUsage = webGrouped ? await Task.Run(() => _db.GetWebUsage(day, webSearch)).ConfigureAwait(true) : [];
            long size = await Task.Run(() => _db.GetSizeBytes()).ConfigureAwait(true);

            if (_disposed || day != _selectedDay) return;

            OverviewActiveText = DurationTextConverter.Format(overview.ActiveSeconds);
            OverviewAwayText = DurationTextConverter.Format(overview.AwaySeconds);
            OverviewSpanText = overview.FirstStart.HasValue && overview.LastEnd.HasValue
                ? $"{overview.FirstStart.Value:HH:mm} — {overview.LastEnd.Value:HH:mm}"
                : "—";
            AppCount = overview.AppCount;
            WebCount = overview.WebCount;
            SwitchCount = overview.AppSwitches;

            Replace(TopApps, overview.TopApps);
            Replace(TopSites, overview.TopSites);
            Replace(Categories, overview.Categories);
            Replace(Timeline, overview.Timeline);
            HasTopApps = TopApps.Count > 0;
            HasTopSites = TopSites.Count > 0;
            HasTimeline = Timeline.Count > 0;

            double maxHour = overview.Hours.Count > 0 ? overview.Hours.Max(h => h.Seconds) : 0;
            Replace(HourBars, overview.Hours.Select(h => new ChartBar(
                $"{h.Hour:00}", h.Seconds, maxHour > 0 ? h.Seconds / maxHour : 0,
                DurationTextConverter.Format(h.Seconds, true))));

            Replace(AppSessions, appSessions);
            Replace(AppUsageRows, appUsage);
            Replace(WebSessions, webSessions);
            Replace(WebUsageRows, webUsage);
            AppRowsHint = appGrouped ? $"{appUsage.Count} 个应用" : $"{appSessions.Count} 条记录";
            WebRowsHint = webGrouped ? $"{webUsage.Count} 个域名" : $"{webSessions.Count} 条记录";

            Replace(Days, days);
            var recent = days.Take(14).OrderBy(d => d.Day).ToList();
            double maxDay = recent.Count > 0 ? recent.Max(d => d.ActiveSeconds) : 0;
            Replace(DayBars, recent.Select(d => new ChartBar(
                d.Day.Length >= 10 ? d.Day[5..] : d.Day, d.ActiveSeconds,
                maxDay > 0 ? d.ActiveSeconds / maxDay : 0,
                DurationTextConverter.Format(d.ActiveSeconds, true))));

            var known = days.Select(d => d.Day).ToList();
            string today = TrackerDatabase.DayKey(DateTime.Now);
            if (!known.Contains(today)) known.Insert(0, today);
            known.Sort(StringComparer.Ordinal);
            known.Reverse();
            if (!AvailableDays.SequenceEqual(known)) Replace(AvailableDays, known);

            DatabaseSizeText = $"{size / 1024.0 / 1024.0:0.##} MB";
            _lastRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = $"读取数据库失败：{ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshBusy, 0);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items) target.Add(item);
    }

    private void ShiftDay(int delta)
    {
        if (!DateTime.TryParse(_selectedDay, out DateTime current)) current = DateTime.Now;
        SelectedDay = current.AddDays(delta).ToString("yyyy-MM-dd");
    }

    private async Task LoadTrendAsync()
    {
        int days = _trendRangeDays;
        string today = TrackerDatabase.DayKey(DateTime.Now);
        string fromDay = DateTime.Now.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
        try
        {
            var trend = await Task.Run(() => _db.GetTrend(days)).ConfigureAwait(true);
            var top = await Task.Run(() => _db.GetTopAppsRange(fromDay, today, 10)).ConfigureAwait(true);

            if (days != _trendRangeDays) return;

            double total = trend.Sum(t => t.ActiveSeconds);
            double avg = trend.Count > 0 ? total / trend.Count : 0;
            var best = trend.OrderByDescending(t => t.ActiveSeconds).FirstOrDefault();
            TrendTotalText = $"总活跃 {DurationTextConverter.Format(total)}";
            TrendAvgText = $"日均 {DurationTextConverter.Format(avg)}";
            TrendBestText = best is not null
                ? $"{best.Day[5..]} · {DurationTextConverter.Format(best.ActiveSeconds)}"
                : "—";

            double maxDay = trend.Count > 0 ? trend.Max(t => t.ActiveSeconds) : 0;
            Replace(TrendBars, trend.Select(t => new ChartBar(
                t.Day.Length >= 10 ? t.Day[5..] : t.Day, t.ActiveSeconds,
                maxDay > 0 ? t.ActiveSeconds / maxDay : 0,
                DurationTextConverter.Format(t.ActiveSeconds, true), t.Day)));

            Replace(TrendTopApps, top);
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = $"读取趋势失败：{ex.Message}";
        }
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出为 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"桌面时长_{_selectedDay}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var apps = _db.GetAppSessions(_selectedDay, "");
            var webs = _db.GetWebSessions(_selectedDay, "");
            var sb = new System.Text.StringBuilder();
            sb.Append('\uFEFF'); // UTF-8 BOM，Excel 打开不乱码
            sb.AppendLine("类型,开始时间,结束时间,时长(秒),名称,详情");
            foreach (var a in apps)
                sb.AppendLine($"应用,{a.Start:yyyy-MM-dd HH:mm:ss},{a.End:yyyy-MM-dd HH:mm:ss},{a.Seconds:F0},{Csv(a.ProcessName)},{Csv(a.WindowTitle)}");
            foreach (var w in webs)
                sb.AppendLine($"网页,{w.Start:yyyy-MM-dd HH:mm:ss},{w.End:yyyy-MM-dd HH:mm:ss},{w.Seconds:F0},{Csv(w.Domain)},{Csv(w.Url)}");
            File.WriteAllText(dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            SettingsStatusText = $"已导出 {apps.Count + webs.Count} 条记录。";
        }
        catch (Exception ex) { SettingsStatusText = $"导出失败：{ex.Message}"; }
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    // ---------- settings actions ----------

    private async void ImportPetModel()
    {
        if (IsImportingPetModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入桌宠人物模型",
            Filter = "VRM 人形模型 (*.vrm)|*.vrm",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        IsImportingPetModel = true;
        PetModelStatusText = "正在校验并复制模型…";
        try
        {
            PetModel model = await Task.Run(() => PetModelStore.Import(dialog.FileName, TrackerSettings.DefaultFolder));
            _settings.PetModels.Add(model);
            try
            {
                _settings.Save();
            }
            catch
            {
                _settings.PetModels.RemoveAll(item => item.Id == model.Id);
                File.Delete(Path.Combine(TrackerSettings.DefaultFolder, model.RelativePath));
                throw;
            }
            PetModelChoices.Add(model);
            SelectedPetModelId = model.Id;
            PetModelStatusText = $"已导入“{model.Name}”，点击下方“保存设置”切换人物。";
        }
        catch (Exception ex)
        {
            PetModelStatusText = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsImportingPetModel = false;
        }
    }

    public void ReportPetModelStatus(string message) => PetModelStatusText = message;

    /// <summary>扫描专门的 Models 文件夹：登记新模型、识别手动改名，并更新设置。</summary>
    public void SyncPetModels()
    {
        try
        {
            IReadOnlyList<PetModel> synced = PetModelStore.SyncFromFolder(
                TrackerSettings.DefaultFolder, _settings.PetModels);

            bool changed = synced.Count != _settings.PetModels.Count ||
                synced.Zip(_settings.PetModels).Any(pair =>
                    pair.First.Id != pair.Second.Id ||
                    !string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) ||
                    !string.Equals(pair.First.FileName, pair.Second.FileName, StringComparison.Ordinal));

            if (!changed) return;

            _settings.PetModels = synced.ToList();
            try { _settings.Save(); } catch (Exception) { }

            PetModelChoices.Clear();
            foreach (PetModel model in synced) PetModelChoices.Add(model);
            if (PetModelChoices.All(m => m.Id != _selectedPetModelId))
                _selectedPetModelId = PetModelChoices.FirstOrDefault()?.Id ?? PetModel.DefaultId;
            RaisePropertyChanged(nameof(SelectedPetModelId));
        }
        catch (Exception ex)
        {
            PetModelStatusText = $"同步模型文件夹失败：{ex.Message}";
        }
    }

    private void RenamePetModel()
    {
        if (IsImportingPetModel) return;

        PetModel? model = PetModelChoices.FirstOrDefault(item => item.Id == SelectedPetModelId);
        if (model is null || model.Id == PetModel.DefaultId)
        {
            PetModelStatusText = "默认人物不可重命名，请先选择一个导入的模型。";
            return;
        }

        string newName = NewPetModelName?.Trim() ?? "";
        if (newName.Length == 0)
        {
            PetModelStatusText = "请先在输入框中填写新名称。";
            return;
        }

        try
        {
            PetModel renamed = PetModelStore.Rename(TrackerSettings.DefaultFolder, model, newName);
            int index = _settings.PetModels.FindIndex(m => m.Id == renamed.Id);
            if (index >= 0) _settings.PetModels[index] = renamed; else _settings.PetModels.Add(renamed);
            _settings.Save();

            for (int i = 0; i < PetModelChoices.Count; i++)
                if (PetModelChoices[i].Id == renamed.Id) PetModelChoices[i] = renamed;

            NewPetModelName = "";
            PetModelStatusText = $"已重命名为“{renamed.Name}”，文件同步改名。";
        }
        catch (Exception ex)
        {
            PetModelStatusText = $"重命名失败：{ex.Message}";
        }
    }

    private void OpenModelsFolder()
    {
        try
        {
            string dir = PetModelStore.ModelsDirectory(TrackerSettings.DefaultFolder);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PetModelStatusText = $"打开文件夹失败：{ex.Message}";
        }
    }

    // ---------- 背景相关 ----------

    public void SyncBackgrounds()
    {
        try
        {
            IReadOnlyList<BackgroundImage> synced = BackgroundImageStore.SyncFromFolder(
                TrackerSettings.DefaultFolder, _settings.BackgroundImages);

            bool changed = synced.Count != BackgroundChoices.Count ||
                synced.Zip(BackgroundChoices).Any(pair =>
                    pair.First.Id != pair.Second.Id ||
                    !string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) ||
                    !string.Equals(pair.First.FileName, pair.Second.FileName, StringComparison.Ordinal));

            if (!changed) return;

            _settings.BackgroundImages = synced
                .Where(b => BackgroundImage.IsImportedId(b.Id))
                .ToList();
            try { _settings.Save(); } catch (Exception) { }

            BackgroundChoices.Clear();
            foreach (BackgroundImage bg in synced) BackgroundChoices.Add(bg);

            // 校正 SelectedBackgroundId
            if (BackgroundChoices.All(b => b.Id != _selectedBackgroundId))
            {
                // 所选背景已被删除 / 不在同步列表里 → 回退：默认背景 → 无背景
                if (BackgroundChoices.Any(b => b.Id == BackgroundImage.DefaultId))
                    _selectedBackgroundId = BackgroundImage.DefaultId;
                else
                    _selectedBackgroundId = BackgroundImage.NoneId;
                RaisePropertyChanged(nameof(SelectedBackgroundId));
            }
        }
        catch (Exception ex)
        {
            BackgroundStatusText = $"同步背景文件夹失败：{ex.Message}";
        }
    }

    private void ImportBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入背景图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            BackgroundImage bg = BackgroundImageStore.Import(dialog.FileName, TrackerSettings.DefaultFolder);
            _settings.BackgroundImages.Add(bg);
            try
            {
                _settings.Save();
            }
            catch
            {
                _settings.BackgroundImages.RemoveAll(item => item.Id == bg.Id);
                try { BackgroundImageStore.Delete(TrackerSettings.DefaultFolder, bg); } catch { }
                throw;
            }
            SyncBackgrounds();
            SelectedBackgroundId = bg.Id;
            BackgroundStatusText = $"已导入“{bg.Name}”，点击下方“保存设置”应用。";
        }
        catch (Exception ex)
        {
            BackgroundStatusText = $"导入失败：{ex.Message}";
        }
    }

    private void RenameBackground()
    {
        BackgroundImage? bg = BackgroundChoices.FirstOrDefault(item => item.Id == SelectedBackgroundId);
        if (bg is null || bg.Id == BackgroundImage.DefaultId || bg.Id == BackgroundImage.NoneId)
        {
            BackgroundStatusText = "默认背景和无背景选项不可重命名。";
            return;
        }

        string newName = NewBackgroundName?.Trim() ?? "";
        if (newName.Length == 0)
        {
            BackgroundStatusText = "请先在输入框中填写新名称。";
            return;
        }

        try
        {
            BackgroundImage renamed = BackgroundImageStore.Rename(TrackerSettings.DefaultFolder, bg, newName);
            int index = _settings.BackgroundImages.FindIndex(b => b.Id == renamed.Id);
            if (index >= 0) _settings.BackgroundImages[index] = renamed; else _settings.BackgroundImages.Add(renamed);
            _settings.Save();
            SyncBackgrounds();
            NewBackgroundName = "";
            BackgroundStatusText = $"已重命名为“{renamed.Name}”。";
        }
        catch (Exception ex)
        {
            BackgroundStatusText = $"重命名失败：{ex.Message}";
        }
    }

    private void DeleteBackground()
    {
        BackgroundImage? bg = BackgroundChoices.FirstOrDefault(item => item.Id == SelectedBackgroundId);
        if (bg is null || bg.Id == BackgroundImage.DefaultId || bg.Id == BackgroundImage.NoneId)
        {
            BackgroundStatusText = "默认背景和无背景选项不可删除。";
            return;
        }

        var answer = MessageBox.Show(
            $"确定要删除背景“{bg.Name}”吗？此操作不可撤销。",
            "删除背景", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            BackgroundImageStore.Delete(TrackerSettings.DefaultFolder, bg);
            _settings.BackgroundImages.RemoveAll(item => item.Id == bg.Id);
            _settings.Save();
            SyncBackgrounds();
            // 选下一个可用的
            SelectedBackgroundId = BackgroundImage.DefaultId;
            BackgroundStatusText = $"已删除“{bg.Name}”。";
        }
        catch (Exception ex)
        {
            BackgroundStatusText = $"删除失败：{ex.Message}";
        }
    }

    // ---------- 内部导航 ----------

    private void NavigateToSettings(string? hint)
    {
        SelectedTabIndex = 5; // "设置" Tab
    }

    // ---------- 打开背景文件夹 ----------

    private void OpenBackgroundFolder()
    {
        try
        {
            string dir = BackgroundImageStore.BackgroundsDirectory(TrackerSettings.DefaultFolder);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BackgroundStatusText = $"打开文件夹失败：{ex.Message}";
        }
    }

    private void SaveSettings()
    {
        if (IsImportingPetModel) return;
        SyncPetModels(); // 保存前先扫描模型文件夹（可能刚放入或改名了文件）
        SyncBackgrounds(); // 背景文件夹同理
        try
        {
            PetModel model = PetModelChoices.FirstOrDefault(item => item.Id == SelectedPetModelId) ?? PetModel.Default;
            if (model.Id != _settings.SelectedPetModelId &&
                !File.Exists(Path.Combine(TrackerSettings.DefaultFolder, model.RelativePath)))
            {
                PetModelStatusText = "所选模型文件不存在，请重新导入或选择其他人物。";
                SettingsStatusText = "保存失败：所选模型文件不存在。";
                return;
            }
            _settings.SelectedPetModelId = model.Id;
            _settings.IdleThresholdSeconds = IdleThresholdSeconds;
            _settings.PollIntervalMs = PollIntervalMs;
            _settings.TrackWebPages = TrackWebPages;
            _settings.StartMinimized = StartMinimized;
            _settings.AutoStartWithWindows = AutoStartWithWindows;
            _settings.AutoStartConfigured = true;
            _settings.BreakReminderMinutes = BreakReminderMinutes;
            _settings.DailyGoalMinutes = DailyGoalMinutes;
            _settings.WaterReminderEnabled = WaterReminderEnabled;
            _settings.WaterReminderHours = WaterReminderHoursText;
            _settings.ShowPet = ShowPet;
            _settings.PetClickLines = PetClickLinesText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).ToList();
            _settings.PetMenuLines = PetMenuLinesText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).ToList();
            _settings.IgnoredProcesses = IgnoredProcessesText
                .Split([',', '，', ';', '；', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            _settings.CategoryRules = ParseCategoryRules(CategoryRulesText);

            // 背景：NoneId 不需要文件，DefaultId 对应 anime_bg.png，ImportedId 对应用户导入的
            BackgroundImage bg = BackgroundChoices.FirstOrDefault(item => item.Id == SelectedBackgroundId) ?? BackgroundImage.Default;
            _settings.BackgroundImages = BackgroundChoices
                .Where(b => BackgroundImage.IsImportedId(b.Id)) // 只存导入的，内置两个选项不进 JSON
                .ToList();
            _settings.SelectedBackgroundId = bg.Id;
            _settings.BackgroundMode = (int)SelectedBackgroundMode;
            _settings.BackgroundOpacity = BackgroundOpacity;
            _settings.ThemeName = Settings.ThemeName;

            _settings.Save();
            _settings = TrackerSettings.Load();
            _tracker.ApplySettings(_settings);
            PetClickLinesText = string.Join(Environment.NewLine, _settings.PetClickLines);
            PetMenuLinesText = string.Join(Environment.NewLine, _settings.PetMenuLines);
            SelectedPetModelId = _settings.SelectedPetModelId;
            SelectedBackgroundId = _settings.SelectedBackgroundId;
            SelectedBackgroundMode = (BackgroundMode)_settings.BackgroundMode;
            BackgroundOpacity = _settings.BackgroundOpacity;
            BackgroundOpacityText = BackgroundOpacity.ToString("0.##");
            PetModelStatusText = ShowPet ? "人物选择已保存。" : "人物选择已保存，显示桌宠时生效。";
            SettingsSaved?.Invoke(_settings);
            BackgroundChanged?.Invoke();
            FlushIntervalText = $"数据每 {_settings.FlushIntervalSeconds} 秒落盘一次 · 关闭窗口不会停止记录";

            AutoStartService.Set(AutoStartWithWindows);
            _autoStart = AutoStartService.IsEnabled();
            RaisePropertyChanged(nameof(AutoStartWithWindows));

            SettingsStatusText = $"已保存 · {DateTime.Now:HH:mm:ss}";
            _ = RefreshDataAsync(true);
        }
        catch (Exception ex)
        {
            SettingsStatusText = $"保存失败：{ex.Message}";
        }
    }

    private static Dictionary<string, string> ParseCategoryRules(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            string key = trimmed[..eq].Trim().ToLowerInvariant();
            string val = trimmed[(eq + 1)..].Trim();
            if (key.Length > 0 && val.Length > 0) map[key] = val;
        }
        return map;
    }

    private void OpenDatabaseFolder()
    {
        try
        {
            if (File.Exists(_db.DatabasePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_db.DatabasePath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(_db.DatabasePath)!) { UseShellExecute = true });
        }
        catch (Exception ex) { SettingsStatusText = $"打开目录失败：{ex.Message}"; }
    }

    private void ChangeDatabasePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择数据库保存位置（重启后生效）",
            Filter = "SQLite 数据库 (*.db)|*.db|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(_db.DatabasePath),
            InitialDirectory = Path.GetDirectoryName(_db.DatabasePath),
            AddExtension = true,
            DefaultExt = ".db",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _settings.DatabasePath = dialog.FileName;
            _settings.Save();
            SettingsStatusText = "已保存新位置，重启程序后生效。";
            MessageBox.Show(
                $"数据库位置已改为：\n{dialog.FileName}\n\n重启程序后生效。当前记录仍写入原位置。",
                "数据库位置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { SettingsStatusText = $"保存失败：{ex.Message}"; }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return;
        // Only web/internal browser schemes: a stored string must never launch an executable.
        if (uri.Scheme is not ("http" or "https" or "chrome" or "edge" or "about" or "chrome-extension")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private void Vacuum()
    {
        try
        {
            _db.Vacuum();
            SettingsStatusText = "已压缩数据库。";
            _ = RefreshDataAsync(true);
        }
        catch (Exception ex) { SettingsStatusText = $"压缩失败：{ex.Message}"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}