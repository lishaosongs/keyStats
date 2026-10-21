using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Models;
using KeyStats.Services;
using KeyStats.Views;

namespace KeyStats.ViewModels;

public class KeyCountItem
{
    public string Key { get; set; } = "";
    public string Count { get; set; } = "";
}

public class AppStatsItem
{
    public string Name { get; set; } = "";
    public string KeyPresses { get; set; } = "0";
    public string Clicks { get; set; } = "0";
    public ImageSource? Icon { get; set; }
    public bool HasIcon => Icon != null;
}

public class ChartDataPoint
{
    public DateTime Date { get; set; }
    public double Value { get; set; }
}

public class StatsPopupViewModel : ViewModelBase
{
    private const int RefreshScopeLight = 0;
    private const int RefreshScopeFull = 1;

    private bool _isActive = true;
    private bool _needsRefresh;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly BackgroundStatsRefresher<PopupRefreshResult> _refresher;
    private HistoryQuery _query = HistoryQuery.FromSelection(1, 0, 3);
    private int _fullRequestStamp;
    private int _fullAppliedStamp;
    private string _keyPresses = "0";
    private string _totalClicks = "0";
    private string _leftClicks = "0";
    private string _rightClicks = "0";
    private string _middleClicks = "0";
    private string _sideBackClicks = "0";
    private string _sideForwardClicks = "0";
    private bool _isSideClickRowVisible;
    private string _mouseDistance = "0 px";
    private string _scrollDistance = "0 px";
    private string _peakKPS = "0";
    private string _peakCPS = "0";
    private bool _isPeakPopupOpen;
    private int _selectedRangeIndex = 1;
    private int _selectedMouseTotalsRangeIndex = 3;
    private int _selectedMetricIndex;
    private int _selectedChartStyleIndex;
    private string _historySummary = string.Format(KeyStats.Properties.Strings.History_TotalFormat, "0");
    private string _historyLeftClicks = "0";
    private string _historyMiddleClicks = "0";
    private string _historyRightClicks = "0";
    private string _historySideBackClicks = "0";
    private string _historySideForwardClicks = "0";
    private string _historyMouseDistance = "0 px";
    private string _historyScrollDistance = "0 px";
    private ObservableCollection<ChartDataPoint> _chartData = new();
    private ObservableCollection<ChartDataPoint>? _localChartData;

    public string KeyPresses
    {
        get => _keyPresses;
        set => SetProperty(ref _keyPresses, value);
    }

    public string TotalClicks
    {
        get => _totalClicks;
        set => SetProperty(ref _totalClicks, value);
    }

    public string LeftClicks
    {
        get => _leftClicks;
        set => SetProperty(ref _leftClicks, value);
    }

    public string RightClicks
    {
        get => _rightClicks;
        set => SetProperty(ref _rightClicks, value);
    }

    public string MiddleClicks
    {
        get => _middleClicks;
        set => SetProperty(ref _middleClicks, value);
    }

    public string SideBackClicks
    {
        get => _sideBackClicks;
        set => SetProperty(ref _sideBackClicks, value);
    }

    public string SideForwardClicks
    {
        get => _sideForwardClicks;
        set => SetProperty(ref _sideForwardClicks, value);
    }

    public bool IsSideClickRowVisible
    {
        get => _isSideClickRowVisible;
        set => SetProperty(ref _isSideClickRowVisible, value);
    }

    public string MouseDistance
    {
        get => _mouseDistance;
        set => SetProperty(ref _mouseDistance, value);
    }

    public string ScrollDistance
    {
        get => _scrollDistance;
        set => SetProperty(ref _scrollDistance, value);
    }

    public string PeakKPS
    {
        get => _peakKPS;
        set => SetProperty(ref _peakKPS, value);
    }

    public string PeakCPS
    {
        get => _peakCPS;
        set => SetProperty(ref _peakCPS, value);
    }

    public bool IsPeakPopupOpen
    {
        get => _isPeakPopupOpen;
        set => SetProperty(ref _isPeakPopupOpen, value);
    }

    public int SelectedRangeIndex
    {
        get => _selectedRangeIndex;
        set
        {
            if (SetProperty(ref _selectedRangeIndex, value))
            {
                App.CurrentApp?.TrackClick("chart_range", new Dictionary<string, object?>
                {
                    ["range"] = value switch
                    {
                        0 => "3d",
                        1 => "7d",
                        2 => "30d",
                        3 => "all",
                        _ => "unknown"
                    }
                });
                UpdateQuery();
                RequestRefresh(conditionsChanged: true);
            }
        }
    }

