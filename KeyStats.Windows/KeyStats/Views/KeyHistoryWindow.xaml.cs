using System;
using System.Windows;
using KeyStats.Helpers;
using KeyStats.Services;
using KeyStats.ViewModels;

namespace KeyStats.Views;

public partial class KeyHistoryWindow : Window
{
    private readonly KeyHistoryViewModel _viewModel;

    public KeyHistoryWindow()
    {
        InitializeComponent();
        var settings = StatsManager.Instance.Settings;
        if (settings.KeyHistoryWindowWidth is double width &&
            width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
        {
            Width = Math.Max(MinWidth, width);
        }

        if (settings.KeyHistoryWindowHeight is double height &&
            height > 0 && !double.IsNaN(height) && !double.IsInfinity(height))
        {
            Height = Math.Max(MinHeight, height);
        }

        _viewModel = (KeyHistoryViewModel)DataContext;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowBackdrop();
        App.CurrentApp?.TrackPageView("key_history");
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
        settings.KeyHistoryWindowWidth = Width;
        settings.KeyHistoryWindowHeight = Height;
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
