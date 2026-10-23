using System.IO;
using System.Windows;
using System.Windows.Threading;
using Imvello.Services;
using Imvello.Views;

namespace Imvello;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        ThemeMode = ThemeMode.System;
        ThemeManager.Initialize(this);
        Loc.Apply(this, null);
        try
        {
            await AppServices.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Common.DataFileError") + "\n\n" + ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Loc.Apply(this, AppServices.Settings.Language);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Sync?.Stop(); } catch { }
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(AppServices.Settings?.DataDirectory ?? Path.GetTempPath(), "error.log");
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
