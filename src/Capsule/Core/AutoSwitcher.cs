using System.Windows.Threading;
using Capsule.Audio;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Core;

/// <summary>
/// Moves sound to headphones when they connect and back when they leave.
/// Keeps the microphone in place when Bluetooth headphones try to take it over
/// (a Bluetooth headset mic forces the headphones into low-quality call mode).
/// All members run on the UI thread.
/// </summary>
internal sealed class AutoSwitcher
{
    // Bluetooth devices come up in steps; a change must hold this long before we act on it.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan MicGuard = TimeSpan.FromSeconds(20);

    private readonly AudioSystem _audio;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _ui;
    private readonly List<AudioEndpoint> _connected = [];          // headphones, in the order they connected
    private readonly Dictionary<string, DispatcherTimer> _pending = [];
    private readonly HashSet<Guid> _bluetoothContainers = [];

    private string? _previousOutputId;   // last non-headphone output: where to go back to
    private string? _previousMicId;      // last mic that was not a headset mic
    private DateTime _micGuardUntil;

    public AutoSwitcher(AudioSystem audio, Func<AppSettings> settings, Dispatcher ui)
    {
        _audio = audio;
        _settings = settings;
        _ui = ui;
    }

    /// <summary>Headphones connected (true) or disconnected (false).</summary>
    public event Action<AudioEndpoint, bool>? HeadphonesChanged;

    /// <summary>Capsule changed the default output.</summary>
    public event Action? Switched;

    public IReadOnlyList<AudioEndpoint> Connected => _connected;

    /// <summary>True while the Windows session is locked or switched away: audio defaults are left alone.</summary>
    public bool Paused { get; set; }

    public void Start(bool atSignIn)
    {
        Rescan();

        var output = _audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia);
        if (output is not null && !IsConnectedHeadphones(output)) _previousOutputId = output;

        var micId = _audio.GetDefaultId(EDataFlow.Capture, ERole.Console);
        var mic = micId is null ? null : _audio.GetEndpoint(micId);
        if (mic is not null && !IsHeadsetMic(mic)) _previousMicId = micId;

