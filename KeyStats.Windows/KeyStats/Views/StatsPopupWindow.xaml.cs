using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Services;
using KeyStats.ViewModels;

namespace KeyStats.Views;

public partial class StatsPopupWindow : Window
{
    public enum DisplayMode
    {
        TrayPopup,
        Windowed
    }

    private const double DefaultWindowModeWidth = 520;
    private const double DefaultWindowModeHeight = 760;
    private const double TrayPopupWidth = 960;
    private readonly StatsPopupViewModel _viewModel;
    private readonly bool _isWindowMode;
    private bool _isFullyLoaded;
    private bool _allowClose;
    private bool _suppressStatePersistence;
    private bool _isTrayBackdropEnabled;
    private bool _isHiding;
    private System.Drawing.Point? _anchorPoint;
    private readonly DispatcherTimer _windowStateSaveTimer;

    public bool IsHiding => _isHiding;

    public StatsPopupWindow(DisplayMode displayMode, System.Drawing.Point? anchorPoint = null)
    {
        Console.WriteLine("StatsPopupWindow constructor...");
        InitializeComponent();
        Console.WriteLine("InitializeComponent done");

        _viewModel = (StatsPopupViewModel)DataContext;
        _isWindowMode = displayMode == DisplayMode.Windowed;
        _anchorPoint = anchorPoint;
        _windowStateSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _windowStateSaveTimer.Tick += WindowStateSaveTimer_Tick;

        ConfigureWindowForMode();
        if (!_isWindowMode)
        {
            _viewModel.SetActive(false);
        }
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
        LocationChanged += OnWindowBoundsChanged;
        SizeChanged += OnWindowBoundsChanged;
        SourceInitialized += OnSourceInitialized;

        ThemeManager.Instance.ThemeChanged += OnThemeChanged;

        Console.WriteLine("StatsPopupWindow constructor done");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_isWindowMode)
        {
            ApplyWindowModeBackdrop();
            return;
        }

