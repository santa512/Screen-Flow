using System;
using System.IO;
using System.Windows;

namespace ScreenFlow;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Array.IndexOf(e.Args, "--self-test") >= 0)
        {
            try { SelfTests.Run(); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText("self-test-results.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show(args.Exception.Message, "ScreenFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        Appearance.Initialize();
        MainWindow = new MainWindow();
        MainWindow.Show();
        bool browserOnly = Array.IndexOf(e.Args, "--browser-self-test") >= 0;
        if (browserOnly || Array.IndexOf(e.Args, "--ui-self-test") >= 0) {
            Dispatcher.BeginInvoke(async () => {
                try { await IntegrationTests.Run(MainWindow, browserOnly); Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(browserOnly ? "browser-test-results.txt" : "ui-test-results.txt", ex.ToString()); Shutdown(1); }
            });
        }
    }
}
