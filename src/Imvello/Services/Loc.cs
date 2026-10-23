using System.Globalization;
using System.Windows;

namespace Imvello.Services;

public static class Loc
{
    private static readonly string[] Areas = { "Common", "Main", "Dialogs", "Calendar", "Contacts" };

    public static string CurrentLanguage { get; private set; } = "en";
    public static event EventHandler? LanguageChanged;

    public static string Resolve(string? setting) =>
        setting is "en" or "es" ? setting :
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    public static void Apply(Application app, string? setting)
    {
        var lang = Resolve(setting);
        var dicts = app.Resources.MergedDictionaries;
        foreach (var d in dicts.Where(d => d.Source != null && d.Source.OriginalString.Contains("/Strings/", StringComparison.OrdinalIgnoreCase)).ToList())
            dicts.Remove(d);
        foreach (var area in Areas)
            dicts.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Strings/{area}.{lang}.xaml", UriKind.Absolute) });
        var changed = lang != CurrentLanguage;
        CurrentLanguage = lang;
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}