        // Right after sign-in, make sure sound starts on the headphones if they are already on.
        if (atSignIn && _settings().AutoSwitch && _connected.Count > 0 && !IsConnectedHeadphones(output))
        {
            SwitchTo(_connected[^1], "already connected at sign-in");
        }
    }

    /// <summary>Re-reads which headphones are connected, without HUDs. Used at start and when settings change.</summary>
    public void Rescan()
    {
        _connected.Clear();
        foreach (var endpoint in _audio.GetEndpoints(EDataFlow.Render))
        {
            if (DeviceRules.IsHeadphones(endpoint, _settings()))
            {
                _connected.Add(endpoint);
                RememberBluetooth(endpoint);
            }
        }
    }

    public void OnEndpointChanged(string id)
    {
        var endpoint = _audio.GetEndpoint(id);
        var known = _connected.Any(e => e.Id == id);

        if (endpoint is null)
        {
            if (known) Schedule(id);
            return;
        }

        if (endpoint.Flow != EDataFlow.Render) return;

        var wanted = endpoint.IsActive && DeviceRules.IsHeadphones(endpoint, _settings());
        if (wanted != known)
        {
            Schedule(id);
        }
        else if (_pending.Remove(id, out var timer))
        {
            timer.Stop(); // it changed back before it settled
        }
    }

    public void OnDefaultChanged(EDataFlow flow, ERole role, string? id)
    {
        // Output: the "default device" role. Microphone: also watch the "communications" role,
        // because Windows sometimes moves only the call microphone to the headset.
        if (id is null) return;
        if (role != ERole.Console && !(flow == EDataFlow.Capture && role == ERole.Communications)) return;

        var endpoint = _audio.GetEndpoint(id);
        if (endpoint is null) return;

        if (flow == EDataFlow.Render)
        {
            if (!DeviceRules.IsHeadphones(endpoint, _settings())) _previousOutputId = id;
        }
        else if (flow == EDataFlow.Capture)
        {
            if (!IsHeadsetMic(endpoint))
            {
                _previousMicId = id;
            }
            else if (DateTime.UtcNow < _micGuardUntil)
            {
                KeepMicrophone();
            }
        }
    }

    private void Schedule(string id)
    {
        if (_pending.ContainsKey(id)) return;
        _pending[id] = new DispatcherTimer(Settle, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            _pending.Remove(id);
            Resolve(id);
        }, _ui);
    }

    private void Resolve(string id)
    {
        var endpoint = _audio.GetEndpoint(id);
        var index = _connected.FindIndex(e => e.Id == id);
        var wanted = endpoint is not null && endpoint.IsActive && DeviceRules.IsHeadphones(endpoint, _settings());

        if (wanted && index < 0)
        {
            OnConnected(endpoint!);
        }
        else if (!wanted && index >= 0)
        {
            var gone = _connected[index];
            _connected.RemoveAt(index);
            OnDisconnected(gone);
        }
    }

    private async void OnConnected(AudioEndpoint headphones)
    {
        try
        {
            Log.Info($"Headphones connected: {headphones.Name}");
            _connected.Add(headphones);
            HeadphonesChanged?.Invoke(headphones, true);

            var settings = _settings();
            if (!settings.AutoSwitch) return;

            // In "only the ones I pick" mode a device is chosen by name. If another active device
            // carries the same name, we can't be sure this is the user's device, so don't move audio.
            if (settings.HeadphoneMode == HeadphoneMode.Selected
                && DeviceRules.NameIsShared(_audio.GetEndpoints(EDataFlow.Render), headphones))
            {
                Log.Info($"Not switching: another device is also called {headphones.Name}");
                return;
            }

            SwitchTo(headphones, "connected");

            var facts = await ContainerInfo.ReadAsync(headphones.ContainerId);
            if (facts.IsBluetooth)
            {
                _bluetoothContainers.Add(headphones.ContainerId);
                _micGuardUntil = DateTime.UtcNow + MicGuard;
                KeepMicrophone();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Handling connected headphones failed", ex);
        }
    }

    private void OnDisconnected(AudioEndpoint headphones)
    {
        Log.Info($"Headphones disconnected: {headphones.Name}");
        HeadphonesChanged?.Invoke(headphones, false);

        var settings = _settings();
        if (!settings.AutoSwitch) return;

        var current = _audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia);
        if (IsConnectedHeadphones(current))
        {
            return; // still on other headphones: leave it
        }

        var target = DeviceRules.ChooseFallback(
            _audio.GetEndpoints(EDataFlow.Render), _connected, settings, _previousOutputId);

        if (target is null)
        {
            Log.Info("No device to go back to; leaving the Windows choice");
        }
        else if (target.Id != current)
        {
            SwitchTo(target, "headphones left");
        }
    }

    private void KeepMicrophone()
    {
        var settings = _settings();
        if (Paused || settings.MicMode == MicMode.LetWindowsDecide) return;

        var target = settings.MicMode == MicMode.Specific
            ? DeviceRules.UniqueByName(_audio.GetEndpoints(EDataFlow.Capture), settings.MicDevice)
            : _previousMicId is null ? null : _audio.GetEndpoint(_previousMicId);

        if (target is null || !target.IsActive) return;

        if (_audio.GetDefaultId(EDataFlow.Capture, ERole.Console) == target.Id
            && _audio.GetDefaultId(EDataFlow.Capture, ERole.Communications) == target.Id)
        {
            return;
        }

        if (_audio.SetDefault(target.Id, includeCommunications: true))
        {
            Log.Info($"Kept microphone: {target.Name}");
        }
    }

    private void SwitchTo(AudioEndpoint endpoint, string reason)
    {
        if (Paused) return;
        Switched?.Invoke();
        if (_audio.SetDefault(endpoint.Id, _settings().SwitchCommunications))
        {
            Log.Info($"Sound moved to {endpoint.Name} ({reason})");
        }
    }

    private bool IsConnectedHeadphones(string? id) => id is not null && _connected.Any(e => e.Id == id);

    // A mic is a "headset mic" when it belongs to the same physical device as a pair of headphones.
    // Checked against every active headphone output, not only settled ones: Windows often grabs the
    // headset mic before the headphones have finished connecting.
    private bool IsHeadsetMic(AudioEndpoint mic)
    {
        if (DeviceRules.IsHandsFree(mic.Name)) return true;
        if (mic.ContainerId == Guid.Empty) return false;
        if (_bluetoothContainers.Contains(mic.ContainerId)) return true;

        var settings = _settings();
        return _audio.GetEndpoints(EDataFlow.Render)
            .Any(r => r.ContainerId == mic.ContainerId && DeviceRules.IsHeadphones(r, settings));
    }

    private async void RememberBluetooth(AudioEndpoint endpoint)
    {
        try
        {
            if ((await ContainerInfo.ReadAsync(endpoint.ContainerId)).IsBluetooth)
            {
                _bluetoothContainers.Add(endpoint.ContainerId);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not check the headphones", ex);
        }
    }
}
