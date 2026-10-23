using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Imvello.Services;

public static class ThemeManager
{
    private const string LightSource = "Themes/Light.xaml";
    private const string DarkSource = "Themes/Dark.xaml";
    private static bool _initialized;

    public static bool IsDark { get; private set; }
    public static event EventHandler? ThemeChanged;

    public static void Initialize(Application app)
    {
        Apply(app, ReadSystemIsDark());
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => Apply(app, ReadSystemIsDark()));
        };
        _initialized = true;
    }

    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply(Application app, bool dark)
    {
        if (_initialized && dark == IsDark) return;
        IsDark = dark;
        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)));
        var replacement = new ResourceDictionary { Source = new Uri("pack://application:,,,/" + (dark ? DarkSource : LightSource), UriKind.Absolute) };
        if (existing != null) dicts.Remove(existing);
        dicts.Add(replacement);
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
