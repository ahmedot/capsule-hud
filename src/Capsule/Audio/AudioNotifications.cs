using System.Runtime.InteropServices;
using System.Windows.Threading;
using Capsule.Interop;

namespace Capsule.Audio;

/// <summary>
/// Receives device events from Windows (on a Windows worker thread) and re-raises them on the UI thread.
/// The callbacks must return quickly and must never throw back into Windows.
/// </summary>
[ComVisible(true)]
public sealed class AudioNotifications : IMMNotificationClient
{
    private readonly Dispatcher _ui;

    public AudioNotifications(Dispatcher ui) => _ui = ui;

    /// <summary>An endpoint appeared, disappeared, or changed state.</summary>
    public event Action<string>? EndpointChanged;

    /// <summary>The default device for a flow and role changed.</summary>
    public event Action<EDataFlow, ERole, string?>? DefaultChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Post(() => EndpointChanged?.Invoke(deviceId));

    public void OnDeviceAdded(string deviceId) => Post(() => EndpointChanged?.Invoke(deviceId));

    public void OnDeviceRemoved(string deviceId) => Post(() => EndpointChanged?.Invoke(deviceId));

    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) =>
        Post(() => DefaultChanged?.Invoke(flow, role, defaultDeviceId));

    public void OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // Not needed.
    }

    private void Post(Action action)
    {
        try
        {
            _ui.BeginInvoke(action);
        }
        catch
        {
            // The app is shutting down.
        }
    }
}