    public int SelectedMouseTotalsRangeIndex
    {
        get => _selectedMouseTotalsRangeIndex;
        set
        {
            if (SetProperty(ref _selectedMouseTotalsRangeIndex, value))
            {
                App.CurrentApp?.TrackClick("mouse_totals_range", new Dictionary<string, object?>
                {
                    ["range"] = value switch
                    {
                        0 => "week",
                        1 => "month",
                        2 => "year",
                        3 => "all",
                        _ => "unknown"
                    }
                });
                UpdateQuery();
                RequestRefresh(conditionsChanged: true);
            }
        }
    }

    public int SelectedMetricIndex
    {
        get => _selectedMetricIndex;
        set
        {
            if (SetProperty(ref _selectedMetricIndex, value))
            {
                App.CurrentApp?.TrackClick("chart_metric", new Dictionary<string, object?>
                {
                    ["metric"] = value switch
                    {
                        0 => "clicks",
                        1 => "key_presses",
                        2 => "mouse_distance",
                        3 => "scroll_distance",
                        _ => "unknown"
                    }
                });
                UpdateQuery();
                RequestRefresh(conditionsChanged: true);
            }
        }
    }

    public int SelectedChartStyleIndex
    {
        get => _selectedChartStyleIndex;
        set
        {
            if (SetProperty(ref _selectedChartStyleIndex, value))
            {
                App.CurrentApp?.TrackClick("chart_style", new Dictionary<string, object?>
                {
                    ["style"] = value == 0 ? "line" : "bar"
                });
                UpdateQuery();
                RequestRefresh(conditionsChanged: true);
            }
        }
    }

    public string HistorySummary
    {
        get => _historySummary;
        set => SetProperty(ref _historySummary, value);
    }

    public string HistoryLeftClicks
    {
        get => _historyLeftClicks;
        set => SetProperty(ref _historyLeftClicks, value);
    }

    public string HistoryMiddleClicks
    {
        get => _historyMiddleClicks;
        set => SetProperty(ref _historyMiddleClicks, value);
    }

    public string HistoryRightClicks
    {
        get => _historyRightClicks;
        set => SetProperty(ref _historyRightClicks, value);
    }

    public string HistorySideBackClicks
    {
        get => _historySideBackClicks;
        set => SetProperty(ref _historySideBackClicks, value);
    }

    public string HistorySideForwardClicks
    {
        get => _historySideForwardClicks;
        set => SetProperty(ref _historySideForwardClicks, value);
    }

    public string HistoryMouseDistance
    {
        get => _historyMouseDistance;
        set => SetProperty(ref _historyMouseDistance, value);
    }

    public string HistoryScrollDistance
    {
        get => _historyScrollDistance;
        set => SetProperty(ref _historyScrollDistance, value);
    }

    public ObservableCollection<KeyCountItem> Column1Items { get; } = new();
    public ObservableCollection<KeyCountItem> Column2Items { get; } = new();
    public ObservableCollection<KeyCountItem> Column3Items { get; } = new();
    
    public ObservableCollection<AppStatsItem> AppStatsItems { get; } = new();

    public ObservableCollection<ChartDataPoint> ChartData
    {
        get => _chartData;
        set => SetProperty(ref _chartData, value);
    }

    public ObservableCollection<ChartDataPoint>? LocalChartData
    {
        get => _localChartData;
        set => SetProperty(ref _localChartData, value);
    }

    public ICommand QuitCommand { get; }

    public StatsPopupViewModel()
    {
        QuitCommand = new RelayCommand(Quit);

        _refresher = new BackgroundStatsRefresher<PopupRefreshResult>(
            PostToUi,
            () => StatsManager.Instance.HistoryRevision,
            ComputeRefresh,
            ApplyRefresh);

        RequestRefresh(RefreshScopeFull, conditionsChanged: true);

        StatsManager.Instance.StatsChanged += OnStatsChanged;
    }

