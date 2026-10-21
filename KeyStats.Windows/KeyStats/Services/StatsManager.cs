using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Timers;
using KeyStats.Models;
using Timer = System.Timers.Timer;

namespace KeyStats.Services;

public class StatsManager : IDisposable
{
    public readonly struct CurrentStatsSnapshot
    {
        public CurrentStatsSnapshot(DailyStats stats)
        {
            KeyPresses = stats.KeyPresses;
            TotalClicks = stats.TotalClicks;
            LeftClicks = stats.LeftClicks;
            RightClicks = stats.RightClicks;
            MiddleClicks = stats.MiddleClicks;
            MouseDistance = stats.MouseDistance;
            ScrollDistance = stats.ScrollDistance;
            PeakKPS = stats.PeakKPS;
            PeakCPS = stats.PeakCPS;
        }

        public int KeyPresses { get; }
        public int TotalClicks { get; }
        public int LeftClicks { get; }
        public int RightClicks { get; }
        public int MiddleClicks { get; }
        public double MouseDistance { get; }
        public double ScrollDistance { get; }
        public double PeakKPS { get; }
        public double PeakCPS { get; }
    }

    public enum StatsUpdateKind
    {
        Full,
        MouseDistanceOnly
    }

    private static StatsManager? _instance;
    public static StatsManager Instance => _instance ??= new StatsManager();

    private const double DefaultMetersPerPixel = AppSettings.DefaultMouseMetersPerPixel;

    private readonly string _dataFolder;
    private readonly string _statsFilePath;
    private readonly string _historyFilePath;
    private readonly string _settingsFilePath;

    private readonly object _lock = new();
    private Timer? _saveTimer;
    private Timer? _midnightTimer;
    private Timer? _statsUpdateTimer;
    private Timer? _mouseMoveUpdateTimer;
    private Timer? _settingsSaveTimer;

    private readonly double _saveInterval = 2000; // 2 seconds
    private readonly double _statsUpdateDebounceInterval = 300; // 0.3 seconds
    private readonly double _mouseMoveIdleUpdateInterval = 350; // 0.35 seconds
    private readonly double _settingsSaveInterval = 500; // 0.5 seconds — collapses per-keystroke text-changed bursts
    private const int MaxMissingDayBackfillDays = 31;
    private bool _pendingSave;
    private bool _pendingStatsUpdate;
    private bool _pendingMouseMoveUpdate;
    private bool _pendingSettingsSave;
    private volatile bool _isDisposed;
    private volatile bool _shutdownFlushCompleted;
    private DisplayStatsAggregator? _displayStatsAggregator;
    private Func<bool>? _isSyncEnabledProvider;
    private Func<bool>? _isImportBlockedProvider;

    // Save gate (created lazily, see SaveGate) and the history aggregate cache.
    // All cache fields are read/written under _lock only.
    private object? _saveGate;
    private long _historyRevision;
    private DisplayRemoteSnapshot? _remoteSnapshot;
    private long _remoteSnapshotRevision = -1;
    private DateTime _historyMinDay;
    private long _historyMinDayRevision = -1;
    private DateTime _historyMinDayToday;
    private DateTime _historyMinDayStatisticsDay;
    private bool _historyMinDayValid;
    private DateTime _heatmapStart;
    private long _heatmapStartRevision = -1;
    private DateTime _heatmapStartToday;
    private DateTime _heatmapStartStatisticsDay;
    private bool _heatmapStartSyncEnabled;
    private bool _heatmapStartValid;
    private HistoryAggregateEntry<(double PeakKPS, double PeakCPS)>? _peakCache;
    private Dictionary<KeyHistoryRange, HistoryAggregateEntry<Dictionary<string, int>>>? _keyHistoryCache;
    private Dictionary<AppStatsRange, HistoryAggregateEntry<Dictionary<string, AppStats>>>? _appSummaryCache;
    private Dictionary<KeyboardHeatmapRange, HistoryAggregateEntry<HeatmapTotals>>? _heatmapCache;
    private Dictionary<(HistoryRange Range, HistoryMetric Metric), HistoryAggregateEntry<TrendSeriesTotals>>? _trendCache;
    private Dictionary<MouseTotalsRange, HistoryAggregateEntry<InputTotals>>? _inputTotalsCache;

    // KPS/CPS peak tracking (1-second sliding window)
    private readonly Queue<DateTime> _recentKeyTimestamps = new();
    private readonly Queue<DateTime> _recentClickTimestamps = new();

    private int _lastNotifiedKeyPresses;
    private int _lastNotifiedClicks;

    public DailyStats CurrentStats { get; private set; }
    public AppSettings Settings { get; private set; }
    public Dictionary<string, DailyStats> History { get; private set; } = new();

    public event Action? StatsUpdateRequested;
    public event Action<StatsUpdateKind>? StatsChanged;
    public event Action? LocalStatsReset;

    private StatsManager()
    {
        _dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyStats");
        Directory.CreateDirectory(_dataFolder);

        _statsFilePath = Path.Combine(_dataFolder, "daily_stats.json");
        _historyFilePath = Path.Combine(_dataFolder, "history.json");
        _settingsFilePath = Path.Combine(_dataFolder, "settings.json");

        Settings = LoadSettings();
        History = LoadHistory();
        CurrentStats = LoadStats() ?? new DailyStats();

        if (CurrentStats.Date.Date != DateTime.Today)
        {
            SynchronizeCurrentDay(DateTime.Today, notifyStatsUpdate: false);
        }
        else
        {
            UpdateNotificationBaselines();
            SaveStats();
        }

        SetupMidnightReset();
        SetupInputMonitor();
    }

    private void SetupInputMonitor()
    {
        var monitor = InputMonitorService.Instance;
        monitor.KeyPressed += OnKeyPressed;
        monitor.LeftMouseClicked += OnLeftClick;
        monitor.RightMouseClicked += OnRightClick;
        monitor.MiddleMouseClicked += OnMiddleClick;
        monitor.SideBackMouseClicked += OnSideBackClick;
        monitor.SideForwardMouseClicked += OnSideForwardClick;
        monitor.MouseMoved += OnMouseMoved;
        monitor.MouseScrolled += OnMouseScrolled;
    }

    private void TeardownInputMonitor()
    {
        var monitor = InputMonitorService.Instance;
        monitor.KeyPressed -= OnKeyPressed;
        monitor.LeftMouseClicked -= OnLeftClick;
        monitor.RightMouseClicked -= OnRightClick;
        monitor.MiddleMouseClicked -= OnMiddleClick;
        monitor.SideBackMouseClicked -= OnSideBackClick;
        monitor.SideForwardMouseClicked -= OnSideForwardClick;
        monitor.MouseMoved -= OnMouseMoved;
        monitor.MouseScrolled -= OnMouseScrolled;
    }

    private void UpdateAppStats(string appName, string displayName, Action<AppStats> updateAction)
    {
        if (string.IsNullOrWhiteSpace(appName)) return;

        var normalizedAppName = appName.Trim();
        var normalizedDisplayName = NormalizeDisplayName(normalizedAppName, displayName);

        if (!CurrentStats.AppStats.TryGetValue(normalizedAppName, out var appStats))
        {
            appStats = new AppStats(normalizedAppName, normalizedDisplayName);
            CurrentStats.AppStats[normalizedAppName] = appStats;
        }
        else if (ShouldUpdateDisplayName(appStats, normalizedDisplayName))
        {
            appStats.DisplayName = normalizedDisplayName;
        }

        updateAction(appStats);
    }

    private static string NormalizeDisplayName(string appName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return appName;
        }

        var normalizedDisplay = displayName.Trim();
        return string.Equals(normalizedDisplay, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? appName
            : normalizedDisplay;
    }

    private static bool ShouldUpdateDisplayName(AppStats appStats, string incomingDisplayName)
    {
        if (string.IsNullOrWhiteSpace(incomingDisplayName))
        {
            return false;
        }

        if (string.Equals(appStats.DisplayName, incomingDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(appStats.DisplayName))
        {
            return true;
        }

        // Prefer richer labels over raw process names (e.g. javaw -> Minecraft).
        return string.Equals(appStats.DisplayName, appStats.AppName, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(incomingDisplayName, appStats.AppName, StringComparison.OrdinalIgnoreCase);
    }

    private void OnKeyPressed(string keyName, string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.KeyPresses++;
            if (!string.IsNullOrEmpty(keyName))
            {
                if (!CurrentStats.KeyPressCounts.ContainsKey(keyName))
                {
                    CurrentStats.KeyPressCounts[keyName] = 0;
                }
                CurrentStats.KeyPressCounts[keyName]++;
            }
            UpdateAppStats(appName, displayName, stats => stats.RecordKeyPress(keyName));
        }

        if (dayChanged) CompleteDayRollover();
        RecordKeyForPeakKPS();
        NotifyStatsUpdate();
        NotifyKeyPressThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnLeftClick(string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.LeftClicks++;
            UpdateAppStats(appName, displayName, stats => stats.RecordLeftClick());
        }

        if (dayChanged) CompleteDayRollover();
        RecordClickForPeakCPS();
        NotifyStatsUpdate();
        NotifyClickThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnRightClick(string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.RightClicks++;
            UpdateAppStats(appName, displayName, stats => stats.RecordRightClick());
        }

        if (dayChanged) CompleteDayRollover();
        RecordClickForPeakCPS();
        NotifyStatsUpdate();
        NotifyClickThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnMiddleClick(string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.MiddleClicks++;
            UpdateAppStats(appName, displayName, stats => stats.RecordMiddleClick());
        }

        if (dayChanged) CompleteDayRollover();
        RecordClickForPeakCPS();
        NotifyStatsUpdate();
        NotifyClickThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnSideBackClick(string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.SideBackClicks++;
            UpdateAppStats(appName, displayName, stats => stats.RecordSideBackClick());
        }

        if (dayChanged) CompleteDayRollover();
        RecordClickForPeakCPS();
        NotifyStatsUpdate();
        NotifyClickThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnSideForwardClick(string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.SideForwardClicks++;
            UpdateAppStats(appName, displayName, stats => stats.RecordSideForwardClick());
        }

        if (dayChanged) CompleteDayRollover();
        RecordClickForPeakCPS();
        NotifyStatsUpdate();
        NotifyClickThresholdIfNeeded();
        ScheduleSave();
    }

    private void OnMouseMoved(double distance)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.MouseDistance += distance;
        }

