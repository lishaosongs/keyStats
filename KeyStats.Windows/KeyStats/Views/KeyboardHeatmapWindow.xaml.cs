using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Models;
using KeyStats.Services;
using KeyStats.Views.Controls;

namespace KeyStats.Views;

public partial class KeyboardHeatmapWindow : Window
{
    private enum DaySlideDirection
    {
        Left,
        Right
    }

    private DateTime _selectedDate = DateTime.Today;
    private DateTime _startDate = DateTime.Today;
    private DateTime _endDate = DateTime.Today;
    private bool _isTransitionAnimating;
    private bool _suppressCalendarSelection;
    private bool _suppressAppSelection;
    private string? _selectedAppName;
    private bool _isReady;
    private bool _isClosed;
    private readonly BackgroundStatsRefresher<HeatmapRefreshResult> _refresher;
    private HeatmapQuery _query = new(
        StatsManager.KeyboardHeatmapRange.Today,
        StatsManager.KeyboardHeatmapMode.Sum,
        DateTime.Today,
        null);
    private DaySlideDirection? _pendingTransition;

    /// <summary>
    /// Immutable selection captured on the UI thread before a background computation starts.
    /// </summary>
    private sealed class HeatmapQuery
    {
        public HeatmapQuery(
            StatsManager.KeyboardHeatmapRange range,
            StatsManager.KeyboardHeatmapMode mode,
            DateTime date,
            string? appName)
        {
            Range = range;
            Mode = mode;
            Date = date;
            AppName = appName;
        }

        public StatsManager.KeyboardHeatmapRange Range { get; }
        public StatsManager.KeyboardHeatmapMode Mode { get; }
        public DateTime Date { get; }
        public string? AppName { get; }
    }

    /// <summary>
    /// Plain data produced off the UI thread; text, icons and heatmap drawing are applied on the UI thread.
    /// </summary>
    private sealed class HeatmapRefreshResult
    {
        public HeatmapQuery Query { get; set; } = null!;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public DateTime SelectedDate { get; set; }
        public string? SelectedAppName { get; set; }
        public Dictionary<string, double> KeyCounts { get; set; } = new(StringComparer.Ordinal);
        public double TotalKeyPresses { get; set; }
        public bool ShowDecimals { get; set; }
        public List<AppStats> DayApps { get; set; } = new();
    }

    private sealed class AppFilterOption
    {
        public string? AppName { get; }
        public string Label { get; }
        public ImageSource? Icon { get; }
        public bool HasIcon => Icon != null;

        public AppFilterOption(string? appName, string label, ImageSource? icon = null)
        {
            AppName = appName;
            Label = label;
            Icon = icon;
        }
    }
    private StatsManager.KeyboardHeatmapRange _selectedRange = StatsManager.KeyboardHeatmapRange.Today;
    private StatsManager.KeyboardHeatmapMode _selectedMode = StatsManager.KeyboardHeatmapMode.Sum;

    public KeyboardHeatmapWindow()
    {
        _refresher = new BackgroundStatsRefresher<HeatmapRefreshResult>(
            PostToUi,
            () => StatsManager.Instance.HistoryRevision,
            ComputeRefresh,
            ApplyRefresh);

        InitializeComponent();

        var settings = StatsManager.Instance.Settings;
        if (settings.KeyboardHeatmapWindowWidth is double width &&
            width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
        {
            Width = Math.Max(MinWidth, width);
        }

        if (settings.KeyboardHeatmapWindowHeight is double height &&
            height > 0 && !double.IsNaN(height) && !double.IsInfinity(height))
        {
            Height = Math.Max(MinHeight, height);
        }

        ApplyLocalizedText();
        RequestRefresh(conditionsChanged: true);
        UpdateAppearance();

        Loaded += OnLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
        StatsManager.Instance.StatsUpdateRequested += OnStatsUpdateRequested;
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        _isReady = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowBackdrop();
        RequestRefresh();
        App.CurrentApp?.TrackPageView("keyboard_heatmap");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _refresher.Close();
        SaveWindowSize();
        StatsManager.Instance.StatsUpdateRequested -= OnStatsUpdateRequested;
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && WindowState == WindowState.Normal)
        {
            SaveWindowSize();
        }
    }

