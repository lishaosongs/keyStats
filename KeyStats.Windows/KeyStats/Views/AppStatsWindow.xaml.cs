using System;
using System.Windows;
using KeyStats.Helpers;
using KeyStats.Services;
using KeyStats.ViewModels;

namespace KeyStats.Views;

public partial class AppStatsWindow : Window
{
    private readonly AppStatsViewModel _viewModel;

    public AppStatsWindow()
    {
        InitializeComponent();
        var settings = StatsManager.Instance.Settings;
        if (settings.AppStatsWindowWidth is double width &&
            width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
        {
            Width = Math.Max(MinWidth, width);
        }

        if (settings.AppStatsWindowHeight is double height &&
            height > 0 && !double.IsNaN(height) && !double.IsInfinity(height))
        {
            Height = Math.Max(MinHeight, height);
        }

        _viewModel = (AppStatsViewModel)DataContext;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowBackdrop();
        App.CurrentApp?.TrackPageView("app_stats");
    }

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(new Action(ApplyWindowBackdrop));
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
        settings.AppStatsWindowWidth = Width;
        settings.AppStatsWindowHeight = Height;
        StatsManager.Instance.SaveSettings();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveWindowSize();
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
        _viewModel.Cleanup();
    }

    private void ApplyWindowBackdrop()
    {
        WindowBackdropHelper.Apply(this, NativeInterop.DwmSystemBackdropType.TransientWindow);
    }
}
