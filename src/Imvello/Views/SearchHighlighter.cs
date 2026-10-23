using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Imvello.Views;

public static class SearchHighlighter
{
    private static readonly Brush HighlightBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x4D)));
    private static readonly Brush HighlightForeground = Freeze(new SolidColorBrush(Colors.Black));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SearchHighlighter), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty PatternProperty = DependencyProperty.RegisterAttached(
        "Pattern", typeof(Regex), typeof(SearchHighlighter), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
    public static Regex? GetPattern(DependencyObject d) => (Regex?)d.GetValue(PatternProperty);
    public static void SetPattern(DependencyObject d, Regex? value) => d.SetValue(PatternProperty, value);

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = GetText(tb) ?? "";
        var pattern = GetPattern(tb);
        tb.Inlines.Clear();
        if (pattern == null || text.Length == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }
        var pos = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Length == 0) continue;
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            tb.Inlines.Add(new Run(m.Value) { Background = HighlightBackground, Foreground = HighlightForeground, FontWeight = FontWeights.Bold });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }
}
