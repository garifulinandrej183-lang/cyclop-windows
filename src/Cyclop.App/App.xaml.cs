using System.Windows;
using System.Windows.Media.Imaging;
using Cyclop.Windows.Shell;

namespace Cyclop.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _panel;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new();
        if (_instance.IsPrimary && e.Args.Contains("--diagnose")) { Console.WriteLine("{\"Running\":false}"); Shutdown(); return; }
        if (!_instance.IsPrimary)
        {
            if (e.Args.Contains("--diagnose")) Console.WriteLine(await _instance.GetDiagnosticsAsync());
            else await _instance.SignalPrimaryAsync();
            Shutdown(); return;
        }
        try
        {
            _panel = new MainWindow
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Cyclop.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
            };
            MainWindow = _panel;
            _instance.DiagnosticsRequested = () => Dispatcher.Invoke(_panel.Diagnostics);
            _instance.OpenRequested += () => Dispatcher.BeginInvoke(() => _panel.OpenExplicit());
            _panel.Show();
            await _panel.InitializeAsync();
            if (e.Args.Contains("--open")) _panel.OpenExplicit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить Cyclop. {ex.GetType().Name}\n{ex.Message}", "Cyclop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _panel?.Dispose(); _instance?.Dispose(); base.OnExit(e);
    }
}
