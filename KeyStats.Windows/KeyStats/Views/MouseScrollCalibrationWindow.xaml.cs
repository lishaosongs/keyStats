using System;
using System.Globalization;
using System.Windows;
using KeyStats.Helpers;
using KeyStats.Services;

namespace KeyStats.Views;

public partial class MouseScrollCalibrationWindow : Window
{
    public MouseScrollCalibrationWindow()
    {
        InitializeComponent();
        CurrentPpiText.Text = string.Format(
            KeyStats.Properties.Strings.ScrollCalibration_CurrentPpiFormat,
            StatsManager.Instance.Settings.MouseScrollPpi);
        Loaded += OnLoaded;
        Closed += OnClosed;
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowBackdrop();
        App.CurrentApp?.TrackPageView("mouse_scroll_calibration");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(new Action(ApplyWindowBackdrop));
    }

    private void ApplyWindowBackdrop()
    {
        WindowBackdropHelper.Apply(this, NativeInterop.DwmSystemBackdropType.TransientWindow);
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TryCalculatePpi(out var ppi))
        {
            ResultText.Text = string.Format(KeyStats.Properties.Strings.ScrollCalibration_ResultFormat, ppi);
            SaveButton.IsEnabled = true;
        }
        else
        {
            ResultText.Text = KeyStats.Properties.Strings.ScrollCalibration_ResultEmpty;
            SaveButton.IsEnabled = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCalculatePpi(out var ppi)) return;

        StatsManager.Instance.UpdateMouseScrollPpi(ppi);
        App.CurrentApp?.TrackClick("save_mouse_scroll_calibration");
        Close();
    }

    private bool TryCalculatePpi(out int ppi)
    {
        ppi = 0;
        if (!int.TryParse(WidthTextBox.Text, out var width) || width <= 0 ||
            !int.TryParse(HeightTextBox.Text, out var height) || height <= 0 ||
            !TryParseDiagonal(DiagonalTextBox.Text, out var diagonalInches) ||
            double.IsNaN(diagonalInches) || double.IsInfinity(diagonalInches) || diagonalInches <= 0)
        {
            return false;
        }

        var calculated = Math.Round(
            Math.Sqrt((double)width * width + (double)height * height) / diagonalInches,
            MidpointRounding.AwayFromZero);
        if (calculated < 1 || calculated > int.MaxValue) return false;

        ppi = (int)calculated;
        return true;
    }

    private static bool TryParseDiagonal(string text, out double diagonalInches)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out diagonalInches) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out diagonalInches);
    }
}
