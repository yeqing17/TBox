using Microsoft.UI.Dispatching;

namespace TBox;

/// <summary>
/// One cursor poll shared by every docked box. Four independent 50ms timers would multiply the
/// idle work and wake-ups; the manager keeps a single timer and only runs it while at least one
/// panel is docked.
/// </summary>
internal sealed class DockPoll
{
    private const int IntervalMs = 50;

    private readonly DispatcherQueueTimer _timer;
    private readonly List<MainWindow> _targets = new();

    public DockPoll()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Poll();
    }

    public void Register(MainWindow window)
    {
        if (!_targets.Contains(window)) _targets.Add(window);
        if (!_timer.IsRunning) _timer.Start();
    }

    public void Unregister(MainWindow window)
    {
        _targets.Remove(window);
        if (_targets.Count == 0) _timer.Stop();
    }

    private void Poll()
    {
        foreach (var window in _targets.ToArray())
        {
            try
            {
                window.PollDock();
            }
            catch (Exception ex)
            {
                App.Log("[dock] poll failed: " + ex.Message);
            }
        }
    }
}
