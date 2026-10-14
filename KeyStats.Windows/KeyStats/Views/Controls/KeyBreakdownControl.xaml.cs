using System.Collections;
using System.Windows;

namespace KeyStats.Views.Controls;

public partial class KeyBreakdownControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty Column1ItemsProperty =
        DependencyProperty.Register(nameof(Column1Items), typeof(IEnumerable), typeof(KeyBreakdownControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty Column2ItemsProperty =
        DependencyProperty.Register(nameof(Column2Items), typeof(IEnumerable), typeof(KeyBreakdownControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty Column3ItemsProperty =
        DependencyProperty.Register(nameof(Column3Items), typeof(IEnumerable), typeof(KeyBreakdownControl),
            new PropertyMetadata(null));

    public IEnumerable? Column1Items
    {
        get => (IEnumerable?)GetValue(Column1ItemsProperty);
        set => SetValue(Column1ItemsProperty, value);
    }

    public IEnumerable? Column2Items
    {
        get => (IEnumerable?)GetValue(Column2ItemsProperty);
        set => SetValue(Column2ItemsProperty, value);
    }

    public IEnumerable? Column3Items
    {
        get => (IEnumerable?)GetValue(Column3ItemsProperty);
        set => SetValue(Column3ItemsProperty, value);
    }

    public KeyBreakdownControl()
    {
        InitializeComponent();
    }
}
