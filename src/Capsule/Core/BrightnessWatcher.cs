using System.Management;
using System.Windows.Threading;

namespace Capsule.Core;

/// <summary>Reports built-in screen brightness changes (laptops and tablets). Does nothing on desktops.</summary>
internal sealed class BrightnessWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private ManagementEventWatcher? _watcher;
    private int _last = -1;
    private bool _disposed;

    public BrightnessWatcher(Dispatcher ui) => _ui = ui;

    /// <summary>New brightness, 0 to 100.</summary>
    public event Action<int>? Changed;

    /// <summary>Starts in the background: WMI can take a moment to answer.</summary>
    public void Start() => Task.Run(() =>
    {
        try
        {
            var current = ReadCurrent();
            if (current < 0)
            {
                Log.Info("No built-in display; brightness HUD is off on this PC");
                return;
            }

            var watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\wmi"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEvent;
            watcher.Start();

            _ui.BeginInvoke(() =>
            {
                _last = current;
                if (_disposed)
                {
                    watcher.Stop();
                    watcher.Dispose();
                }
                else
                {
                    _watcher = watcher;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Brightness HUD is unavailable", ex);
        }
    });

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }
        catch
        {
            // Already stopped.
        }
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var level = Convert.ToInt32(e.NewEvent["Brightness"]);
            _ui.BeginInvoke(() =>
            {
                if (level == _last || level is < 0 or > 100) return;
                _last = level;
                Changed?.Invoke(level);
            });
        }
        catch
        {
            // Ignore a malformed event.
        }
    }

    private static int ReadCurrent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"]);
                }
            }
        }
        catch (ManagementException)
        {
            // "Not supported": no built-in display.
        }

        return -1;
    }
}
