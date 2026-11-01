using Microsoft.UI.Xaml;

namespace TBox;

public partial class App : Application
{
    private const string LogPath = @"F:/zcode/TBox/.winui-log.txt";
    private Window? _window;
    private Mutex? _mutex;

    internal static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); } catch { }
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
            _window = new MainWindow();
            Log("MainWindow created");
            _window.Activate();
            Log("Activated");
        }
        catch (Exception ex)
        {
            Log("OnLaunched FAILED: " + ex);
            throw;
        }
    }
}
