using System.Windows.Threading;
using Capsule.Settings;
using Windows.Gaming.Input;

namespace Capsule.Core;

internal sealed record ControllerInfo(string Key, string Name, int? BatteryPercent);

/// <summary>Reports game controllers (USB and Bluetooth) connecting and disconnecting.</summary>
internal sealed class ControllerWatcher : IDisposable
{
    // Steam Input creates virtual controllers that come and go with games; they use Valve's vendor number.
    private const ushort SteamVendorId = 10462;
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromMilliseconds(1500);

    private readonly Dispatcher _ui;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, ControllerInfo> _present = [];
    private readonly Dictionary<string, DispatcherTimer> _leaving = [];
    private DateTime _quietUntil;
    private bool _started;

    public ControllerWatcher(Dispatcher ui, Func<AppSettings> settings)
    {
        _ui = ui;
        _settings = settings;
    }

    public event Action<ControllerInfo, bool>? Changed;

    /// <summary>Names of the controllers connected right now.</summary>
    public IEnumerable<string> PresentNames => _present.Values.Select(c => c.Name);

    /// <summary>Current battery level of each connected controller that reports one.</summary>
    public IEnumerable<(string Key, string Name, int Percent)> Batteries()
    {
        IReadOnlyList<RawGameController> controllers;
        try
        {
            controllers = RawGameController.RawGameControllers;
        }
        catch
        {
            yield break;
        }

        foreach (var controller in controllers)
        {
            if (Ignore(controller)) continue;
            var info = Describe(controller);
            if (info.BatteryPercent is int percent) yield return (info.Key, info.Name, percent);
        }
    }

    public void Start()
    {
        _quietUntil = DateTime.UtcNow + StartupQuiet;
        try
        {
            RawGameController.RawGameControllerAdded += OnAdded;
            RawGameController.RawGameControllerRemoved += OnRemoved;
            _started = true;
            foreach (var controller in RawGameController.RawGameControllers)
            {
                if (!Ignore(controller)) Arrive(Describe(controller), announce: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Game controller watching is unavailable", ex);
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        RawGameController.RawGameControllerAdded -= OnAdded;
        RawGameController.RawGameControllerRemoved -= OnRemoved;
        foreach (var timer in _leaving.Values) timer.Stop();
    }

    private void OnAdded(object? sender, RawGameController controller)
    {
        try
        {
            if (Ignore(controller)) return;
            var info = Describe(controller);
            _ui.BeginInvoke(() => Arrive(info, announce: true));
        }
        catch (Exception ex)
        {
            Log.Error("Controller event failed", ex);
        }
    }

    private void OnRemoved(object? sender, RawGameController controller)
    {
        try
        {
            var key = KeyOf(controller);
            _ui.BeginInvoke(() => Leave(key));
        }
        catch (Exception ex)
        {
            Log.Error("Controller event failed", ex);
        }
    }

    private void Arrive(ControllerInfo info, bool announce)
    {
        // A quick drop-and-reconnect (common over Bluetooth) is not worth a HUD.
        if (_leaving.Remove(info.Key, out var timer))
        {
            timer.Stop();
            return;
        }

        if (!_present.TryAdd(info.Key, info)) return;
        Log.Info($"Controller connected: {info.Name}");
        if (announce && DateTime.UtcNow >= _quietUntil) Changed?.Invoke(info, true);
    }

    private void Leave(string key)
    {
        if (!_present.TryGetValue(key, out var info) || _leaving.ContainsKey(key)) return;

        _leaving[key] = new DispatcherTimer(ReconnectGrace, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            _leaving.Remove(key);
            _present.Remove(key);
            Log.Info($"Controller disconnected: {info.Name}");
            Changed?.Invoke(info with { BatteryPercent = null }, false);
        }, _ui);
    }

    private bool Ignore(RawGameController controller) =>
        _settings().IgnoreVirtualControllers && controller.HardwareVendorId == SteamVendorId;

    private static string KeyOf(RawGameController controller) =>
        string.IsNullOrEmpty(controller.NonRoamableId)
            ? $"{controller.DisplayName}/{controller.HardwareVendorId}/{controller.HardwareProductId}"
            : controller.NonRoamableId;

    private static ControllerInfo Describe(RawGameController controller)
    {
        var clean = DisplayText.Clean(controller.DisplayName, 80);
        var name = clean.Length == 0 ? "Controller" : clean;
        int? battery = null;
        try
        {
            var report = controller.TryGetBatteryReport();
            if (report?.RemainingCapacityInMilliwattHours is int remaining
                && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
            {
                battery = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
            }
        }
        catch
        {
            // Many controllers do not report a battery.
        }

        return new ControllerInfo(KeyOf(controller), name, battery);
    }
}