    private void SaveWindowSize()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var settings = StatsManager.Instance.Settings;
        settings.KeyboardHeatmapWindowWidth = Width;
        settings.KeyboardHeatmapWindowHeight = Height;
        StatsManager.Instance.SaveSettings();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        RequestRefresh();
    }

    private void OnThemeChanged()
    {
        PostToUi(() =>
        {
            UpdateAppearance();
            RequestRefresh();
        });
    }

    private void OnStatsUpdateRequested()
    {
        // Notifications arrive on a thread pool thread; post instead of blocking it on the UI.
        PostToUi(() =>
        {
            if (!_isTransitionAnimating && !AppFilterComboBox.IsDropDownOpen)
            {
                RequestRefresh();
            }
        });
    }

    private Task PostToUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.BeginInvoke(DispatcherPriority.Background, action).Task;
    }

    private void RequestRefresh(bool conditionsChanged = false)
    {
        if (_isClosed)
        {
            return;
        }

        _query = new HeatmapQuery(_selectedRange, _selectedMode, _selectedDate, _selectedAppName);
        _refresher.Request(conditionsChanged);
    }

    private HeatmapRefreshResult ComputeRefresh()
    {
        var manager = StatsManager.Instance;
        var query = _query;
        var bounds = manager.GetKeyboardHeatmapDateBounds();
        var startDate = bounds.Start.Date;
        var endDate = bounds.End.Date;
        var selectedDate = ClampDate(query.Date, startDate, endDate);

        var result = new HeatmapRefreshResult
        {
            Query = query,
            StartDate = startDate,
            EndDate = endDate,
            SelectedDate = selectedDate,
            ShowDecimals = query.Range != StatsManager.KeyboardHeatmapRange.Today &&
                           query.Mode == StatsManager.KeyboardHeatmapMode.Average
        };

        if (query.Range == StatsManager.KeyboardHeatmapRange.Today)
        {
            var dayData = manager.GetKeyboardHeatmapDay(selectedDate, query.AppName);
            result.SelectedAppName = dayData.SelectedAppName;
            result.DayApps = dayData.Apps;
            result.KeyCounts = dayData.KeyCounts
                .Where(kvp => KeyboardHeatmapControl.SupportedKeyIds.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => (double)kvp.Value, StringComparer.Ordinal);
            result.TotalKeyPresses = dayData.TotalKeyPresses;
        }
        else
        {
            var summary = manager.GetKeyboardHeatmapSummary(query.Range, query.Mode);
            result.SelectedAppName = null;
            result.KeyCounts = summary.KeyCounts
                .Where(kvp => KeyboardHeatmapControl.SupportedKeyIds.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            result.TotalKeyPresses = summary.TotalKeyPresses;
        }

        return result;
    }

    private void ApplyRefresh(HeatmapRefreshResult result)
    {
        var query = result.Query;
        var snapshot = _pendingTransition == null ? null : CaptureCurrentLayerSnapshot();

        _startDate = result.StartDate;
        _endDate = result.EndDate;
        _selectedDate = result.SelectedDate;

        if (query.Range == StatsManager.KeyboardHeatmapRange.Today)
        {
            _selectedAppName = result.SelectedAppName;
            UpdateAppFilter(result);
        }
        else
        {
            _selectedAppName = null;
            ClearAppFilter();
        }

        KeyboardHeatmapView.Apply(result.KeyCounts, result.ShowDecimals);

        var activeKeys = result.KeyCounts.Values.Count(value => value > 0);
        var totalFormatted = FormatHeatmapValue(result.TotalKeyPresses, result.ShowDecimals);
        var activeFormatted = activeKeys.ToString("N0", CultureInfo.CurrentCulture);
        var summaryFormat = result.ShowDecimals
            ? KeyStats.Properties.Strings.Heatmap_AverageSummaryFormat
            : KeyStats.Properties.Strings.Heatmap_SummaryFormat;
        SummaryText.Text = string.Format(CultureInfo.CurrentCulture, summaryFormat, totalFormatted, activeFormatted);
        DateText.Text = DisplayDateString(_selectedDate);

        var hasActivity = query.Range == StatsManager.KeyboardHeatmapRange.Today
            ? result.KeyCounts.Values.Any(value => value > 0)
            : result.TotalKeyPresses > 0;
        EmptyBadgeText.Text = query.Range == StatsManager.KeyboardHeatmapRange.Today && _selectedAppName != null
            ? KeyStats.Properties.Strings.Heatmap_NoAppData
            : KeyStats.Properties.Strings.Heatmap_NoData;
        EmptyOverlay.Visibility = hasActivity ? Visibility.Collapsed : Visibility.Visible;
        EmptyBadge.Visibility = hasActivity ? Visibility.Collapsed : Visibility.Visible;

        UpdateNavigationState();
        UpdateDatePickerState();

        if (_pendingTransition is { } direction)
        {
            _pendingTransition = null;
            AnimateDayTransition(direction, snapshot);
        }
    }

    private void ApplyLocalizedText()
    {
        Title = KeyStats.Properties.Strings.Heatmap_WindowTitle;
        TitleText.Text = KeyStats.Properties.Strings.Heatmap_HeaderTitle;
        SubtitleText.Text = KeyStats.Properties.Strings.Heatmap_HeaderSubtitle;
        PrevDayButton.Content = KeyStats.Properties.Strings.Heatmap_PrevDay;
        NextDayButton.Content = KeyStats.Properties.Strings.Heatmap_NextDay;
        BackToTodayButton.Content = KeyStats.Properties.Strings.Heatmap_BackToToday;
        EmptyBadgeText.Text = KeyStats.Properties.Strings.Heatmap_NoData;
        DatePickerButton.ToolTip = KeyStats.Properties.Strings.Heatmap_DatePickerTooltip;
    }

    private void HeatmapRange_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady || sender is not RadioButton radio || radio.Tag is not string tag ||
            !Enum.TryParse<StatsManager.KeyboardHeatmapRange>(tag, out var range))
        {
            return;
        }

        _selectedRange = range;
        if (range == StatsManager.KeyboardHeatmapRange.Today)
        {
            _selectedDate = DateTime.Today;
        }
        else
        {
            _selectedAppName = null;
        }

        DatePickerPopup.IsOpen = false;
        _pendingTransition = null;
        App.CurrentApp?.TrackClick("keyboard_heatmap_range", new Dictionary<string, object?>
        {
            ["range"] = tag
        });
        RequestRefresh(conditionsChanged: true);
    }

    private void HeatmapMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady || sender is not RadioButton radio || radio.Tag is not string tag ||
            !Enum.TryParse<StatsManager.KeyboardHeatmapMode>(tag, out var mode))
        {
            return;
        }

        _selectedMode = mode;
        _pendingTransition = null;
        App.CurrentApp?.TrackClick("keyboard_heatmap_mode", new Dictionary<string, object?>
        {
            ["mode"] = tag
        });
        RequestRefresh(conditionsChanged: true);
    }

    private void UpdateAppearance()
    {
        ApplyWindowBackdrop();

        var isDark = ThemeManager.Instance.IsDarkTheme;
        var badgeColor = isDark
            ? Color.FromArgb(220, 45, 45, 45)
            : Color.FromArgb(220, 248, 248, 248);
        EmptyBadge.Background = new SolidColorBrush(badgeColor);
        KeyboardHeatmapView.InvalidateVisual();
    }

    private void ApplyWindowBackdrop()
    {
        WindowBackdropHelper.Apply(this, NativeInterop.DwmSystemBackdropType.TransientWindow);
    }

    private void UpdateAppFilter(HeatmapRefreshResult result)
    {
        var options = new List<AppFilterOption>
        {
            new(null, KeyStats.Properties.Strings.Heatmap_AllApps)
        };
        options.AddRange(result.DayApps.Select(app => new AppFilterOption(
            app.AppName,
            string.Format(CultureInfo.CurrentCulture, KeyStats.Properties.Strings.Heatmap_AppOptionFormat,
                string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppName : app.DisplayName,
                app.KeyPresses.ToString("N0", CultureInfo.CurrentCulture)),
            AppIconHelper.GetAppIcon(app.AppName, app.DisplayName))));

        _suppressAppSelection = true;
        try
        {
            AppFilterComboBox.ItemsSource = options;
            AppFilterComboBox.SelectedItem = options.First(option =>
                string.Equals(option.AppName, _selectedAppName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressAppSelection = false;
        }
    }

    private void ClearAppFilter()
    {
        _suppressAppSelection = true;
        try
        {
            AppFilterComboBox.ItemsSource = null;
            AppFilterComboBox.SelectedItem = null;
        }
        finally
        {
            _suppressAppSelection = false;
        }
    }

    private void AppFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSelection || _selectedRange != StatsManager.KeyboardHeatmapRange.Today ||
            AppFilterComboBox.SelectedItem is not AppFilterOption option) return;

        _selectedAppName = option.AppName;
        App.CurrentApp?.TrackClick("keyboard_heatmap_app_filter", new Dictionary<string, object?>
        {
            ["scope"] = _selectedAppName == null ? "all" : "app"
        });
        RequestRefresh(conditionsChanged: true);
    }

    private void AppFilterComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (!_suppressAppSelection) RequestRefresh();
    }

    private static string FormatHeatmapValue(double value, bool showDecimals)
    {
        return value.ToString(showDecimals ? "N1" : "N0", CultureInfo.CurrentCulture);
    }

    private string DisplayDateString(DateTime date)
    {
        if (date.Year == DateTime.Today.Year)
        {
            return date.ToString("M", CultureInfo.CurrentCulture);
        }

        return date.ToString("d", CultureInfo.CurrentCulture);
    }

    private DateTime ClampDate(DateTime date) => ClampDate(date, _startDate, _endDate);

    private static DateTime ClampDate(DateTime date, DateTime start, DateTime end)
    {
        var normalized = date.Date;
        if (normalized < start)
        {
            return start;
        }

        if (normalized > end)
        {
            return end;
        }

        return normalized;
    }

    private void UpdateNavigationState()
    {
        var isDaily = _selectedRange == StatsManager.KeyboardHeatmapRange.Today;
        var today = DateTime.Today;
        AppFilterPanel.Visibility = isDaily ? Visibility.Visible : Visibility.Collapsed;
        PrevDayButton.Visibility = isDaily ? Visibility.Visible : Visibility.Collapsed;
        NextDayButton.Visibility = isDaily ? Visibility.Visible : Visibility.Collapsed;
        DatePickerButton.Visibility = isDaily ? Visibility.Visible : Visibility.Collapsed;
        BackToTodayButton.Visibility = isDaily && _selectedDate != today
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrevDayButton.IsEnabled = isDaily && _selectedDate > _startDate;
        NextDayButton.IsEnabled = isDaily && _selectedDate < _endDate;
    }

    private void UpdateDatePickerState()
    {
        var minDate = _startDate.Date;
        var maxDate = _endDate.Date;
        if (maxDate < minDate)
        {
            maxDate = minDate;
        }

        var displayStart = new DateTime(minDate.Year, minDate.Month, 1);
        var displayEnd = new DateTime(maxDate.Year, maxDate.Month, DateTime.DaysInMonth(maxDate.Year, maxDate.Month));

        DatePickerCalendar.DisplayDateStart = displayStart;
        DatePickerCalendar.DisplayDateEnd = displayEnd;
        DatePickerCalendar.BlackoutDates.Clear();

        if (minDate > DateTime.MinValue.Date)
        {
            DatePickerCalendar.BlackoutDates.Add(new CalendarDateRange(DateTime.MinValue.Date, minDate.AddDays(-1)));
        }

        if (maxDate < DateTime.MaxValue.Date)
        {
            DatePickerCalendar.BlackoutDates.Add(new CalendarDateRange(maxDate.AddDays(1), DateTime.MaxValue.Date));
        }

        if (!DatePickerPopup.IsOpen)
        {
            _suppressCalendarSelection = true;
            var clamped = ClampDate(_selectedDate);
            DatePickerCalendar.DisplayDate = clamped;
            DatePickerCalendar.SelectedDate = clamped;
            _suppressCalendarSelection = false;
        }

        DatePickerButton.IsEnabled = minDate <= maxDate;
    }

    private void OpenDatePicker_Click(object sender, RoutedEventArgs e)
    {
        if (DatePickerPopup.IsOpen)
        {
            DatePickerPopup.IsOpen = false;
            return;
        }

        UpdateDatePickerState();
        DatePickerPopup.IsOpen = true;
        DatePickerCalendar.Focus();
    }

    private void DatePickerPopup_Closed(object sender, EventArgs e)
    {
        DatePickerButton.Focus();
    }

    private void DatePickerCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressCalendarSelection)
        {
            return;
        }

        if (DatePickerCalendar.SelectedDate is not DateTime selected)
        {
            return;
        }

        DatePickerPopup.IsOpen = false;
        TransitionToDate(selected.Date);
    }

    private void ShowPreviousDay_Click(object sender, RoutedEventArgs e)
    {
        var target = _selectedDate.AddDays(-1);
        if (target < _startDate)
        {
            return;
        }

        TransitionToDate(target);
    }

    private void ShowNextDay_Click(object sender, RoutedEventArgs e)
    {
        var target = _selectedDate.AddDays(1);
        if (target > _endDate)
        {
            return;
        }

        TransitionToDate(target);
    }

    private void BackToToday_Click(object sender, RoutedEventArgs e)
    {
        TransitionToDate(DateTime.Today);
    }

    private void HeatmapAnimationHost_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_selectedRange != StatsManager.KeyboardHeatmapRange.Today || _isTransitionAnimating || e.Delta == 0)
        {
            return;
        }

        var target = _selectedDate.AddDays(e.Delta > 0 ? -1 : 1);
        if (target < _startDate || target > _endDate)
        {
            return;
        }

        e.Handled = true;
        TransitionToDate(target);
    }

    private void TransitionToDate(DateTime targetDate)
    {
        if (_selectedRange != StatsManager.KeyboardHeatmapRange.Today)
        {
            return;
        }

        DatePickerPopup.IsOpen = false;

        var target = ClampDate(targetDate);
        if (target == _selectedDate)
        {
            return;
        }

        var direction = target < _selectedDate ? DaySlideDirection.Left : DaySlideDirection.Right;

        _selectedDate = target;
        // The outgoing snapshot is taken in ApplyRefresh, right before the new day is drawn,
        // so the slide animation keeps the same ordering as the previous synchronous refresh.
        _pendingTransition = direction;
        RequestRefresh(conditionsChanged: true);
    }

    private BitmapSource? CaptureCurrentLayerSnapshot()
    {
        CurrentHeatmapLayer.UpdateLayout();
        if (CurrentHeatmapLayer.ActualWidth <= 0 || CurrentHeatmapLayer.ActualHeight <= 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelWidth = Math.Max(1, (int)Math.Round(CurrentHeatmapLayer.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Round(CurrentHeatmapLayer.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(CurrentHeatmapLayer);
        bitmap.Freeze();
        return bitmap;
    }

    private void AnimateDayTransition(DaySlideDirection direction, BitmapSource? previousSnapshot)
    {
        if (_isTransitionAnimating)
        {
            return;
        }

        _isTransitionAnimating = true;
        var duration = TimeSpan.FromMilliseconds(160);
        var slideDistance = Math.Max(260, HeatmapAnimationHost.ActualWidth * 0.88);
        var incomingOffset = direction == DaySlideDirection.Left ? -slideDistance : slideDistance;
        var outgoingOffset = -incomingOffset;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        CurrentLayerTransform.X = incomingOffset;
        CurrentHeatmapLayer.Opacity = 0;

        if (previousSnapshot != null)
        {
            PreviousSnapshotImage.Source = previousSnapshot;
            PreviousSnapshotImage.Visibility = Visibility.Visible;
            PreviousLayerTransform.X = 0;
            PreviousSnapshotImage.Opacity = 1;
        }
        else
        {
            PreviousSnapshotImage.Visibility = Visibility.Collapsed;
            PreviousSnapshotImage.Source = null;
        }

        var incomingX = new DoubleAnimation(incomingOffset, 0, duration) { EasingFunction = easing };
        var incomingOpacity = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };

        incomingX.Completed += (_, _) =>
        {
            CurrentLayerTransform.BeginAnimation(TranslateTransform.XProperty, null);
            CurrentHeatmapLayer.BeginAnimation(UIElement.OpacityProperty, null);
            PreviousLayerTransform.BeginAnimation(TranslateTransform.XProperty, null);
            PreviousSnapshotImage.BeginAnimation(UIElement.OpacityProperty, null);

            PreviousSnapshotImage.Visibility = Visibility.Collapsed;
            PreviousSnapshotImage.Source = null;
            PreviousLayerTransform.X = 0;
            CurrentLayerTransform.X = 0;
            CurrentHeatmapLayer.Opacity = 1;
            _isTransitionAnimating = false;
        };

        CurrentLayerTransform.BeginAnimation(TranslateTransform.XProperty, incomingX);
        CurrentHeatmapLayer.BeginAnimation(UIElement.OpacityProperty, incomingOpacity);

        if (previousSnapshot != null)
        {
            var outgoingX = new DoubleAnimation(0, outgoingOffset, duration) { EasingFunction = easing };
            var outgoingOpacity = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
            PreviousLayerTransform.BeginAnimation(TranslateTransform.XProperty, outgoingX);
            PreviousSnapshotImage.BeginAnimation(UIElement.OpacityProperty, outgoingOpacity);
        }
    }
}