        ApplyTrayPopupBackdrop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isWindowMode)
        {
            RestoreWindowModeBounds();
            App.CurrentApp?.TrackPageView("stats_popup");
        }

        _isFullyLoaded = true;
    }

    private System.Windows.Vector GetSlideOffset()
    {
        var screen = Screen.FromPoint(_anchorPoint ?? System.Windows.Forms.Control.MousePosition);
        var workingArea = screen.WorkingArea;
        var bounds = screen.Bounds;
        const double distance = 10;

        if (workingArea.Top > bounds.Top) return new System.Windows.Vector(0, -distance);
        if (workingArea.Right < bounds.Right) return new System.Windows.Vector(distance, 0);
        if (workingArea.Left > bounds.Left) return new System.Windows.Vector(-distance, 0);
        return new System.Windows.Vector(0, distance);
    }

    private void SlideIn()
    {
        _isHiding = false;
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 170 : 0);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = Opacity,
            To = 1,
            Duration = duration,
            EasingFunction = easing
        });
        WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation
        {
            From = WindowTransform.X,
            To = 0,
            Duration = duration,
            EasingFunction = easing
        });
        WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = WindowTransform.Y,
            To = 0,
            Duration = duration,
            EasingFunction = easing
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _allowClose = true;
        _isHiding = false;
        _windowStateSaveTimer.Stop();

        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;

        _viewModel.Cleanup();
    }

    private void OnThemeChanged()
    {
        if (_isWindowMode)
        {
            ApplyWindowModeBackdrop();
        }
        else
        {
            ApplyTrayPopupBackdrop();
        }
    }

    private void ApplyWindowModeBackdrop()
    {
        WindowBackdropHelper.Apply(this, NativeInterop.DwmSystemBackdropType.TransientWindow);

        if (FindName("RootBorder") is System.Windows.Controls.Border rootBorder)
        {
            rootBorder.SetResourceReference(
                System.Windows.Controls.Border.BackgroundProperty,
                "WindowSurfaceBrush");
            rootBorder.BorderThickness = new Thickness(0);
        }
    }

    private void ApplyTrayPopupBackdrop()
    {
        _isTrayBackdropEnabled = WindowBackdropHelper.Apply(
            this,
            NativeInterop.DwmSystemBackdropType.TransientWindow);
        ApplyTrayPopupSurface();
    }

    private void ApplyTrayPopupSurface()
    {
        if (FindName("RootBorder") is not System.Windows.Controls.Border rootBorder)
        {
            return;
        }

        rootBorder.BorderThickness = new Thickness(1);
        rootBorder.SetResourceReference(
            System.Windows.Controls.Border.BorderBrushProperty,
            "TrayPopupBorderBrush");

        rootBorder.SetResourceReference(
            System.Windows.Controls.Border.BackgroundProperty,
            _isTrayBackdropEnabled ? "TrayBackdropTintBrush" : "SurfaceBrush");
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_isWindowMode)
        {
            return;
        }

        Console.WriteLine($"Window_Deactivated called, _isFullyLoaded={_isFullyLoaded}");
        if (_isFullyLoaded)
        {
            SlideOut();
        }
    }

    private void OpenAppStats_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentApp?.TrackClick("open_app_stats");
        App.CurrentApp?.ShowAppStatsWindow();
    }

    private void OpenKeyboardHeatmap_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentApp?.TrackClick("open_keyboard_heatmap");
        App.CurrentApp?.ShowKeyboardHeatmapWindow();
    }

    private void OpenKeyHistory_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentApp?.TrackClick("open_key_history");
        App.CurrentApp?.ShowKeyHistoryWindow();
    }
    
    private void SlideOut()
    {
        if (_isWindowMode || !IsVisible || _isHiding || _allowClose)
        {
            return;
        }

        _isHiding = true;
        _viewModel.SetActive(false);
        RootBorder.IsHitTestVisible = false;

        // IsOpen=false precedes HWND teardown; wait for Closed before fading the owner.
        if (PresentationSource.FromVisual(PeakPopup.Child) != null)
        {
            _viewModel.IsPeakPopupOpen = false;
            return;
        }

        StartSlideOutAnimation();
    }

    private void PeakPopup_Closed(object sender, EventArgs e)
    {
        if (_isHiding && !_allowClose)
        {
            StartSlideOutAnimation();
        }
    }

    private void StartSlideOutAnimation()
    {
        var offset = GetSlideOffset();
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 110 : 0);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnimation = new DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = duration,
            EasingFunction = easing
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (_isHiding && !_allowClose)
            {
                Hide();
                _isHiding = false;
            }
        };

        BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation
        {
            From = WindowTransform.X,
            To = offset.X,
            Duration = duration,
            EasingFunction = easing
        });
        WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = WindowTransform.Y,
            To = offset.Y,
            Duration = duration,
            EasingFunction = easing
        });
    }

    private void PositionNearTray()
    {
        // Get current mouse position (prefer the click-time anchor to avoid async delay drift)
        var mousePos = _anchorPoint ?? System.Windows.Forms.Control.MousePosition;
        var mouseX = mousePos.X;
        var mouseY = mousePos.Y;

        // Get the primary screen info
        var screen = Screen.FromPoint(new System.Drawing.Point(mouseX, mouseY));
        if (screen == null) screen = Screen.PrimaryScreen;
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;

        // DPI scale factors (handle X/Y independently for non-uniform DPI)
        var transformToDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        var dpiScaleX = transformToDevice?.M11 ?? 1.0;
        var dpiScaleY = transformToDevice?.M22 ?? dpiScaleX;

        // Determine taskbar position
        bool taskbarAtBottom = workingArea.Bottom < screenBounds.Bottom;
        bool taskbarAtTop = workingArea.Top > screenBounds.Top;
        bool taskbarAtRight = workingArea.Right < screenBounds.Right;
        bool taskbarAtLeft = workingArea.Left > screenBounds.Left;

        // Reserve space for the system tray area (avoid covering icons)
        const int trayAreaWidth = 250; // System tray area width (right side)
        const int spacing = 10; // Minimum gap between window and mouse/taskbar

        Width = Math.Min(TrayPopupWidth, Math.Max(1, (workingArea.Width - spacing * 2) / dpiScaleX));
        MaxHeight = Math.Max(200, (workingArea.Height - spacing * 2) / dpiScaleY);
        RootBorder.Measure(new System.Windows.Size(Width, MaxHeight));
        UpdateLayout();

        var windowWidthDip = Width;
        var windowHeightDip = Math.Min(RootBorder.DesiredSize.Height, MaxHeight);

        var windowWidth = windowWidthDip * dpiScaleX;
        var windowHeight = windowHeightDip * dpiScaleY;

        double left, top;

        if (taskbarAtBottom)
        {
            // Taskbar at bottom: show window above the mouse
            left = mouseX - windowWidth / 2;

            // If the mouse is on the right side of the screen (system tray area), position the window to the left to avoid covering icons
            if (mouseX > screenBounds.Right - trayAreaWidth)
            {
                // Position the window to the right side of the screen, but leave space for the system tray area
                left = screenBounds.Right - windowWidth - trayAreaWidth - spacing;
            }

            // Display the window just above the mouse with a tiny gap
            top = mouseY - windowHeight - spacing;

            // Ensure the window stays fully within the working area
            if (top + windowHeight > workingArea.Bottom - spacing)
            {
                top = workingArea.Bottom - windowHeight - spacing;
            }
        }
        else if (taskbarAtTop)
        {
            // Taskbar at top: show window below the mouse
            left = mouseX - windowWidth / 2;
            top = workingArea.Top + 10;
        }
        else if (taskbarAtRight)
        {
            // Taskbar on the right: show window to the left of the mouse
            // If the mouse is in the right-side taskbar region (likely a tray-icon click), position the window further left
            left = workingArea.Right - windowWidth - trayAreaWidth - 10;
            top = mouseY - windowHeight / 2;
        }
        else if (taskbarAtLeft)
        {
            // Taskbar on the left: show window to the right of the mouse
            left = workingArea.Left + 10;
            top = mouseY - windowHeight / 2;
        }
        else
        {
            // Default: show window near the mouse
            left = mouseX - windowWidth / 2;
            top = mouseY - windowHeight / 2;
        }

        // Ensure the window is fully within the visible screen area
        if (left < workingArea.Left)
            left = workingArea.Left + 10;
        if (left + windowWidth > workingArea.Right)
            left = workingArea.Right - windowWidth - 10;
        if (top < workingArea.Top)
            top = workingArea.Top + 10;

        // Ensure the window bottom does not extend into the taskbar area, leaving a small gap
        if (top + windowHeight > workingArea.Bottom - spacing)
            top = workingArea.Bottom - windowHeight - spacing;

        // Keep the HWND aligned to physical pixels so text does not land on a fractional device pixel at 125%/150% DPI.
        Left = Math.Round(left) / dpiScaleX;
        Top = Math.Round(top) / dpiScaleY;
    }

    private void ConfigurePopupSections()
    {
        PopupSectionsGrid.RowDefinitions.Clear();
        PopupSectionsGrid.ColumnDefinitions.Clear();
        PopupSectionsGrid.Children.Clear();

        var sections = new System.Windows.FrameworkElement[]
        {
            TodaySection,
            KeyBreakdownSection,
            ActiveAppsSection,
            HistorySection,
            MouseTotalsSection
        };
        foreach (var section in sections)
        {
            section.Margin = _isWindowMode ? new Thickness(0) : new Thickness(4);
        }

        if (!_isWindowMode)
        {
            ActiveAppsSection.SetBinding(
                System.Windows.FrameworkElement.HeightProperty,
                new System.Windows.Data.Binding(nameof(ActualHeight))
                {
                    Source = KeyBreakdownSection,
                    Mode = System.Windows.Data.BindingMode.OneWay
                });
        }

        if (_isWindowMode)
        {
            PopupSectionsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            foreach (var section in sections)
            {
                PopupSectionsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = GridLength.Auto
                });
                System.Windows.Controls.Grid.SetRow(section, PopupSectionsGrid.RowDefinitions.Count - 1);
                PopupSectionsGrid.Children.Add(section);
            }

            return;
        }

        for (var column = 0; column < 3; column++)
        {
            PopupSectionsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        PopupSectionsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        PopupSectionsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        System.Windows.Controls.Grid.SetRow(TodaySection, 0);
        System.Windows.Controls.Grid.SetColumn(TodaySection, 0);
        System.Windows.Controls.Grid.SetRow(KeyBreakdownSection, 0);
        System.Windows.Controls.Grid.SetColumn(KeyBreakdownSection, 1);
        System.Windows.Controls.Grid.SetRow(ActiveAppsSection, 0);
        System.Windows.Controls.Grid.SetColumn(ActiveAppsSection, 2);
        System.Windows.Controls.Grid.SetRow(MouseTotalsSection, 1);
        System.Windows.Controls.Grid.SetColumn(MouseTotalsSection, 0);
        System.Windows.Controls.Grid.SetRow(HistorySection, 1);
        System.Windows.Controls.Grid.SetColumn(HistorySection, 1);
        System.Windows.Controls.Grid.SetColumnSpan(HistorySection, 2);

        foreach (var section in sections)
        {
            PopupSectionsGrid.Children.Add(section);
        }
    }

    private void ConfigureWindowForMode()
    {
        ConfigurePopupSections();

        if (FindName("RootBorder") is System.Windows.Controls.Border rootBorder)
        {
            rootBorder.CornerRadius = _isWindowMode ? new CornerRadius(0) : new CornerRadius(8);
        }

        if (_isWindowMode)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = true;
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            SizeToContent = SizeToContent.Manual;
            Width = DefaultWindowModeWidth;
            Height = DefaultWindowModeHeight;
            MinWidth = 420;
            MinHeight = 560;
            MaxHeight = double.PositiveInfinity;
            Opacity = 1;
            WindowStartupLocation = WindowStartupLocation.Manual;
            return;
        }

        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isWindowMode)
        {
            PersistWindowModeBounds();
            Hide();
        }
        else
        {
            SlideOut();
        }
    }

    private void OnWindowBoundsChanged(object? sender, EventArgs e)
    {
        if (!_isWindowMode || !_isFullyLoaded || _suppressStatePersistence || WindowState != WindowState.Normal)
        {
            return;
        }

        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    private void OnWindowBoundsChanged(object? sender, SizeChangedEventArgs e)
    {
        OnWindowBoundsChanged(sender, EventArgs.Empty);
    }

    private void WindowStateSaveTimer_Tick(object? sender, EventArgs e)
    {
        _windowStateSaveTimer.Stop();
        PersistWindowModeBounds();
    }

    public void ShowWindow(System.Drawing.Point? anchorPoint = null)
    {
        if (_isWindowMode)
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            return;
        }

        if (IsVisible && !_isHiding)
        {
            Activate();
            return;
        }

        _anchorPoint = anchorPoint ?? System.Windows.Forms.Control.MousePosition;
        _viewModel.SetActive(true);
        RootBorder.IsHitTestVisible = true;
        if (!IsVisible)
        {
            BeginAnimation(UIElement.OpacityProperty, null);
            Opacity = 0;
            WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            WindowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            var offset = GetSlideOffset();
            WindowTransform.X = offset.X;
            WindowTransform.Y = offset.Y;
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            PositionNearTray();
            Show();
        }

        SlideIn();
        Activate();
        App.CurrentApp?.TrackPageView("stats_popup");
    }

    public void CloseWindow(bool force)
    {
        _allowClose = force;
        Close();
    }

    public void PrepareForExit()
    {
        _allowClose = true;
    }

    private void RestoreWindowModeBounds()
    {
        if (!_isWindowMode)
        {
            return;
        }

        var settings = StatsManager.Instance.Settings;
        var width = settings.MainWindowWidth ?? DefaultWindowModeWidth;
        var height = settings.MainWindowHeight ?? DefaultWindowModeHeight;

        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);

        var left = settings.MainWindowLeft;
        var top = settings.MainWindowTop;
        if (!left.HasValue || !top.HasValue)
        {
            CenterWindowOnPrimaryScreen();
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        var bounds = new Rect(left.Value, top.Value, Width, Height);
        var visibleBounds = GetVisibleWorkingArea();
        if (!visibleBounds.HasValue)
        {
            Left = left.Value;
            Top = top.Value;
            return;
        }

        var clamped = ClampToVisibleArea(bounds, visibleBounds.Value);
        _suppressStatePersistence = true;
        try
        {
            Left = clamped.Left;
            Top = clamped.Top;
            Width = clamped.Width;
            Height = clamped.Height;
        }
        finally
        {
            _suppressStatePersistence = false;
        }
    }

    private void PersistWindowModeBounds()
    {
        if (!_isWindowMode || WindowState != WindowState.Normal)
        {
            return;
        }

        var settings = StatsManager.Instance.Settings;
        settings.MainWindowLeft = Left;
        settings.MainWindowTop = Top;
        settings.MainWindowWidth = Width;
        settings.MainWindowHeight = Height;
        StatsManager.Instance.SaveSettings();
    }

    private static Rect ClampToVisibleArea(Rect bounds, Rect workingArea)
    {
        var width = Math.Min(bounds.Width, workingArea.Width);
        var height = Math.Min(bounds.Height, workingArea.Height);
        var left = Math.Max(workingArea.Left, Math.Min(bounds.Left, workingArea.Right - width));
        var top = Math.Max(workingArea.Top, Math.Min(bounds.Top, workingArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    private static Rect? GetVisibleWorkingArea()
    {
        Rect? combinedBounds = null;
        foreach (var screen in Screen.AllScreens)
        {
            var area = new Rect(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height);

            combinedBounds = combinedBounds.HasValue ? Rect.Union(combinedBounds.Value, area) : area;
        }

        return combinedBounds;
    }

    private void CenterWindowOnPrimaryScreen()
    {
        var workArea = SystemParameters.WorkArea;
        _suppressStatePersistence = true;
        try
        {
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        }
        finally
        {
            _suppressStatePersistence = false;
        }
    }
}
