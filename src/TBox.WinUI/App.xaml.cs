using Microsoft.UI.Xaml;

namespace TBox;

public partial class App : Application
{
    // Log file under %LOCALAPPDATA%/TBox, created on demand (also used by CI smoke tests).
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TBox", "app.log");
    private BoxManager? _manager;
    private Mutex? _mutex;

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + "\n");
        }
        catch { }
    }

    public App()
    {
        Log("App ctor");
        try
        {
            InitializeComponent();
            Log("InitializeComponent ok");
        }
        catch (Exception ex)
        {
            Log("InitializeComponent FAILED: " + ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched");

        _mutex = new Mutex(initiallyOwned: true, @"Local\TBox.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Log("second instance, exiting");
            Exit();
            return;
        }
        try
        {
            _manager = new BoxManager();
            Log("BoxManager created");
            _manager.Start();
            Log("Activated");
        }
        catch (Exception ex)
        {
            Log("OnLaunched FAILED: " + ex);
            throw;
        }
    }
}
