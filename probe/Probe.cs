using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

public static class Probe
{
    public static void A(Window owner, string s)
    {
        MessageBox.Show(owner, s, s, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    public static void B(Line l)
    {
        var lb = new ListBox { ScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    public static void C(Dispatcher d)
    {
        d.BeginInvoke(new Action(() => { }), DispatcherPriority.Input);
    }
    public static void D()
    {
        DependencyObject d = new TextBlock();
        bool x = d is Visual or Media3D.Visual3D;
    }
    public static void E()
    {
        var t = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
    }
}