        if (dayChanged) CompleteDayRollover();
        ScheduleMouseMoveIdleUpdate();
        ScheduleSave();
    }

    private void OnMouseScrolled(double distance, string appName, string displayName)
    {
        bool dayChanged;
        lock (_lock)
        {
            if (_isDisposed) return;

            dayChanged = EnsureCurrentDayLocked();
            CurrentStats.ScrollDistance += Math.Abs(distance);
            UpdateAppStats(appName, displayName, stats => stats.AddScrollDistance(Math.Abs(distance)));
        }

        if (dayChanged) CompleteDayRollover();
        ScheduleDebouncedStatsUpdate();
        ScheduleSave();
    }

    /// <summary>
    /// Rolls the counters over to today when they are held with <see cref="_lock"/> already taken.
    /// Callers must persist/notify through <see cref="CompleteDayRollover"/> after releasing the lock
    /// so a save never waits for the gate while the statistics lock is held.
    /// </summary>
    private bool EnsureCurrentDayLocked()
    {
        return RolloverToDateLocked(DateTime.Today);
    }

    private void CompleteDayRollover()
    {
        NotifyStatsUpdate();
        SaveStats();
        // Persist History too — past-day data just changed (today's counters were
        // archived and possibly missing days were back-filled).
        SaveHistory();
    }

    private void RecordKeyForPeakKPS()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-1.0);
        lock (_lock)
        {
            _recentKeyTimestamps.Enqueue(now);
            while (_recentKeyTimestamps.Count > 0 && _recentKeyTimestamps.Peek() <= cutoff)
            {
                _recentKeyTimestamps.Dequeue();
            }
            var currentKPS = (double)_recentKeyTimestamps.Count;
            if (currentKPS > CurrentStats.PeakKPS)
            {
                CurrentStats.PeakKPS = currentKPS;
            }
        }
    }

    private void RecordClickForPeakCPS()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-1.0);
        lock (_lock)
        {
            _recentClickTimestamps.Enqueue(now);
            while (_recentClickTimestamps.Count > 0 && _recentClickTimestamps.Peek() <= cutoff)
            {
                _recentClickTimestamps.Dequeue();
            }
            var currentCPS = (double)_recentClickTimestamps.Count;
            if (currentCPS > CurrentStats.PeakCPS)
            {
                CurrentStats.PeakCPS = currentCPS;
            }
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            // Keep the first deadline so continuous input cannot postpone saving.
            if (_pendingSave) return;

            _pendingSave = true;

            if (_saveTimer == null)
            {
                _saveTimer = new Timer(_saveInterval)
                {
                    AutoReset = false
                };
                _saveTimer.Elapsed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (!_pendingSave)
                        {
                            return;
                        }

                        _pendingSave = false;
                    }

                    SaveStats();
                };
            }

            var saveTimer = _saveTimer;
            if (saveTimer == null)
            {
                return;
            }

            RestartTimer(saveTimer);
        }
    }

    private void ScheduleDebouncedStatsUpdate()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            if (_pendingStatsUpdate) return;
            _pendingStatsUpdate = true;

            if (_statsUpdateTimer == null)
            {
                _statsUpdateTimer = new Timer(_statsUpdateDebounceInterval)
                {
                    AutoReset = false
                };
                _statsUpdateTimer.Elapsed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed || !_pendingStatsUpdate)
                        {
                            return;
                        }

                        _pendingStatsUpdate = false;
                    }

                    NotifyStatsUpdate();
                };
            }

            RestartTimer(_statsUpdateTimer);
        }
    }

    private void ScheduleMouseMoveIdleUpdate()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            _pendingMouseMoveUpdate = true;

            if (_mouseMoveUpdateTimer == null)
            {
                _mouseMoveUpdateTimer = new Timer(_mouseMoveIdleUpdateInterval)
                {
                    AutoReset = false
                };
                _mouseMoveUpdateTimer.Elapsed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed || !_pendingMouseMoveUpdate)
                        {
                            return;
                        }

                        _pendingMouseMoveUpdate = false;
                    }

                    NotifyStatsUpdate(StatsUpdateKind.MouseDistanceOnly);
                };
            }

            RestartTimer(_mouseMoveUpdateTimer);
        }
    }

    private static void RestartTimer(Timer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private static void StopTimer(Timer? timer)
    {
        timer?.Stop();
    }

    private static void DisposeTimer(ref Timer? timer)
    {
        var timerToDispose = timer;
        timer = null;
        timerToDispose?.Dispose();
    }

    private void StopActivityTimers()
    {
        lock (_lock)
        {
            StopTimer(_saveTimer);
            StopTimer(_statsUpdateTimer);
            StopTimer(_mouseMoveUpdateTimer);
            StopTimer(_settingsSaveTimer);
        }
    }

    private void DisposeActivityTimers()
    {
        lock (_lock)
        {
            DisposeTimer(ref _saveTimer);
            DisposeTimer(ref _statsUpdateTimer);
            DisposeTimer(ref _mouseMoveUpdateTimer);
            DisposeTimer(ref _settingsSaveTimer);
        }
    }

    private void StopMidnightTimer()
    {
        lock (_midnightTimerLock)
        {
            StopTimer(_midnightTimer);
        }
    }

    private void DisposeMidnightTimer()
    {
        lock (_midnightTimerLock)
        {
            DisposeTimer(ref _midnightTimer);
        }
    }

    private bool BeginDispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return false;
            }

            _isDisposed = true;
            _pendingSave = false;
            _pendingStatsUpdate = false;
            _pendingMouseMoveUpdate = false;
            _pendingSettingsSave = false;
            return true;
        }
    }

    private void NotifyStatsUpdate(StatsUpdateKind kind = StatsUpdateKind.Full)
    {
        StatsChanged?.Invoke(kind);

        if (kind == StatsUpdateKind.Full)
        {
            StatsUpdateRequested?.Invoke();
        }
    }

    public void ConfigureSyncDisplay(
        DisplayStatsAggregator aggregator,
        Func<bool> isSyncEnabledProvider,
        Func<bool>? isImportBlockedProvider = null)
    {
        lock (_lock)
        {
            _displayStatsAggregator = aggregator;
            _isSyncEnabledProvider = isSyncEnabledProvider;
            _isImportBlockedProvider = isImportBlockedProvider ?? isSyncEnabledProvider;
            InvalidateHistoryAggregatesLocked();
        }

        NotifyStatsUpdate();
    }

    public void NotifyRemoteStatsChanged()
    {
        lock (_lock)
        {
            InvalidateHistoryAggregatesLocked();
        }

        NotifyStatsUpdate();
    }

    #region Persistence

    /// <summary>
    /// Single save gate. Acquired before the statistics lock so serialization and disk
    /// writes never block counters; the snapshot itself is still taken under <see cref="_lock"/>.
    /// The gate is created lazily so tests that build instances through
    /// <see cref="System.Runtime.Serialization.FormatterServices"/> keep working.
    /// </summary>
    private object SaveGate
    {
        get
        {
            var gate = _saveGate;
            if (gate != null)
            {
                return gate;
            }

            Interlocked.CompareExchange(ref _saveGate, new object(), null);
            return _saveGate!;
        }
    }

    private void SaveStats()
    {
        // Fast path without the gate; re-checked under the gate because a save that passed the
        // check can still be queued behind the shutdown flush.
        if (_shutdownFlushCompleted) return;

        lock (SaveGate)
        {
            if (_shutdownFlushCompleted) return;
            WriteStatsSnapshot();
        }
    }

    private void WriteStatsSnapshot()
    {
        DailyStats statsSnapshot;
        lock (_lock)
        {
            // Mirror today's counters into the in-memory History dict so query paths
            // that read from History stay in sync. The history *file* is no longer
            // re-written here — past-day data only changes at day rollover / import /
            // reset / shutdown, so writing it every 2s is wasted I/O.
            RecordCurrentStatsToHistory();
            statsSnapshot = CloneDailyStats(CurrentStats, CurrentStats.Date.Date);
        }

        // Serialize and replace outside the statistics lock: older snapshots cannot win
        // because the whole operation is serialized by the save gate.
        WriteJsonDurable(_statsFilePath, statsSnapshot, "stats");
    }

    private void SaveHistory()
    {
        if (_shutdownFlushCompleted) return;

        lock (SaveGate)
        {
            if (_shutdownFlushCompleted) return;
            WriteHistorySnapshot();
        }
    }

    private void WriteHistorySnapshot()
    {
        Dictionary<string, DailyStats> snapshot;
        lock (_lock)
        {
            snapshot = CloneHistorySnapshot(History);
        }

        WriteJsonDurable(_historyFilePath, snapshot, "history");
    }

    private DailyStats? LoadStats()
    {
        var primary = TryDeserialize<DailyStats>(_statsFilePath);
        if (primary != null) return primary;

        var backup = TryDeserialize<DailyStats>(_statsFilePath + ".bak");
        if (backup != null)
        {
            System.Diagnostics.Debug.WriteLine("Recovered daily_stats from .bak");
            return backup;
        }

        // Last resort: today's entry in already-loaded History.
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        if (History.TryGetValue(todayKey, out var fromHistory))
        {
            System.Diagnostics.Debug.WriteLine("Recovered daily_stats from history.json");
            // Force date to today: this branch only fires when history has a today-keyed
            // entry, so a corrupt Date field shouldn't cause SynchronizeCurrentDay to
            // discard the recovered counts on startup.
            return CloneDailyStats(fromHistory, DateTime.Today);
        }

        return null;
    }

    private void RecordCurrentStatsToHistory()
    {
        var key = CurrentStats.Date.ToString("yyyy-MM-dd");
        // Clone CurrentStats so callers cannot mutate the snapshot stored in History.
        var statsCopy = new DailyStats(CurrentStats.Date)
        {
            KeyPresses = CurrentStats.KeyPresses,
            LeftClicks = CurrentStats.LeftClicks,
            RightClicks = CurrentStats.RightClicks,
            MiddleClicks = CurrentStats.MiddleClicks,
            SideBackClicks = CurrentStats.SideBackClicks,
            SideForwardClicks = CurrentStats.SideForwardClicks,
            MouseDistance = CurrentStats.MouseDistance,
            ScrollDistance = CurrentStats.ScrollDistance,
            PeakKPS = CurrentStats.PeakKPS,
            PeakCPS = CurrentStats.PeakCPS,
            KeyPressCounts = new Dictionary<string, int>(CurrentStats.KeyPressCounts),
            AppStats = CurrentStats.AppStats.ToDictionary(k => k.Key, v => new AppStats(v.Value))
        };
        History[key] = statsCopy;
    }

    private Dictionary<string, DailyStats> CloneHistorySnapshot(Dictionary<string, DailyStats> source)
    {
        return source.ToDictionary(
            kvp => kvp.Key,
            kvp => CloneDailyStats(kvp.Value, kvp.Value.Date.Date));
    }


    private Dictionary<string, DailyStats> LoadHistory()
    {
        var primary = TryDeserialize<Dictionary<string, DailyStats>>(_historyFilePath);
        if (primary != null) return primary;

        var backup = TryDeserialize<Dictionary<string, DailyStats>>(_historyFilePath + ".bak");
        if (backup != null)
        {
            System.Diagnostics.Debug.WriteLine("Recovered history from .bak");
            return backup;
        }

        return new();
    }

    public void SaveSettings()
    {
        // UI call sites can fire per-keystroke (e.g. NotificationSettingsWindow's
        // OnThresholdTextChanged), so debounce the durable write — otherwise every
        // keystroke would block the UI thread on a synchronous FlushFileBuffers.
        lock (_lock)
        {
            if (_isDisposed) return;

            _pendingSettingsSave = true;

            if (_settingsSaveTimer == null)
            {
                _settingsSaveTimer = new Timer(_settingsSaveInterval) { AutoReset = false };
                _settingsSaveTimer.Elapsed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (!_pendingSettingsSave) return;
                        _pendingSettingsSave = false;
                    }

                    FlushSettings();
                };
            }

            RestartTimer(_settingsSaveTimer);
        }
    }

    private void FlushSettings()
    {
        if (_shutdownFlushCompleted) return;

        lock (SaveGate)
        {
            if (_shutdownFlushCompleted) return;
            WriteSettingsSnapshot();
        }
    }

    private void WriteSettingsSnapshot()
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = Settings.Snapshot();
        }

        WriteJsonDurable(_settingsFilePath, snapshot, "settings");
    }

    private AppSettings LoadSettings()
    {
        return TryDeserialize<AppSettings>(_settingsFilePath)
            ?? TryDeserialize<AppSettings>(_settingsFilePath + ".bak")
            ?? new AppSettings();
    }

    private static void WriteJsonDurable<T>(string targetPath, T value, string contextLabel)
    {
        var tempPath = targetPath + ".tmp";
        var backupPath = targetPath + ".bak";

        try
        {
            // Serialize inside the try/catch — System.Text.Json throws on NaN/Infinity
            // by default, and an autosave-thread escape would crash the app.
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);

            // WriteThrough + Flush(true) ensures FlushFileBuffers is issued, so the
            // tmp file's data blocks are durable before we swap it in. Otherwise a
            // power loss right after Replace can leave a 0-byte / truncated target.
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, backupPath);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving {contextLabel}: {ex.Message}");
        }
    }

    private static T? TryDeserialize<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading {path}: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Export

    public enum ImportMode
    {
        Overwrite,
        Merge
    }

    public byte[] ExportStatsData()
    {
        ExportPayload payload;
        lock (_lock)
        {
            var normalizedDate = CurrentStats.Date.Date;
            var currentCopy = CloneDailyStats(CurrentStats, normalizedDate);
            var exportHistory = new Dictionary<string, DailyStats>(History.Count + 1);

            foreach (var kvp in History)
            {
                exportHistory[kvp.Key] = CloneDailyStats(kvp.Value, kvp.Value.Date.Date);
            }

            var key = normalizedDate.ToString("yyyy-MM-dd");
            // Ensure current stats are included (overwrite today's history entry if present).
            exportHistory[key] = currentCopy;

            payload = new ExportPayload
            {
                Version = 1,
                Scope = "currentDevice",
                ExportedAt = DateTime.UtcNow,
                CurrentStats = currentCopy,
                History = exportHistory
            };
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(payload, options);
        return Encoding.UTF8.GetBytes(json);
    }

    public void ImportStatsData(byte[] data)
    {
        ImportStatsData(data, ImportMode.Overwrite);
    }

    public void ImportStatsData(byte[] data, ImportMode mode)
    {
        if (_isImportBlockedProvider?.Invoke() == true)
        {
            throw new InvalidOperationException(KeyStats.Properties.Strings.Error_ImportDisabledWhileSyncing);
        }

        if (data == null || data.Length == 0)
        {
            throw new InvalidDataException(KeyStats.Properties.Strings.Error_ImportEmpty);
        }

        var deserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        ExportPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ExportPayload>(data, deserializeOptions)
                ?? throw new InvalidDataException(KeyStats.Properties.Strings.Error_ImportInvalidFormat);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(KeyStats.Properties.Strings.Error_ImportInvalidFormat, ex);
        }

        if (payload.Version != 1)
        {
            throw new InvalidDataException(KeyStats.Properties.Strings.Error_ImportUnsupportedVersion);
        }
        if (!string.IsNullOrWhiteSpace(payload.Scope) &&
            !string.Equals(payload.Scope, "currentDevice", StringComparison.Ordinal))
        {
            throw new InvalidDataException(KeyStats.Properties.Strings.Error_ImportInvalidFormat);
        }

        lock (_lock)
        {
            _saveTimer?.Stop();
            _pendingSave = false;
            _statsUpdateTimer?.Stop();
            _pendingStatsUpdate = false;
            _mouseMoveUpdateTimer?.Stop();
            _pendingMouseMoveUpdate = false;

            var importedHistory = NormalizeHistory(payload.History);
            var importedCurrent = NormalizeDailyStats(payload.CurrentStats, DateTime.Today);
            importedHistory[importedCurrent.Date.ToString("yyyy-MM-dd")] = CloneDailyStats(importedCurrent, importedCurrent.Date);

            var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
            if (mode == ImportMode.Merge)
            {
                var mergedHistory = CloneHistorySnapshot(History);
                var currentSnapshot = CloneDailyStats(CurrentStats, CurrentStats.Date.Date);
                mergedHistory[currentSnapshot.Date.ToString("yyyy-MM-dd")] = currentSnapshot;

                foreach (var kvp in importedHistory)
                {
                    if (mergedHistory.TryGetValue(kvp.Key, out var existing))
                    {
                        mergedHistory[kvp.Key] = MergeDailyStats(existing, kvp.Value);
                    }
                    else
                    {
                        mergedHistory[kvp.Key] = CloneDailyStats(kvp.Value, kvp.Value.Date.Date);
                    }
                }

                History = mergedHistory;
            }
            else
            {
                History = importedHistory;
            }

            CurrentStats = History.TryGetValue(todayKey, out var todayStats)
                ? CloneDailyStats(todayStats, todayStats.Date.Date)
                : new DailyStats(DateTime.Today);

            UpdateNotificationBaselines();
            InvalidateHistoryAggregatesLocked();
        }

        SaveStats();
        SaveHistory();
        NotifyStatsUpdate();
    }

    private static DailyStats CloneDailyStats(DailyStats source, DateTime dateOverride)
    {
        var normalizedDate = dateOverride.Date;
        return new DailyStats(normalizedDate)
        {
            KeyPresses = source.KeyPresses,
            LeftClicks = source.LeftClicks,
            RightClicks = source.RightClicks,
            MiddleClicks = source.MiddleClicks,
            SideBackClicks = source.SideBackClicks,
            SideForwardClicks = source.SideForwardClicks,
            MouseDistance = source.MouseDistance,
            ScrollDistance = source.ScrollDistance,
            PeakKPS = source.PeakKPS,
            PeakCPS = source.PeakCPS,
            KeyPressCounts = new Dictionary<string, int>(source.KeyPressCounts),
            AppStats = source.AppStats.ToDictionary(k => k.Key, v => new AppStats(v.Value))
        };
    }

    private static Dictionary<string, DailyStats> NormalizeHistory(Dictionary<string, DailyStats>? source)
    {
        var normalized = new Dictionary<string, DailyStats>();
        if (source == null)
        {
            return normalized;
        }

        foreach (var kvp in source)
        {
            var fallbackDate = DateTime.Today;
            if (DateTime.TryParseExact(
                kvp.Key,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
            {
                fallbackDate = parsedDate.Date;
            }

            var daily = NormalizeDailyStats(kvp.Value, fallbackDate);
            normalized[daily.Date.ToString("yyyy-MM-dd")] = daily;
        }

        return normalized;
    }

    private static DailyStats NormalizeDailyStats(DailyStats? source, DateTime fallbackDate)
    {
        var normalizedDate = source?.Date.Date ?? fallbackDate.Date;
        if (normalizedDate == DateTime.MinValue.Date)
        {
            normalizedDate = fallbackDate.Date;
        }

        var keyPressCounts = NormalizeKeyPressCounts(source?.KeyPressCounts);

        var appStats = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        if (source?.AppStats != null)
        {
            foreach (var kvp in source.AppStats)
            {
                var keyName = (kvp.Key ?? string.Empty).Trim();
                var sourceStats = kvp.Value ?? new AppStats();
                var appName = string.IsNullOrWhiteSpace(sourceStats.AppName)
                    ? keyName
                    : sourceStats.AppName.Trim();

                if (string.IsNullOrWhiteSpace(appName))
                {
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(sourceStats.DisplayName)
                    ? appName
                    : sourceStats.DisplayName.Trim();

                appStats[appName] = new AppStats(appName, displayName)
                {
                    KeyPresses = Math.Max(0, sourceStats.KeyPresses),
                    KeyPressCounts = NormalizeKeyPressCounts(sourceStats.KeyPressCounts),
                    LeftClicks = Math.Max(0, sourceStats.LeftClicks),
                    RightClicks = Math.Max(0, sourceStats.RightClicks),
                    MiddleClicks = Math.Max(0, sourceStats.MiddleClicks),
                    SideBackClicks = Math.Max(0, sourceStats.SideBackClicks),
                    SideForwardClicks = Math.Max(0, sourceStats.SideForwardClicks),
                    ScrollDistance = SanitizeDistance(sourceStats.ScrollDistance)
                };
            }
        }

        return new DailyStats(normalizedDate)
        {
            KeyPresses = Math.Max(0, source?.KeyPresses ?? 0),
            KeyPressCounts = keyPressCounts,
            LeftClicks = Math.Max(0, source?.LeftClicks ?? 0),
            RightClicks = Math.Max(0, source?.RightClicks ?? 0),
            MiddleClicks = Math.Max(0, source?.MiddleClicks ?? 0),
            SideBackClicks = Math.Max(0, source?.SideBackClicks ?? 0),
            SideForwardClicks = Math.Max(0, source?.SideForwardClicks ?? 0),
            MouseDistance = SanitizeDistance(source?.MouseDistance ?? 0),
            ScrollDistance = SanitizeDistance(source?.ScrollDistance ?? 0),
            PeakKPS = Math.Max(0, source?.PeakKPS ?? 0),
            PeakCPS = Math.Max(0, source?.PeakCPS ?? 0),
            AppStats = appStats
        };
    }

    private static Dictionary<string, int> NormalizeKeyPressCounts(Dictionary<string, int>? source)
    {
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        if (source == null) return normalized;

        foreach (var kvp in source)
        {
            var key = (kvp.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) || kvp.Value <= 0) continue;

            normalized[key] = SafeAdd(normalized.TryGetValue(key, out var current) ? current : 0, kvp.Value);
        }

        return normalized;
    }

    private static DailyStats MergeDailyStats(DailyStats existing, DailyStats incoming)
    {
        var normalizedExisting = NormalizeDailyStats(existing, existing.Date.Date);
        var normalizedIncoming = NormalizeDailyStats(incoming, normalizedExisting.Date.Date);

        var keyPressCounts = new Dictionary<string, int>(normalizedExisting.KeyPressCounts, StringComparer.Ordinal);
        foreach (var kvp in normalizedIncoming.KeyPressCounts)
        {
            keyPressCounts[kvp.Key] = keyPressCounts.TryGetValue(kvp.Key, out var current)
                ? current + kvp.Value
                : kvp.Value;
        }

        var appStats = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in normalizedExisting.AppStats)
        {
            appStats[kvp.Key] = new AppStats(kvp.Value);
        }

        foreach (var kvp in normalizedIncoming.AppStats)
        {
            if (!appStats.TryGetValue(kvp.Key, out var existingApp))
            {
                appStats[kvp.Key] = new AppStats(kvp.Value);
                continue;
            }

            if (string.IsNullOrWhiteSpace(existingApp.DisplayName) && !string.IsNullOrWhiteSpace(kvp.Value.DisplayName))
            {
                existingApp.DisplayName = kvp.Value.DisplayName;
            }

            existingApp.KeyPresses += kvp.Value.KeyPresses;
            MergeKeyCounts(kvp.Value.KeyPressCounts, existingApp.KeyPressCounts);
            existingApp.LeftClicks += kvp.Value.LeftClicks;
            existingApp.RightClicks += kvp.Value.RightClicks;
            existingApp.MiddleClicks += kvp.Value.MiddleClicks;
            existingApp.SideBackClicks += kvp.Value.SideBackClicks;
            existingApp.SideForwardClicks += kvp.Value.SideForwardClicks;
            existingApp.ScrollDistance += kvp.Value.ScrollDistance;
        }

        return new DailyStats(normalizedExisting.Date.Date)
        {
            KeyPresses = normalizedExisting.KeyPresses + normalizedIncoming.KeyPresses,
            LeftClicks = normalizedExisting.LeftClicks + normalizedIncoming.LeftClicks,
            RightClicks = normalizedExisting.RightClicks + normalizedIncoming.RightClicks,
            MiddleClicks = normalizedExisting.MiddleClicks + normalizedIncoming.MiddleClicks,
            SideBackClicks = normalizedExisting.SideBackClicks + normalizedIncoming.SideBackClicks,
            SideForwardClicks = normalizedExisting.SideForwardClicks + normalizedIncoming.SideForwardClicks,
            MouseDistance = normalizedExisting.MouseDistance + normalizedIncoming.MouseDistance,
            ScrollDistance = normalizedExisting.ScrollDistance + normalizedIncoming.ScrollDistance,
            PeakKPS = Math.Max(normalizedExisting.PeakKPS, normalizedIncoming.PeakKPS),
            PeakCPS = Math.Max(normalizedExisting.PeakCPS, normalizedIncoming.PeakCPS),
            KeyPressCounts = keyPressCounts,
            AppStats = appStats
        };
    }

    private static double SanitizeDistance(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }
        return value;
    }

    private sealed class ExportPayload
    {
        public int Version { get; set; }
        public string? Scope { get; set; }
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public DailyStats CurrentStats { get; set; } = new();
        public Dictionary<string, DailyStats> History { get; set; } = new();
    }

    #endregion

    #region Midnight Reset

    private void SetupMidnightReset()
    {
        ScheduleNextMidnightReset();
    }

    private readonly object _midnightTimerLock = new();

    private void ScheduleNextMidnightReset()
    {
        if (_isDisposed) return;

        lock (_midnightTimerLock)
        {
            if (_isDisposed) return;

            _midnightTimer?.Stop();
            _midnightTimer?.Dispose();

            var now = DateTime.Now;
            var nextMidnight = DateTime.Today.AddDays(1);
            var timeUntilMidnight = nextMidnight - now;

            _midnightTimer = new Timer(timeUntilMidnight.TotalMilliseconds);
            _midnightTimer.Elapsed += (_, _) => PerformMidnightReset();
            _midnightTimer.AutoReset = false;
            _midnightTimer.Start();
        }
    }

    private void PerformMidnightReset()
    {
        SynchronizeCurrentDay(DateTime.Now, notifyStatsUpdate: true);
        ScheduleNextMidnightReset();
    }

    public void HandleSystemResume()
    {
        SynchronizeCurrentDay(DateTime.Now, notifyStatsUpdate: true);
        ScheduleNextMidnightReset();
    }

    public void ResetStats()
    {
        ResetStats(DateTime.Today);
    }

    private void ResetStats(DateTime date)
    {
        lock (_lock)
        {
            // Archive the old counters to History first so the increment since the last save isn't lost.
            RecordCurrentStatsToHistory();

            // Then create a fresh stats object for the new day.
            CurrentStats = new DailyStats(date);
            InvalidateHistoryAggregatesLocked();
        }

        SaveHistory();
        UpdateNotificationBaselines();
        NotifyStatsUpdate();
        SaveStats();
        LocalStatsReset?.Invoke();
    }

    private void SynchronizeCurrentDay(DateTime targetDate, bool notifyStatsUpdate)
    {
        bool changed;
        lock (_lock)
        {
            changed = RolloverToDateLocked(targetDate.Date);
        }

        if (!changed)
        {
            return;
        }

        if (notifyStatsUpdate)
        {
            NotifyStatsUpdate();
        }

        SaveStats();
        // Persist History too — past-day data just changed (today's counters were
        // archived and possibly missing days were back-filled).
        SaveHistory();
    }

    /// <summary>
    /// Applies the cross-day rollover. Must be called with <see cref="_lock"/> held so the
    /// archive-and-reset stays atomic with respect to input handlers; the caller performs
    /// notification and persistence after releasing the lock.
    /// </summary>
    private bool RolloverToDateLocked(DateTime normalizedTargetDate)
    {
        var currentDate = CurrentStats.Date.Date;
        if (currentDate == normalizedTargetDate)
        {
            return false;
        }

        History[currentDate.ToString("yyyy-MM-dd")] = CloneDailyStats(CurrentStats, currentDate);

        if (currentDate < normalizedTargetDate)
        {
            var missingDayCount = (normalizedTargetDate - currentDate).Days - 1;
            if (missingDayCount > 0 && missingDayCount <= MaxMissingDayBackfillDays)
            {
                for (var missingDate = currentDate.AddDays(1); missingDate < normalizedTargetDate; missingDate = missingDate.AddDays(1))
                {
                    var key = missingDate.ToString("yyyy-MM-dd");
                    if (!History.ContainsKey(key))
                    {
                        History[key] = new DailyStats(missingDate);
                    }
                }
            }
        }

        CurrentStats = new DailyStats(normalizedTargetDate);
        _recentKeyTimestamps.Clear();
        _recentClickTimestamps.Clear();
        UpdateNotificationBaselines();
        InvalidateHistoryAggregatesLocked();
        return true;
    }

    #endregion

    #region Notifications

    private void UpdateNotificationBaselines()
    {
        _lastNotifiedKeyPresses = NormalizedBaseline(CurrentStats.KeyPresses, Settings.KeyPressNotifyThreshold);
        _lastNotifiedClicks = NormalizedBaseline(CurrentStats.TotalClicks, Settings.ClickNotifyThreshold);
    }

    private int NormalizedBaseline(int count, int threshold)
    {
        if (threshold <= 0) return 0;
        return (count / threshold) * threshold;
    }

    private void NotifyKeyPressThresholdIfNeeded()
    {
        if (!Settings.NotificationsEnabled) return;
        var threshold = Settings.KeyPressNotifyThreshold;
        if (threshold <= 0) return;
        var count = CurrentStats.KeyPresses;

        // Compute the threshold milestone for the current count (rounded down to the nearest multiple of threshold).
        var currentThreshold = NormalizedBaseline(count, threshold);

        // Fire a notification only when we've crossed into a new milestone since the last one.
        if (currentThreshold > _lastNotifiedKeyPresses)
        {
            _lastNotifiedKeyPresses = currentThreshold;
            NotificationService.Instance.SendThresholdNotification(NotificationService.Metric.KeyPresses, currentThreshold);
        }
    }

    private void NotifyClickThresholdIfNeeded()
    {
        if (!Settings.NotificationsEnabled) return;
        var threshold = Settings.ClickNotifyThreshold;
        if (threshold <= 0) return;
        var count = CurrentStats.TotalClicks;

        // Compute the threshold milestone for the current count (rounded down to the nearest multiple of threshold).
        var currentThreshold = NormalizedBaseline(count, threshold);

        // Fire a notification only when we've crossed into a new milestone since the last one.
        if (currentThreshold > _lastNotifiedClicks)
        {
            _lastNotifiedClicks = currentThreshold;
            NotificationService.Instance.SendThresholdNotification(NotificationService.Metric.Clicks, currentThreshold);
        }
    }

    #endregion

    #region Formatting

    public string FormatNumber(int number)
    {
        // 999,950+ would round to "1000.0k", so switch to M before that.
        if (number >= 999_950)
            return $"{number / 1_000_000.0:F1}M";
        if (number >= 1_000)
            return $"{number / 1_000.0:F1}k";
        return number.ToString("N0");
    }

    /// <summary>
    /// Returns a lock-protected snapshot of today's local statistics for UI projection.
    /// </summary>
    public CurrentStatsSnapshot GetCurrentStatsSnapshot()
    {
        lock (_lock)
        {
            return new CurrentStatsSnapshot(CurrentStats);
        }
    }

    /// <summary>
    /// Returns a detached copy of today's statistics (including per-key and per-app counts)
    /// so background aggregation never reads counters that are being mutated.
    /// </summary>
    public DailyStats GetCurrentStatsCopy()
    {
        lock (_lock)
        {
            return CloneDailyStats(CurrentStats, CurrentStats.Date.Date);
        }
    }

    /// <summary>
    /// Monotonic revision of the immutable history view. It advances on day rollover, import,
    /// reset, remote-data changes and sync display configuration changes, but not on ordinary
    /// counter updates, so background refreshes can discard results computed across a change.
    /// </summary>
    public long HistoryRevision
    {
        get
        {
            lock (_lock)
            {
                return _historyRevision;
            }
        }
    }

    private void InvalidateHistoryAggregatesLocked()
    {
        _historyRevision++;
        _remoteSnapshot = null;
        _remoteSnapshotRevision = -1;
        _historyMinDayValid = false;
        _heatmapStartValid = false;
        _peakCache = null;
        _keyHistoryCache = null;
        _appSummaryCache = null;
        _heatmapCache = null;
        _trendCache = null;
        _inputTotalsCache = null;
    }

    public (double PeakKPS, double PeakCPS) GetAllTimePeakRates()
    {
        var today = DateTime.Today;
        HistoryAggregateStamp stamp;
        (double PeakKPS, double PeakCPS) stablePeaks;
        var stableDays = new List<DailyStats>();
        (double PeakKPS, double PeakCPS) live;
        bool cached;

        // Stamp, live peaks and immutable history references are taken together so an import or
        // rollover cannot bind a newer history to an older stamp.
        lock (_lock)
        {
            stamp = CaptureStampLocked(today);
            cached = TryGetCachedAggregate(_peakCache, stamp, out stablePeaks);
            live = ComputeLivePeaksLocked(today, stamp.StatisticsDay);

            if (!cached)
            {
                foreach (var stats in History.Values)
                {
                    var date = stats.Date.Date;
                    if (date <= today && date < stamp.StatisticsDay)
                    {
                        stableDays.Add(stats);
                    }
                }
            }
        }

        if (!cached)
        {
            double peakKPS = 0;
            double peakCPS = 0;
            foreach (var stats in stableDays)
            {
                peakKPS = Math.Max(peakKPS, stats.PeakKPS);
                peakCPS = Math.Max(peakCPS, stats.PeakCPS);
            }

            stablePeaks = (peakKPS, peakCPS);
            PublishAggregate(ref _peakCache, stamp, stablePeaks);
        }

        return (Math.Max(stablePeaks.PeakKPS, live.PeakKPS), Math.Max(stablePeaks.PeakCPS, live.PeakCPS));
    }

    /// <summary>Today's live peaks plus any archived day from the statistics day onward.</summary>
    private (double PeakKPS, double PeakCPS) ComputeLivePeaksLocked(DateTime today, DateTime statisticsDay)
    {
        var peakKPS = CurrentStats.PeakKPS;
        var peakCPS = CurrentStats.PeakCPS;
        for (var date = statisticsDay; date <= today; date = date.AddDays(1))
        {
            if (!History.TryGetValue(date.ToString("yyyy-MM-dd"), out var stats))
            {
                continue;
            }

            peakKPS = Math.Max(peakKPS, stats.PeakKPS);
            peakCPS = Math.Max(peakCPS, stats.PeakCPS);
        }

        return (peakKPS, peakCPS);
    }

    public List<(string Key, int Count)> GetKeyPressBreakdownSorted()
    {
        lock (_lock)
        {
            return CurrentStats.KeyPressCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Key, x.Value))
                .ToList();
        }
    }

    public Dictionary<string, DailyStats> GetLocalHistorySnapshot()
    {
        lock (_lock)
        {
            var snapshot = CloneHistorySnapshot(History);
            snapshot[CurrentStats.Date.ToString("yyyy-MM-dd")] =
                CloneDailyStats(CurrentStats, CurrentStats.Date.Date);
            return snapshot;
        }
    }

    public List<AppStats> GetAppStatsSorted(int limit = 5)
    {
        lock (_lock)
        {
            return CurrentStats.AppStats.Values
                .OrderByDescending(a => a.KeyPresses + a.TotalClicks + a.ScrollDistance)
                .Take(limit)
                .Select(a => new AppStats(a))
                .ToList();
        }
    }

    #endregion

    #region App Stats Summary

    public enum AppStatsRange { Today, Week, Month, All }
    public enum KeyboardHeatmapRange { Today, Week, Month, All }
    public enum KeyboardHeatmapMode { Average, Sum }

    public sealed class KeyboardHeatmapDay
    {
        public DateTime Date { get; set; }
        public int TotalKeyPresses { get; set; }
        public Dictionary<string, int> KeyCounts { get; set; } = new(StringComparer.Ordinal);
        public List<AppStats> Apps { get; set; } = new();
        public string? SelectedAppName { get; set; }
    }

    public sealed class KeyboardHeatmapSummary
    {
        public double TotalKeyPresses { get; set; }
        public Dictionary<string, double> KeyCounts { get; set; } = new(StringComparer.Ordinal);
    }

    public List<AppStats> GetAppStatsSummary(AppStatsRange range)
    {
        var stable = GetHistoryAggregate(
            ref _appSummaryCache,
            range,
            today => GetAppStatsDates(range, today),
            today => range == AppStatsRange.All
                ? SelectLiveHistoryKeyDates(today, includeRemoteDays: false)
                : LiveDatesFrom(AppStatsRangeStart(range, today), today),
            AggregateAppStatsTotals,
            out var capture);

        var totals = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in stable)
        {
            totals[kvp.Key] = new AppStats(kvp.Value);
        }

        foreach (var day in capture.LiveDays)
        {
            MergeAppStats(ResolveDay(capture, day), totals);
        }

        return totals.Values
            .Select(a => new AppStats(a))
            .ToList();
    }

    private static Dictionary<string, AppStats> AggregateAppStatsTotals(HistoryAggregateCapture capture)
    {
        var totals = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in capture.StableDays)
        {
            MergeAppStats(ResolveDay(capture, day), totals);
        }

        return totals;
    }

    public (DateTime Start, DateTime End) GetKeyboardHeatmapDateBounds()
    {
        return GetKeyboardHeatmapDateBounds(DateTime.Today);
    }

    private (DateTime Start, DateTime End) GetKeyboardHeatmapDateBounds(DateTime today)
    {
        lock (_lock)
        {
            return (GetHeatmapStartLocked(today), today);
        }
    }

    public KeyboardHeatmapDay GetKeyboardHeatmapDay(DateTime date, string? appName = null)
    {
        var normalizedDate = date.Date;
        lock (_lock)
        {
            var daily = GetDailyStats(normalizedDate);
            var apps = daily.AppStats.Values
                .Where(app => app.KeyPresses > 0 || app.KeyPressCounts.Count > 0)
                .OrderByDescending(app => app.KeyPresses)
                .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.AppName, StringComparer.Ordinal)
                .Select(app => new AppStats(app))
                .ToList();
            var selectedApp = apps.FirstOrDefault(app => string.Equals(app.AppName, appName, StringComparison.OrdinalIgnoreCase));
            var aggregated = AggregateKeyboardHeatmapCounts(selectedApp?.KeyPressCounts ?? daily.KeyPressCounts);

            return new KeyboardHeatmapDay
            {
                Date = normalizedDate,
                TotalKeyPresses = Math.Max(0, selectedApp?.KeyPresses ?? daily.KeyPresses),
                KeyCounts = aggregated,
                Apps = apps,
                SelectedAppName = selectedApp?.AppName
            };
        }
    }

    public KeyboardHeatmapSummary GetKeyboardHeatmapSummary(
        KeyboardHeatmapRange range,
        KeyboardHeatmapMode mode)
    {
        var stable = GetHistoryAggregate(
            ref _heatmapCache,
            range,
            today => GetKeyboardHeatmapDates(range, today),
            today => LiveDatesFrom(
                range == KeyboardHeatmapRange.All ? GetHeatmapStartLocked(today) : KeyboardHeatmapRangeStart(range, today),
                today),
            AggregateHeatmapTotals,
            out var capture);

        var totalKeyPresses = stable.TotalKeyPresses;
        var keyCounts = new Dictionary<string, double>(stable.KeyCounts, StringComparer.Ordinal);
        var dayCount = stable.DayCount;

        foreach (var day in capture.LiveDays)
        {
            var daily = ResolveDay(capture, day);
            totalKeyPresses += Math.Max(0, daily.KeyPresses);

            foreach (var keyValue in AggregateKeyboardHeatmapCounts(daily.KeyPressCounts))
            {
                keyCounts[keyValue.Key] = (keyCounts.TryGetValue(keyValue.Key, out var current) ? current : 0) + keyValue.Value;
            }

            dayCount++;
        }

        if (mode == KeyboardHeatmapMode.Average && dayCount > 0)
        {
            totalKeyPresses /= dayCount;
            foreach (var key in keyCounts.Keys.ToList())
            {
                keyCounts[key] /= dayCount;
            }
        }

        return new KeyboardHeatmapSummary
        {
            TotalKeyPresses = totalKeyPresses,
            KeyCounts = keyCounts
        };
    }

    private static HeatmapTotals AggregateHeatmapTotals(HistoryAggregateCapture capture)
    {
        var totalKeyPresses = 0.0;
        var keyCounts = new Dictionary<string, double>(StringComparer.Ordinal);
        var dayCount = 0;

        foreach (var day in capture.StableDays)
        {
            var daily = ResolveDay(capture, day);
            totalKeyPresses += Math.Max(0, daily.KeyPresses);

            foreach (var keyValue in AggregateKeyboardHeatmapCounts(daily.KeyPressCounts))
            {
                keyCounts[keyValue.Key] = (keyCounts.TryGetValue(keyValue.Key, out var current) ? current : 0) + keyValue.Value;
            }

            dayCount++;
        }

        return new HeatmapTotals(totalKeyPresses, keyCounts, dayCount);
    }

    private List<DateTime> GetKeyboardHeatmapDates(KeyboardHeatmapRange range, DateTime today)
    {
        var startDate = range switch
        {
            KeyboardHeatmapRange.Today => today,
            KeyboardHeatmapRange.Week => today.AddDays(-6),
            KeyboardHeatmapRange.Month => today.AddDays(-29),
            KeyboardHeatmapRange.All => GetKeyboardHeatmapDateBounds(today).Start.Date,
            _ => today
        };

        var dates = new List<DateTime>();
        for (var date = startDate.Date; date <= today; date = date.AddDays(1))
        {
            dates.Add(date);
        }

        return dates;
    }

    private List<DateTime> GetAppStatsDates(AppStatsRange range, DateTime today)
    {
        var dates = new List<DateTime>();

        if (range == AppStatsRange.All)
        {
            foreach (var key in History.Keys)
            {
                if (DateTime.TryParse(key, out var parsed))
                {
                    dates.Add(parsed.Date);
                }
            }
            if (!dates.Contains(today))
            {
                dates.Add(today);
            }
            return dates.Distinct().OrderBy(d => d).ToList();
        }

        var startDate = range switch
        {
            AppStatsRange.Today => today,
            AppStatsRange.Week => today.AddDays(-6),
            AppStatsRange.Month => today.AddDays(-29),
            _ => today
        };

        for (var date = startDate.Date; date <= today; date = date.AddDays(1))
        {
            dates.Add(date);
        }

        return dates;
    }

    /// <summary>
    /// Single-date read used by the per-day heatmap view. Multi-day queries capture one
    /// <see cref="DisplayRemoteSnapshot"/> and go through <see cref="ResolveDay"/> instead.
    /// </summary>
    private DailyStats GetDailyStats(DateTime date)
    {
        var local = GetLocalDailyStats(date);

        return _displayStatsAggregator != null && _isSyncEnabledProvider?.Invoke() == true
            ? _displayStatsAggregator.Aggregate(date.Date, local)
            : local;
    }

    private DailyStats GetLocalDailyStats(DateTime date)
    {
        if (date.Date == CurrentStats.Date.Date)
        {
            return CurrentStats;
        }

        var key = date.ToString("yyyy-MM-dd");
        return History.TryGetValue(key, out var stats) ? stats : new DailyStats(date);
    }

    private static Dictionary<string, int> AggregateKeyboardHeatmapCounts(Dictionary<string, int> keyPressCounts)
    {
        var aggregated = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var kvp in keyPressCounts)
        {
            var count = Math.Max(0, kvp.Value);
            if (count <= 0)
            {
                continue;
            }

            var rawKey = kvp.Key ?? string.Empty;
            if (NormalizeKeyboardHeatmapKey(rawKey) is string exactKey)
            {
                aggregated[exactKey] = SafeAdd(aggregated.TryGetValue(exactKey, out var current) ? current : 0, count);
                continue;
            }

            if (rawKey.IndexOf("Num+", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                aggregated["Num+"] = SafeAdd(aggregated.TryGetValue("Num+", out var current) ? current : 0, count);
            }

            var components = rawKey
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            if (components.Count == 0)
            {
                components.Add(rawKey.Trim());
            }

            foreach (var sourceKey in components)
            {
                if (NormalizeKeyboardHeatmapKey(sourceKey) is not string normalizedKey || string.IsNullOrWhiteSpace(normalizedKey))
                {
                    continue;
                }

                aggregated[normalizedKey] = SafeAdd(aggregated.TryGetValue(normalizedKey, out var current) ? current : 0, count);
            }
        }

        return aggregated;
    }

    private static string? NormalizeKeyboardHeatmapKey(string rawKey)
    {
        var trimmed = (rawKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var upper = trimmed.ToUpperInvariant();
        if (upper.Length == 1)
        {
            return upper;
        }

        if (upper.StartsWith("F", StringComparison.Ordinal) && int.TryParse(upper.Substring(1), out _))
        {
            return upper;
        }

        if (upper.StartsWith("NUM", StringComparison.Ordinal))
        {
            var suffix = upper.Substring(3);
            if (suffix.Length == 1 && char.IsDigit(suffix[0]))
            {
                return "Num" + suffix;
            }

            if (suffix == ".")
            {
                return "Num.";
            }
            if (suffix == "+")
            {
                return "Num+";
            }
            if (suffix == "-")
            {
                return "Num-";
            }
            if (suffix == "/")
            {
                return "Num/";
            }
            if (suffix == "*")
            {
                return "Num*";
            }
            if (suffix == "ENTER")
            {
                return "NumEnter";
            }
        }

        switch (upper)
        {
            case "CMD":
            case "COMMAND":
            case "WIN":
                return "Cmd";
            case "LWIN":
                return "LWin";
            case "RWIN":
                return "RWin";
            case "CTRL":
            case "CONTROL":
                return "Ctrl";
            case "LCTRL":
            case "LCTL":
            case "LCONTROL":
                return "LCtrl";
            case "RCTRL":
            case "RCTL":
            case "RCONTROL":
                return "RCtrl";
            case "OPTION":
            case "OPT":
            case "ALT":
            case "MENU":
                return "Option";
            case "LALT":
            case "LMENU":
                return "LAlt";
            case "RALT":
            case "RMENU":
                return "RAlt";
            case "SHIFT":
            case "LSHIFT":
            case "RSHIFT":
                return "Shift";
            case "FN":
            case "FUNCTION":
                return "Fn";
            case "APPS":
            case "APPLICATION":
            case "CONTEXTMENU":
                return "Apps";
            case "SPACE":
            case "SPACEBAR":
                return "Space";
            case "ESC":
            case "ESCAPE":
                return "Esc";
            case "ENTER":
            case "RETURN":
                return "Return";
            case "TAB":
                return "Tab";
            case "BACKSPACE":
            case "BKSP":
                return "Backspace";
            case "DELETE":
            case "DEL":
            case "FORWARDDELETE":
                return "Delete";
            case "INSERT":
            case "INS":
                return "Insert";
            case "CAPSLOCK":
                return "CapsLock";
            case "PAGEUP":
            case "PGUP":
            case "PRIOR":
                return "PageUp";
            case "PAGEDOWN":
            case "PGDN":
            case "NEXT":
                return "PageDown";
            case "HOME":
                return "Home";
            case "END":
                return "End";
            case "PRINTSCREEN":
            case "PRTSC":
            case "PRTSCN":
            case "SNAPSHOT":
                return "PrintScreen";
            case "SCROLLLOCK":
            case "SCROLL":
                return "ScrollLock";
            case "NUMLOCK":
                return "NumLock";
            case "PAUSE":
            case "BREAK":
                return "Pause";
            case "LEFT":
            case "ARROWLEFT":
            case "LEFTARROW":
                return "Left";
            case "RIGHT":
            case "ARROWRIGHT":
            case "RIGHTARROW":
                return "Right";
            case "UP":
            case "ARROWUP":
            case "UPARROW":
                return "Up";
            case "DOWN":
            case "ARROWDOWN":
            case "DOWNARROW":
                return "Down";
            default:
                return null;
        }
    }

    private static int SafeAdd(int left, int right)
    {
        if (right <= 0)
        {
            return left;
        }

        if (left > int.MaxValue - right)
        {
            return int.MaxValue;
        }

        return left + right;
    }

    private static void MergeAppStats(DailyStats daily, Dictionary<string, AppStats> totals)
    {
        if (daily.AppStats.Count == 0) return;

        foreach (var kvp in daily.AppStats)
        {
            var appName = kvp.Key;
            var source = kvp.Value;

            if (!totals.TryGetValue(appName, out var total))
            {
                total = new AppStats(appName, source.DisplayName);
                totals[appName] = total;
            }

            if (!string.IsNullOrEmpty(source.DisplayName))
            {
                total.DisplayName = source.DisplayName;
            }

            total.KeyPresses += source.KeyPresses;
            total.LeftClicks += source.LeftClicks;
            total.RightClicks += source.RightClicks;
            total.MiddleClicks += source.MiddleClicks;
            total.SideBackClicks += source.SideBackClicks;
            total.SideForwardClicks += source.SideForwardClicks;
            total.ScrollDistance += source.ScrollDistance;
        }
    }

    #endregion

    #region History aggregate cache

    /// <summary>
    /// Identifies the immutable part of the history view: the monotonic revision plus the
    /// statistics day, the wall-clock day the query ranges were derived from, and whether
    /// remote data currently contributes. Entries whose stamp no longer matches are ignored.
    /// </summary>
    private readonly struct HistoryAggregateStamp
    {
        public HistoryAggregateStamp(long revision, DateTime statisticsDay, DateTime today, bool syncEnabled)
        {
            Revision = revision;
            StatisticsDay = statisticsDay;
            Today = today;
            SyncEnabled = syncEnabled;
        }

        public long Revision { get; }
        public DateTime StatisticsDay { get; }
        public DateTime Today { get; }
        public bool SyncEnabled { get; }

        public bool Matches(HistoryAggregateStamp other) =>
            Revision == other.Revision &&
            StatisticsDay == other.StatisticsDay &&
            Today == other.Today &&
            SyncEnabled == other.SyncEnabled;
    }

    private sealed class HistoryAggregateEntry<T>
    {
        public HistoryAggregateEntry(HistoryAggregateStamp stamp, T value)
        {
            Stamp = stamp;
            Value = value;
        }

        public HistoryAggregateStamp Stamp { get; }
        public T Value { get; }
    }

    private sealed class HistoryAggregateCapture
    {
        public HistoryAggregateCapture(
            HistoryAggregateStamp stamp,
            DisplayStatsAggregator? aggregator,
            DisplayRemoteSnapshot remote,
            List<(DateTime Date, DailyStats Stats)> stableDays,
            List<(DateTime Date, DailyStats Stats)> liveDays)
        {
            Stamp = stamp;
            Aggregator = aggregator;
            Remote = remote;
            StableDays = stableDays;
            LiveDays = liveDays;
        }

        public HistoryAggregateStamp Stamp { get; }
        public DisplayStatsAggregator? Aggregator { get; }
        public DisplayRemoteSnapshot Remote { get; }

        /// <summary>Days strictly before the statistics day: immutable until a revision bump.</summary>
        public List<(DateTime Date, DailyStats Stats)> StableDays { get; }

        /// <summary>Days at or after the statistics day: re-read on every query.</summary>
        public List<(DateTime Date, DailyStats Stats)> LiveDays { get; }
    }

    private readonly struct HeatmapTotals
    {
        public HeatmapTotals(double totalKeyPresses, Dictionary<string, double> keyCounts, int dayCount)
        {
            TotalKeyPresses = totalKeyPresses;
            KeyCounts = keyCounts;
            DayCount = dayCount;
        }

        public double TotalKeyPresses { get; }
        public Dictionary<string, double> KeyCounts { get; }
        public int DayCount { get; }
    }

    private readonly struct TrendSeriesTotals
    {
        public TrendSeriesTotals(
            List<(DateTime Date, double Value)> local,
            List<(DateTime Date, double Value)>? display)
        {
            Local = local;
            Display = display;
        }

        public List<(DateTime Date, double Value)> Local { get; }
        public List<(DateTime Date, double Value)>? Display { get; }
    }

    private readonly struct InputTotals
    {
        public InputTotals(
            int leftClicks,
            int middleClicks,
            int rightClicks,
            int sideBackClicks,
            int sideForwardClicks,
            double mouseDistance,
            double scrollDistance)
        {
            LeftClicks = leftClicks;
            MiddleClicks = middleClicks;
            RightClicks = rightClicks;
            SideBackClicks = sideBackClicks;
            SideForwardClicks = sideForwardClicks;
            MouseDistance = mouseDistance;
            ScrollDistance = scrollDistance;
        }

        public int LeftClicks { get; }
        public int MiddleClicks { get; }
        public int RightClicks { get; }
        public int SideBackClicks { get; }
        public int SideForwardClicks { get; }
        public double MouseDistance { get; }
        public double ScrollDistance { get; }
    }

    private HistoryAggregateStamp CaptureStampLocked(DateTime today) =>
        new(
            _historyRevision,
            CurrentStats.Date.Date,
            today.Date,
            _isSyncEnabledProvider?.Invoke() == true && _displayStatsAggregator != null);

    private bool IsStampCurrentLocked(HistoryAggregateStamp stamp) =>
        stamp.Today == DateTime.Today.Date && stamp.Matches(CaptureStampLocked(stamp.Today));

    /// <summary>
    /// Serves a cached aggregate for the stable part of the range and only reads the days at or
    /// after the statistics day on a hit. <paramref name="selectLiveDates"/> must return a
    /// bounded window (the live days plus the ones a range adds) instead of walking the whole
    /// history, so repeated refreshes never rescan or re-aggregate past days.
    /// </summary>
    private T GetHistoryAggregate<TKey, T>(
        ref Dictionary<TKey, HistoryAggregateEntry<T>>? cache,
        TKey key,
        Func<DateTime, List<DateTime>> selectDates,
        Func<DateTime, List<DateTime>> selectLiveDates,
        Func<HistoryAggregateCapture, T> build,
        out HistoryAggregateCapture capture)
        where TKey : notnull
    {
        var today = DateTime.Today;
        HistoryAggregateStamp stamp;
        DisplayStatsAggregator? aggregator;
        T cachedValue = default!;
        bool hit;
        var stableDays = new List<(DateTime Date, DailyStats Stats)>();
        var liveDays = new List<(DateTime Date, DailyStats Stats)>();

        // The stamp and the day inputs are taken under one lock: a rollover or import in between
        // could otherwise bind new history to an older stamp.
        lock (_lock)
        {
            stamp = CaptureStampLocked(today);
            hit = TryGetCachedAggregate(cache, key, stamp, out cachedValue);
            aggregator = stamp.SyncEnabled ? _displayStatsAggregator : null;
            CollectDaysLocked(stamp, (hit ? selectLiveDates : selectDates)(today), stableDays, liveDays);
        }

        // The remote snapshot is captured outside the statistics lock; it is memoised per revision.
        capture = new HistoryAggregateCapture(stamp, aggregator, GetRemoteSnapshot(stamp), stableDays, liveDays);
        if (hit)
        {
            return cachedValue;
        }

        var value = build(capture);
        // A result computed while the history revision moved on is returned but never cached.
        PublishAggregate(ref cache, key, stamp, value);
        return value;
    }

    /// <summary>
    /// Splits the range into immutable archived references and clones of the still-changing days.
    /// Callers must hold <see cref="_lock"/>.
    /// </summary>
    private void CollectDaysLocked(
        HistoryAggregateStamp stamp,
        List<DateTime> dates,
        List<(DateTime Date, DailyStats Stats)> stableDays,
        List<(DateTime Date, DailyStats Stats)> liveDays)
    {
        foreach (var date in dates)
        {
            var normalized = date.Date;
            if (normalized < stamp.StatisticsDay && normalized <= stamp.Today)
            {
                // History entries are replaced, never mutated in place, so the reference is
                // safe to aggregate outside the lock.
                stableDays.Add((normalized, GetLocalDailyStats(normalized)));
            }
            else
            {
                liveDays.Add((normalized, CloneDailyStats(GetLocalDailyStats(normalized), normalized)));
            }
        }
    }

    /// <summary>
    /// Remote contributions are captured once per history revision: ordinary counter updates do
    /// not change them, and every remote change advances <see cref="HistoryRevision"/>.
    /// </summary>
    private DisplayRemoteSnapshot GetRemoteSnapshot(HistoryAggregateStamp stamp)
    {
        if (!stamp.SyncEnabled)
        {
            return DisplayRemoteSnapshot.Empty;
        }

        DisplayStatsAggregator? aggregator;
        lock (_lock)
        {
            if (_remoteSnapshot != null && _remoteSnapshotRevision == stamp.Revision)
            {
                return _remoteSnapshot;
            }

            aggregator = _displayStatsAggregator;
        }

        if (aggregator == null)
        {
            return DisplayRemoteSnapshot.Empty;
        }

        var snapshot = aggregator.CaptureSnapshot();
        lock (_lock)
        {
            // Only memoise when the capture still belongs to this revision; otherwise the caller
            // gets a one-off snapshot and its own aggregate is never cached (stale stamp).
            if (_historyRevision == stamp.Revision)
            {
                _remoteSnapshot = snapshot;
                _remoteSnapshotRevision = stamp.Revision;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Dates from <paramref name="startDate"/> to today that belong to the live window. Callers
    /// must hold <see cref="_lock"/> (it reads <see cref="CurrentStats"/>).
    /// </summary>
    private List<DateTime> LiveDatesFrom(DateTime startDate, DateTime today)
    {
        var statisticsDay = CurrentStats.Date.Date;
        var start = startDate.Date > statisticsDay ? startDate.Date : statisticsDay;
        var dates = new List<DateTime>();
        for (var date = start; date <= today; date = date.AddDays(1))
        {
            dates.Add(date);
        }

        return dates;
    }

    /// <summary>Live subset of the "All" key/app date sets: archived days from the statistics day on.</summary>
    private List<DateTime> SelectLiveHistoryKeyDates(DateTime today, bool includeRemoteDays)
    {
        var statisticsDay = CurrentStats.Date.Date;
        var dates = new List<DateTime>();
        for (var date = statisticsDay; date <= today; date = date.AddDays(1))
        {
            if (History.ContainsKey(date.ToString("yyyy-MM-dd")))
            {
                dates.Add(date);
            }
        }

        if (today >= statisticsDay && !dates.Contains(today))
        {
            dates.Add(today);
        }

        if (includeRemoteDays && _isSyncEnabledProvider?.Invoke() == true && _displayStatsAggregator != null)
        {
            // Reuse the per-revision snapshot's day list on the hot path instead of walking the
            // remote cache again; only the memo lookup itself touches the cache.
            IEnumerable<DateTime> remoteDays =
                _remoteSnapshot != null && _remoteSnapshotRevision == _historyRevision
                    ? _remoteSnapshot.Days
                    : _displayStatsAggregator.GetRemoteDays();

            foreach (var remoteDay in remoteDays)
            {
                if (remoteDay <= today && remoteDay >= statisticsDay && !dates.Contains(remoteDay))
                {
                    dates.Add(remoteDay);
                }
            }
        }

        dates.Sort();
        return dates;
    }

    private static DateTime HistoryRangeStart(HistoryRange range, DateTime today) => range switch
    {
        HistoryRange.Yesterday => today.AddDays(-1),
        HistoryRange.ThreeDays => today.AddDays(-2),
        HistoryRange.Week => today.AddDays(-6),
        HistoryRange.Month => today.AddDays(-29),
        _ => today
    };

    private static DateTime KeyHistoryRangeStart(KeyHistoryRange range, DateTime today) => range switch
    {
        KeyHistoryRange.Week => today.AddDays(-6),
        KeyHistoryRange.Month => today.AddDays(-29),
        _ => today
    };

    private static DateTime AppStatsRangeStart(AppStatsRange range, DateTime today) => range switch
    {
        AppStatsRange.Week => today.AddDays(-6),
        AppStatsRange.Month => today.AddDays(-29),
        _ => today
    };

    private static DateTime KeyboardHeatmapRangeStart(KeyboardHeatmapRange range, DateTime today) => range switch
    {
        KeyboardHeatmapRange.Week => today.AddDays(-6),
        KeyboardHeatmapRange.Month => today.AddDays(-29),
        _ => today
    };

    private static DateTime MouseTotalsRangeStart(MouseTotalsRange range, DateTime today) => range switch
    {
        MouseTotalsRange.Week => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
        MouseTotalsRange.Month => new DateTime(today.Year, today.Month, 1),
        MouseTotalsRange.Year => new DateTime(today.Year, 1, 1),
        _ => today
    };

    /// <summary>
    /// Earliest archived day. Validated by the full history stamp (revision, wall-clock day and
    /// statistics day) so a date that moves on before the rollover cannot reuse a stale boundary.
    /// </summary>
    private DateTime GetHistoryMinDayLocked(DateTime today)
    {
        var statisticsDay = CurrentStats.Date.Date;
        if (_historyMinDayValid &&
            _historyMinDayRevision == _historyRevision &&
            _historyMinDayToday == today.Date &&
            _historyMinDayStatisticsDay == statisticsDay)
        {
            return _historyMinDay;
        }

        var min = today;
        foreach (var stats in History.Values)
        {
            var date = stats.Date.Date;
            if (date <= today && date < min)
            {
                min = date;
            }
        }

        _historyMinDay = min;
        _historyMinDayRevision = _historyRevision;
        _historyMinDayToday = today.Date;
        _historyMinDayStatisticsDay = statisticsDay;
        _historyMinDayValid = true;
        return min;
    }

    private DateTime GetHeatmapStartLocked(DateTime today)
    {
        var statisticsDay = CurrentStats.Date.Date;
        var syncEnabled = _isSyncEnabledProvider?.Invoke() == true && _displayStatsAggregator != null;
        if (_heatmapStartValid &&
            _heatmapStartRevision == _historyRevision &&
            _heatmapStartToday == today.Date &&
            _heatmapStartStatisticsDay == statisticsDay &&
            _heatmapStartSyncEnabled == syncEnabled)
        {
            return _heatmapStart;
        }

        var start = statisticsDay <= today ? statisticsDay : today;
        foreach (var stats in History.Values)
        {
            var candidate = stats.Date.Date;
            if (candidate <= today && candidate < start)
            {
                start = candidate;
            }
        }

        if (syncEnabled)
        {
            foreach (var candidate in _displayStatsAggregator!.GetRemoteDays())
            {
                if (candidate <= today && candidate < start)
                {
                    start = candidate;
                }
            }
        }

        _heatmapStart = start;
        _heatmapStartRevision = _historyRevision;
        _heatmapStartToday = today.Date;
        _heatmapStartStatisticsDay = statisticsDay;
        _heatmapStartSyncEnabled = syncEnabled;
        _heatmapStartValid = true;
        return start;
    }

    private static DailyStats ResolveDay(HistoryAggregateCapture capture, (DateTime Date, DailyStats Stats) day) =>
        capture.Aggregator != null
            ? capture.Aggregator.Aggregate(day.Date, day.Stats, capture.Remote)
            : day.Stats;

    private static bool TryGetCachedAggregate<T>(
        HistoryAggregateEntry<T>? entry,
        in HistoryAggregateStamp stamp,
        out T value)
    {
        if (entry != null && entry.Stamp.Matches(stamp))
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    private static bool TryGetCachedAggregate<TKey, T>(
        Dictionary<TKey, HistoryAggregateEntry<T>>? cache,
        TKey key,
        in HistoryAggregateStamp stamp,
        out T value)
        where TKey : notnull
    {
        if (cache != null && cache.TryGetValue(key, out var entry))
        {
            return TryGetCachedAggregate(entry, stamp, out value);
        }

        value = default!;
        return false;
    }

    private void PublishAggregate<T>(ref HistoryAggregateEntry<T>? field, in HistoryAggregateStamp stamp, T value)
    {
        lock (_lock)
        {
            if (!IsStampCurrentLocked(stamp))
            {
                return;
            }

            field = new HistoryAggregateEntry<T>(stamp, value);
        }
    }

    private void PublishAggregate<TKey, T>(
        ref Dictionary<TKey, HistoryAggregateEntry<T>>? cache,
        TKey key,
        in HistoryAggregateStamp stamp,
        T value)
        where TKey : notnull
    {
        lock (_lock)
        {
            if (!IsStampCurrentLocked(stamp))
            {
                return;
            }

            (cache ??= new Dictionary<TKey, HistoryAggregateEntry<T>>())[key] =
                new HistoryAggregateEntry<T>(stamp, value);
        }
    }

    #endregion

    #region History

    public enum HistoryRange { Today, Yesterday, ThreeDays, Week, Month, All }
    public enum HistoryMetric { KeyPresses, Clicks, MouseDistance, ScrollDistance }
    public enum MouseTotalsRange { Week, Month, Year, All }
    public enum KeyHistoryRange { Today, Week, Month, All }

    public sealed class HistoryTrendSeries
    {
        public HistoryTrendSeries(
            List<(DateTime Date, double Value)> display,
            List<(DateTime Date, double Value)>? local)
        {
            Display = display;
            Local = local;
        }

        public List<(DateTime Date, double Value)> Display { get; }
        public List<(DateTime Date, double Value)>? Local { get; }
    }

    public List<(DateTime Date, double Value)> GetHistorySeries(HistoryRange range, HistoryMetric metric)
    {
        return GetHistoryTrendSeries(range, metric).Display;
    }

    public HistoryTrendSeries GetHistoryTrendSeries(HistoryRange range, HistoryMetric metric)
    {
        var stable = GetHistoryAggregate(
            ref _trendCache,
            (range, metric),
            today => GetDatesInRange(range, today),
            today => LiveDatesFrom(
                range == HistoryRange.All ? GetHistoryMinDayLocked(today) : HistoryRangeStart(range, today),
                today),
            capture => AggregateTrendTotals(capture, metric),
            out var capture);

        var localSeries = new List<(DateTime Date, double Value)>(stable.Local);
        var displaySeries = stable.Display != null
            ? new List<(DateTime Date, double Value)>(stable.Display)
            : null;

        foreach (var day in capture.LiveDays)
        {
            localSeries.Add((day.Date, GetMetricValue(metric, day.Stats)));
            if (displaySeries != null)
            {
                displaySeries.Add((day.Date, GetMetricValue(metric, ResolveDay(capture, day))));
            }
        }

        return new HistoryTrendSeries(displaySeries ?? localSeries, localSeries);
    }

    private TrendSeriesTotals AggregateTrendTotals(HistoryAggregateCapture capture, HistoryMetric metric)
    {
        var local = capture.StableDays
            .Select(day => (day.Date, GetMetricValue(metric, day.Stats)))
            .ToList();

        var hasRemoteDays = capture.Aggregator != null &&
                            capture.Stamp.SyncEnabled &&
                            capture.Remote.Days.Count > 0;

        if (metric is HistoryMetric.MouseDistance or HistoryMetric.ScrollDistance || !hasRemoteDays)
        {
            return new TrendSeriesTotals(local, null);
        }

        var display = capture.StableDays
            .Select(day => (day.Date, GetMetricValue(metric, ResolveDay(capture, day))))
            .ToList();

        return new TrendSeriesTotals(local, display);
    }

    public (
        int LeftClicks,
        int MiddleClicks,
        int RightClicks,
        int SideBackClicks,
        int SideForwardClicks,
        double MouseDistance,
        double ScrollDistance
    ) GetHistoryInputTotals(MouseTotalsRange range)
    {
        var stable = GetHistoryAggregate(
            ref _inputTotalsCache,
            range,
            today => GetDatesInMouseTotalsRange(range, today),
            today => LiveDatesFrom(
                range == MouseTotalsRange.All ? CurrentStats.Date.Date : MouseTotalsRangeStart(range, today),
                today),
            AggregateInputTotals,
            out var capture);

        var totals = stable;
        foreach (var day in capture.LiveDays)
        {
            totals = AddDayToInputTotals(totals, ResolveDay(capture, day));
        }

        return (
            totals.LeftClicks,
            totals.MiddleClicks,
            totals.RightClicks,
            totals.SideBackClicks,
            totals.SideForwardClicks,
            totals.MouseDistance,
            totals.ScrollDistance);
    }

    private static InputTotals AggregateInputTotals(HistoryAggregateCapture capture)
    {
        var totals = new InputTotals(0, 0, 0, 0, 0, 0.0, 0.0);
        foreach (var day in capture.StableDays)
        {
            totals = AddDayToInputTotals(totals, ResolveDay(capture, day));
        }

        return totals;
    }

    private static InputTotals AddDayToInputTotals(InputTotals totals, DailyStats stats) =>
        new(
            SafeAdd(totals.LeftClicks, stats.LeftClicks),
            SafeAdd(totals.MiddleClicks, stats.MiddleClicks),
            SafeAdd(totals.RightClicks, stats.RightClicks),
            SafeAdd(totals.SideBackClicks, stats.SideBackClicks),
            SafeAdd(totals.SideForwardClicks, stats.SideForwardClicks),
            totals.MouseDistance + Math.Max(0, stats.MouseDistance),
            totals.ScrollDistance + Math.Max(0, stats.ScrollDistance));

    public string FormatHistoryValue(HistoryMetric metric, double value)
    {
        return metric switch
        {
            HistoryMetric.KeyPresses or HistoryMetric.Clicks => FormatNumber((int)value),
            HistoryMetric.MouseDistance => FormatMouseDistance(value),
            HistoryMetric.ScrollDistance => FormatScrollDistance(value),
            _ => value.ToString("N0")
        };
    }

    public List<(string Key, int Count)> GetHistoricalKeyCounts(KeyHistoryRange range, int limit = int.MaxValue)
    {
        var stable = GetHistoryAggregate(
            ref _keyHistoryCache,
            range,
            today => GetDatesInKeyHistoryRange(range, today),
            today => range == KeyHistoryRange.All
                ? SelectLiveHistoryKeyDates(today, includeRemoteDays: true)
                : LiveDatesFrom(KeyHistoryRangeStart(range, today), today),
            AggregateKeyHistoryTotals,
            out var capture);

        var totals = new Dictionary<string, int>(stable, StringComparer.Ordinal);
        foreach (var day in capture.LiveDays)
        {
            MergeKeyCounts(ResolveDay(capture, day).KeyPressCounts, totals);
        }

        var sorted = totals
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Value));

        if (limit > 0 && limit < int.MaxValue)
        {
            return sorted.Take(limit).ToList();
        }

        return sorted.ToList();
    }

    private static Dictionary<string, int> AggregateKeyHistoryTotals(HistoryAggregateCapture capture)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var day in capture.StableDays)
        {
            MergeKeyCounts(ResolveDay(capture, day).KeyPressCounts, totals);
        }

        return totals;
    }

    private List<DateTime> GetDatesInMouseTotalsRange(MouseTotalsRange range, DateTime today)
    {
        var startDate = range switch
        {
            MouseTotalsRange.Week => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            MouseTotalsRange.Month => new DateTime(today.Year, today.Month, 1),
            MouseTotalsRange.Year => new DateTime(today.Year, 1, 1),
            MouseTotalsRange.All => History.Values
                .Select(stats => stats.Date.Date)
                .Append(CurrentStats.Date.Date)
                .Concat(_displayStatsAggregator != null && _isSyncEnabledProvider?.Invoke() == true
                    ? _displayStatsAggregator.GetRemoteDays()
                    : Array.Empty<DateTime>())
                .Where(date => date <= today)
                .DefaultIfEmpty(today)
                .Min(),
            _ => today
        };

        var dates = new List<DateTime>();
        for (var date = startDate; date <= today; date = date.AddDays(1))
        {
            dates.Add(date);
        }
        return dates;
    }

    private List<DateTime> GetDatesInRange(HistoryRange range, DateTime today)
    {
        DateTime startDate;
        if (range == HistoryRange.All)
        {
            startDate = GetHistoryMinDayLocked(today);
        }
        else
        {
            startDate = range switch
            {
                HistoryRange.Today => today,
                HistoryRange.Yesterday => today.AddDays(-1),
                HistoryRange.ThreeDays => today.AddDays(-2),
                HistoryRange.Week => today.AddDays(-6),
                HistoryRange.Month => today.AddDays(-29),
                _ => today
            };
        }

        var dates = new List<DateTime>();
        for (var date = startDate; date <= today; date = date.AddDays(1))
        {
            dates.Add(date);
        }
        return dates;
    }

    private List<DateTime> GetDatesInKeyHistoryRange(KeyHistoryRange range, DateTime today)
    {
        if (range == KeyHistoryRange.All)
        {
            var dates = History.Values
                .Select(x => x.Date.Date)
                .Where(x => x <= today)
                .ToHashSet();
            dates.Add(today);
            if (_displayStatsAggregator != null && _isSyncEnabledProvider?.Invoke() == true)
            {
                dates.UnionWith(_displayStatsAggregator.GetRemoteDays().Where(date => date <= today));
            }
            return dates.OrderBy(x => x).ToList();
        }

        var startDate = range switch
        {
            KeyHistoryRange.Today => today,
            KeyHistoryRange.Week => today.AddDays(-6),
            KeyHistoryRange.Month => today.AddDays(-29),
            _ => today
        };

        var result = new List<DateTime>();
        for (var date = startDate.Date; date <= today; date = date.AddDays(1))
        {
            result.Add(date);
        }

        return result;
    }

    private double GetMetricValue(HistoryMetric metric, DailyStats stats)
    {
        return metric switch
        {
            HistoryMetric.KeyPresses => stats.KeyPresses,
            HistoryMetric.Clicks => stats.TotalClicks,
            HistoryMetric.MouseDistance => stats.MouseDistance,
            HistoryMetric.ScrollDistance => stats.ScrollDistance,
            _ => 0
        };
    }

    private static void MergeKeyCounts(Dictionary<string, int> source, Dictionary<string, int> target)
    {
        foreach (var kvp in source)
        {
            var key = (kvp.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var count = Math.Max(0, kvp.Value);
            if (count <= 0)
            {
                continue;
            }

            target[key] = SafeAdd(target.TryGetValue(key, out var existing) ? existing : 0, count);
        }
    }

    public string FormatMouseDistance(double distance)
    {
        if (string.Equals(Settings.MouseDistanceUnit, "px", StringComparison.OrdinalIgnoreCase))
        {
            return $"{distance:F0} px";
        }

        var metersPerPixel = GetMetersPerPixel();
        if (metersPerPixel <= 0)
        {
            return $"{distance:F0} px";
        }

        var meters = distance * metersPerPixel;
        if (meters >= 1000)
            return $"{meters / 1000:F2} km";
        if (meters >= 1)
            return $"{meters:F1} m";
        return $"{meters * 100:F1} cm";
    }

    public string FormatScrollDistance(double distance)
    {
        if (distance >= 10000)
            return $"{distance / 1000:F1} k";
        return $"{distance:F0} px";
    }

    public string FormatCalibratedDistance(double distance)
    {
        var ppi = Settings.MouseScrollPpi > 0 ? Settings.MouseScrollPpi : 92;
        var centimeters = Math.Max(0, distance) / ppi * 2.54;
        if (centimeters >= 100_000)
            return $"{centimeters / 100_000:F2} km";
        if (centimeters >= 100)
            return $"{centimeters / 100:F1} m";
        return $"{centimeters:F1} cm";
    }

    #endregion

    #region Mouse Calibration

    public void UpdateMouseCalibration(double metersPerPixel)
    {
        if (double.IsNaN(metersPerPixel) || double.IsInfinity(metersPerPixel) || metersPerPixel <= 0)
        {
            return;
        }

        lock (_lock)
        {
            Settings.MouseMetersPerPixel = metersPerPixel;
        }

        SaveSettings();
        NotifyStatsUpdate();
    }

    public void UpdateMouseScrollPpi(int ppi)
    {
        if (ppi <= 0) return;

        lock (_lock)
        {
            Settings.MouseScrollPpi = ppi;
        }

        SaveSettings();
        NotifyStatsUpdate();
    }

    public void UpdateMouseDistanceUnit(string unit)
    {
        var normalized = string.Equals(unit, "px", StringComparison.OrdinalIgnoreCase) ? "px" : "auto";
        lock (_lock)
        {
            Settings.MouseDistanceUnit = normalized;
        }

        SaveSettings();
        NotifyStatsUpdate();
    }

    private double GetMetersPerPixel()
    {
        var metersPerPixel = Settings.MouseMetersPerPixel;
        if (double.IsNaN(metersPerPixel) || double.IsInfinity(metersPerPixel) || metersPerPixel <= 0)
        {
            return DefaultMetersPerPixel;
        }
        return metersPerPixel;
    }

    #endregion

    public void FlushPendingSave()
    {
        StopActivityTimers();
        StopMidnightTimer();

        lock (_lock)
        {
            if (_isDisposed) return;

            _pendingSave = false;
            _pendingStatsUpdate = false;
            _pendingMouseMoveUpdate = false;
            _pendingSettingsSave = false;
        }

        SaveStats();
        SaveHistory();
        FlushSettings();
    }

    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        TeardownInputMonitor();
        StopActivityTimers();
        StopMidnightTimer();

        // Take the gate for the final flush and mark it complete under the same gate: a save that
        // was already queued cannot write a newer snapshot over the exit state afterwards.
        lock (SaveGate)
        {
            WriteStatsSnapshot();
            WriteHistorySnapshot();
            WriteSettingsSnapshot();
            _shutdownFlushCompleted = true;
        }

        DisposeActivityTimers();
        DisposeMidnightTimer();
        _instance = null;
    }
}
