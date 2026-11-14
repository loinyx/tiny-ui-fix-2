using Microsoft.UI.Xaml;

namespace TinyUIFixApp;

public partial class App : Application
{
    Window? _window;

    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "TinyUIFixApp.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    public static void Log(Exception? e)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:s} {e}\r\n"); } catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        L.Init();
        _window = new MainWindow();
        _window.Activate();
    }
}
