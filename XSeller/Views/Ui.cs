using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XSeller.Views;

/// <summary>Tiny helpers for the code-built admin / login / closing dialogs (light theme = high contrast).</summary>
internal static class Ui
{
    public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));
    public static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x60));

    public static Window Dialog(string title, double w, double h, Window? owner)
    {
        var win = new Window
        {
            Title = title, Width = w, Height = h, MinWidth = Math.Min(w, 420), MinHeight = Math.Min(h, 300),
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Brushes.White, Foreground = Brushes.Black, FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            UseLayoutRounding = true, ShowInTaskbar = owner == null,
        };
        // Admin dialogs use the default light look → reset the app-wide dark selection overrides to standard ones.
        win.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        win.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        win.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7));
        win.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.Black;
        // Local default button style (the dark PrimaryButton is not applied here).
        win.Resources[typeof(Button)] = new Style(typeof(Button)) { Setters = { new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)), new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)) } };
        if (owner != null && owner.IsLoaded) win.Owner = owner;
        return win;
    }

    public static TextBlock Text(string s, double size = 13, bool bold = false, Brush? fg = null, bool wrap = true) => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg ?? Brushes.Black, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = new Thickness(0, 0, 0, 6),
    };

    public static Button Button(string s, RoutedEventHandler onClick, bool isDefault = false)
    {
        var b = new Button { Content = s, IsDefault = isDefault };
        b.Click += onClick;
        return b;
    }

    public static StackPanel Row(params UIElement[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    public static StackPanel Stack(params UIElement[] kids)
    {
        var p = new StackPanel();
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    public static FrameworkElement Labeled(string label, FrameworkElement input, double labelWidth = 150)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        g.Children.Add(t);
        Grid.SetColumn(input, 1);
        g.Children.Add(input);
        return g;
    }

    public static DataGrid Table(params (string Header, string Path)[] cols)
    {
        var dg = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single,
            Background = Brushes.White, RowBackground = Brushes.White, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9)),
            Foreground = Brushes.Black, MinHeight = 80, Margin = new Thickness(0, 0, 0, 10),
            EnableRowVirtualization = true,
        };
        foreach (var (h, p) in cols)
            dg.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p) { StringFormat = null }, Width = DataGridLength.Auto });
        if (dg.Columns.Count > 0) dg.Columns[0].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        return dg;
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16),
    };

    public static void Error(Window? owner, string msg) =>
        MessageBox.Show(owner ?? Application.Current.MainWindow, msg, "X Seller", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static string Money(decimal d) => d.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);
}