    private Task PostToUi(Action action)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }

        return _dispatcher.BeginInvoke(DispatcherPriority.Background, action).Task;
    }

    private void OnStatsChanged(StatsManager.StatsUpdateKind updateKind)
    {
        // Never block the notifying thread pool thread on the UI; merge bursts through the refresher.
        PostToUi(() =>
        {
            if (!_isActive)
            {
                _needsRefresh = true;
                return;
            }

            RequestRefresh(updateKind == StatsManager.StatsUpdateKind.MouseDistanceOnly
                ? RefreshScopeLight
                : RefreshScopeFull);
        });
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && _needsRefresh)
        {
            _needsRefresh = false;
            RequestRefresh(RefreshScopeFull);
        }
    }

    private void UpdateQuery()
    {
        Volatile.Write(ref _query, HistoryQuery.FromSelection(
            SelectedRangeIndex, SelectedMetricIndex, SelectedMouseTotalsRangeIndex));
    }

    private void RequestRefresh(int scope = RefreshScopeLight, bool conditionsChanged = false)
    {
        if (scope == RefreshScopeFull)
        {
            Interlocked.Increment(ref _fullRequestStamp);
        }

        _refresher.Request(conditionsChanged);
    }

    private PopupRefreshResult ComputeRefresh()
    {
        var manager = StatsManager.Instance;
        var query = _query;
        // The full scope stays pending until a result that includes the key breakdown and app
        // list is actually applied, so a discarded or deferred result cannot lose that work.
        var fullStamp = Volatile.Read(ref _fullRequestStamp);
        var isFull = fullStamp != Volatile.Read(ref _fullAppliedStamp);

        var result = new PopupRefreshResult
        {
            IsFull = isFull,
            FullStamp = fullStamp,
            Metric = query.Metric,
            Today = manager.GetCurrentStatsCopy(),
            HistoricalPeaks = manager.GetAllTimePeakRates(),
            History = manager.GetHistoryTrendSeries(query.Range, query.Metric),
            MouseTotals = manager.GetHistoryInputTotals(query.MouseTotalsRange)
        };

        if (isFull)
        {
            result.KeyBreakdown = manager.GetKeyPressBreakdownSorted();
            result.Apps = manager.GetAppStatsSorted(int.MaxValue);
        }

        return result;
    }

    private void ApplyRefresh(PopupRefreshResult result)
    {
        if (!_isActive)
        {
            // Hidden: keep the data unapplied and refresh once the window is shown again.
            _needsRefresh = true;
            return;
        }

        ApplyTodayStats(result);
        ApplyHistorySection(result);
        ApplyMouseTotals(result);

        if (result.IsFull)
        {
            ApplyKeyBreakdown(result.KeyBreakdown!);
            ApplyAppStats(result.Apps!);
            Interlocked.Exchange(ref _fullAppliedStamp, result.FullStamp);
        }
    }

    private void ApplyTodayStats(PopupRefreshResult result)
    {
        var stats = result.Today;
        var manager = StatsManager.Instance;

        KeyPresses = stats.KeyPresses.ToString("N0");
        TotalClicks = stats.TotalClicks.ToString("N0");
        LeftClicks = stats.LeftClicks.ToString("N0");
        RightClicks = stats.RightClicks.ToString("N0");
        MiddleClicks = stats.MiddleClicks.ToString("N0");
        SideBackClicks = stats.SideBackClicks.ToString("N0");
        SideForwardClicks = stats.SideForwardClicks.ToString("N0");
        IsSideClickRowVisible = (stats.SideBackClicks + stats.SideForwardClicks) > 0;
        MouseDistance = manager.FormatMouseDistance(stats.MouseDistance);
        ScrollDistance = stats.FormattedScrollDistance;
        PeakKPS = string.Format(
            KeyStats.Properties.Strings.Stats_HistoricalPeakFormat,
            Math.Round(stats.PeakKPS, MidpointRounding.AwayFromZero).ToString("N0"),
            Math.Round(result.HistoricalPeaks.PeakKPS, MidpointRounding.AwayFromZero).ToString("N0"));
        PeakCPS = string.Format(
            KeyStats.Properties.Strings.Stats_HistoricalPeakFormat,
            Math.Round(stats.PeakCPS, MidpointRounding.AwayFromZero).ToString("N0"),
            Math.Round(result.HistoricalPeaks.PeakCPS, MidpointRounding.AwayFromZero).ToString("N0"));
    }

    private void ApplyKeyBreakdown(List<(string Key, int Count)> items)
    {
        var manager = StatsManager.Instance;

        Column1Items.Clear();
        Column2Items.Clear();
        Column3Items.Clear();

        var limitedItems = items.Take(30).ToList();

        for (int i = 0; i < limitedItems.Count; i++)
        {
            var item = new KeyCountItem
            {
                Key = limitedItems[i].Key,
                Count = manager.FormatNumber(limitedItems[i].Count)
            };

            var columnIndex = i / 10;
            switch (columnIndex)
            {
                case 0:
                    Column1Items.Add(item);
                    break;
                case 1:
                    Column2Items.Add(item);
                    break;
                case 2:
                    Column3Items.Add(item);
                    break;
            }
        }
    }

    private void ApplyAppStats(List<AppStats> sortedApps)
    {
        var manager = StatsManager.Instance;

        AppStatsItems.Clear();

        foreach (var app in sortedApps)
        {
            AppStatsItems.Add(new AppStatsItem
            {
                Name = app.DisplayName,
                KeyPresses = manager.FormatNumber(app.KeyPresses),
                Clicks = manager.FormatNumber(app.TotalClicks),
                Icon = AppIconHelper.GetAppIcon(app.AppName, app.DisplayName)
            });
        }
    }

    private void ApplyHistorySection(PopupRefreshResult result)
    {
        var manager = StatsManager.Instance;
        var trendSeries = result.History;

        ChartData = new ObservableCollection<ChartDataPoint>(
            trendSeries.Display.Select(point => new ChartDataPoint { Date = point.Date, Value = point.Value }));
        LocalChartData = trendSeries.Local == null
            ? null
            : new ObservableCollection<ChartDataPoint>(
                trendSeries.Local.Select(point => new ChartDataPoint { Date = point.Date, Value = point.Value }));

        var total = trendSeries.Display.Sum(x => x.Value);
        var formatted = manager.FormatHistoryValue(result.Metric, total);
        HistorySummary = string.Format(KeyStats.Properties.Strings.History_TotalFormat, formatted);
    }

    private void ApplyMouseTotals(PopupRefreshResult result)
    {
        var manager = StatsManager.Instance;
        var totals = result.MouseTotals;
        HistoryLeftClicks = manager.FormatNumber(totals.LeftClicks);
        HistoryMiddleClicks = manager.FormatNumber(totals.MiddleClicks);
        HistoryRightClicks = manager.FormatNumber(totals.RightClicks);
        HistorySideBackClicks = manager.FormatNumber(totals.SideBackClicks);
        HistorySideForwardClicks = manager.FormatNumber(totals.SideForwardClicks);
        HistoryMouseDistance = manager.FormatMouseDistance(totals.MouseDistance);
        HistoryScrollDistance = manager.FormatCalibratedDistance(totals.ScrollDistance);
    }

    private static StatsManager.MouseTotalsRange MapMouseTotalsRange(int index) => index switch
    {
        0 => StatsManager.MouseTotalsRange.Week,
        1 => StatsManager.MouseTotalsRange.Month,
        2 => StatsManager.MouseTotalsRange.Year,
        3 => StatsManager.MouseTotalsRange.All,
        _ => StatsManager.MouseTotalsRange.All
    };

    private void Quit()
    {
        App.CurrentApp?.TrackClick("popup_quit");
        StatsManager.Instance.FlushPendingSave();
        InputMonitorService.Instance.StopMonitoring();
        Application.Current.Shutdown();
    }

    public void Cleanup()
    {
        StatsManager.Instance.StatsChanged -= OnStatsChanged;
        _refresher.Close();
    }

    /// <summary>
    /// Immutable query selection captured on the UI thread before a background computation starts.
    /// </summary>
    private sealed class HistoryQuery
    {
        private HistoryQuery(
            StatsManager.HistoryRange range,
            StatsManager.HistoryMetric metric,
            StatsManager.MouseTotalsRange mouseTotalsRange)
        {
            Range = range;
            Metric = metric;
            MouseTotalsRange = mouseTotalsRange;
        }

        public StatsManager.HistoryRange Range { get; }
        public StatsManager.HistoryMetric Metric { get; }
        public StatsManager.MouseTotalsRange MouseTotalsRange { get; }

        public static HistoryQuery FromSelection(int rangeIndex, int metricIndex, int mouseTotalsRangeIndex) =>
            new(
                rangeIndex switch
                {
                    0 => StatsManager.HistoryRange.ThreeDays,
                    1 => StatsManager.HistoryRange.Week,
                    2 => StatsManager.HistoryRange.Month,
                    3 => StatsManager.HistoryRange.All,
                    _ => StatsManager.HistoryRange.Week
                },
                metricIndex switch
                {
                    0 => StatsManager.HistoryMetric.Clicks,
                    1 => StatsManager.HistoryMetric.KeyPresses,
                    2 => StatsManager.HistoryMetric.MouseDistance,
                    3 => StatsManager.HistoryMetric.ScrollDistance,
                    _ => StatsManager.HistoryMetric.Clicks
                },
                MapMouseTotalsRange(mouseTotalsRangeIndex));
    }

    /// <summary>
    /// Plain data produced off the UI thread; formatting, icons and collections are applied on the UI thread.
    /// </summary>
    private sealed class PopupRefreshResult
    {
        public bool IsFull { get; set; }
        public int FullStamp { get; set; }
        public StatsManager.HistoryMetric Metric { get; set; }
        public DailyStats Today { get; set; } = new();
        public (double PeakKPS, double PeakCPS) HistoricalPeaks { get; set; }
        public StatsManager.HistoryTrendSeries History { get; set; } =
            new(new List<(DateTime Date, double Value)>(), null);
        public (
            int LeftClicks,
            int MiddleClicks,
            int RightClicks,
            int SideBackClicks,
            int SideForwardClicks,
            double MouseDistance,
            double ScrollDistance
        ) MouseTotals { get; set; }

        public List<(string Key, int Count)>? KeyBreakdown { get; set; }
        public List<AppStats>? Apps { get; set; }
    }
}
