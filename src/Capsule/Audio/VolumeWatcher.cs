using System.Runtime.InteropServices;
using System.Windows.Threading;
using Capsule.Core;
using Capsule.Interop;

namespace Capsule.Audio;

[ComVisible(true)]
public sealed class VolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action<bool, float> _changed;

    public VolumeCallback(Action<bool, float> changed) => _changed = changed;

    public void OnNotify(IntPtr notifyData)
    {
        try
        {
            if (notifyData == IntPtr.Zero) return;
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
            _changed(data.Muted != 0, data.MasterVolume);
        }
        catch
        {
            // Never throw back into Windows.
        }
    }
}

/// <summary>Watches volume/mute of the default speakers and mute of the default microphone.</summary>
internal sealed class VolumeWatcher : IDisposable
{
    private static readonly TimeSpan QuietAfterSwitch = TimeSpan.FromMilliseconds(1500);

    private readonly AudioSystem _audio;
    private readonly Dispatcher _ui;
    private readonly Watched _output = new();
    private readonly Watched _mic = new();
    private DateTime _quietUntil;

    public VolumeWatcher(AudioSystem audio, Dispatcher ui)
    {
        _audio = audio;
        _ui = ui;
    }

    /// <summary>Volume or mute changed on the default output: device, level 0..1, muted.</summary>
    public event Action<AudioEndpoint, float, bool>? VolumeChanged;

    /// <summary>Mute changed on the default microphone.</summary>
    public event Action<AudioEndpoint, bool>? MicMuteChanged;

    public void Refresh()
    {
        Attach(_output, _audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia), OnOutput);
        Attach(_mic, _audio.GetDefaultId(EDataFlow.Capture, ERole.Communications), OnMic);
    }

    /// <summary>Called when the default device changes so the change itself does not pop a volume HUD.</summary>
    public void BeQuiet() => _quietUntil = DateTime.UtcNow + QuietAfterSwitch;

    public void Dispose()
    {
        Detach(_output);
        Detach(_mic);
    }

    private void Attach(Watched watched, string? id, Action<string, bool, float> handler)
    {
        if (id == watched.Id && watched.Volume is not null) return;
        Detach(watched);
        if (id is null) return;

        try
        {
            var volume = _audio.ActivateVolume(id);
            var endpoint = _audio.GetEndpoint(id);
            if (volume is null || endpoint is null)
            {
                if (volume is not null) Marshal.ReleaseComObject(volume);
                return;
            }

            volume.GetMute(out var muted);
            volume.GetMasterVolumeLevelScalar(out var level);
            var callback = new VolumeCallback((m, v) => _ui.BeginInvoke(() => handler(id, m, v)));
            if (volume.RegisterControlChangeNotify(callback) != 0)
            {
                Marshal.ReleaseComObject(volume);
                return;
            }

            watched.Id = id;
            watched.Endpoint = endpoint;
            watched.Volume = volume;
            watched.Callback = callback;
            watched.Muted = muted;
            watched.Level = level;
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch the volume", ex);
        }
    }

    private static void Detach(Watched watched)
    {
        if (watched.Volume is not null)
        {
            try
            {
                if (watched.Callback is not null) watched.Volume.UnregisterControlChangeNotify(watched.Callback);
                Marshal.ReleaseComObject(watched.Volume);
            }
            catch
            {
                // The device is already gone.
            }
        }

        watched.Id = null;
        watched.Endpoint = null;
        watched.Volume = null;
        watched.Callback = null;
    }

    private void OnOutput(string id, bool muted, float level)
    {
        if (id != _output.Id || _output.Endpoint is null) return;
        var changed = muted != _output.Muted || Math.Abs(level - _output.Level) > 0.004f;
        _output.Muted = muted;
        _output.Level = level;
        if (changed && DateTime.UtcNow >= _quietUntil) VolumeChanged?.Invoke(_output.Endpoint, level, muted);
    }

    private void OnMic(string id, bool muted, float level)
    {
        // Mic level changes are ignored on purpose: call apps adjust them constantly.
        if (id != _mic.Id || _mic.Endpoint is null || muted == _mic.Muted) return;
        _mic.Muted = muted;
        _mic.Level = level;
        if (DateTime.UtcNow >= _quietUntil) MicMuteChanged?.Invoke(_mic.Endpoint, muted);
    }

    private sealed class Watched
    {
        public string? Id;
        public AudioEndpoint? Endpoint;
        public IAudioEndpointVolume? Volume;
        public VolumeCallback? Callback;
        public bool Muted;
        public float Level;
    }
}
