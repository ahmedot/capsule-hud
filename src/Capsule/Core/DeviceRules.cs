using Capsule.Audio;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Core;

/// <summary>Pure decision rules, kept free of Windows calls so they can be unit-tested.</summary>
internal static class DeviceRules
{
    /// <summary>
    /// Bluetooth "Hands-Free" endpoints are the low-quality call mode of a headset.
    /// Sound is never moved to them.
    /// </summary>
    public static bool IsHandsFree(string name) =>
        name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hands Free", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeadphones(AudioEndpoint endpoint, AppSettings settings) =>
        IsHeadphones(endpoint.Name, endpoint.FormFactor, endpoint.Flow, settings);

    public static bool IsHeadphones(string name, FormFactor formFactor, EDataFlow flow, AppSettings settings)
    {
        if (flow != EDataFlow.Render || IsHandsFree(name)) return false;

        return settings.HeadphoneMode switch
        {
            HeadphoneMode.Selected => settings.SelectedHeadphones.Contains(name, StringComparer.OrdinalIgnoreCase),
            _ => formFactor is FormFactor.Headphones or FormFactor.Headset,
        };
    }

    /// <summary>Picks where the sound should go after headphones disconnect.</summary>
    public static AudioEndpoint? ChooseFallback(
        IReadOnlyList<AudioEndpoint> activeRender,
        IReadOnlyList<AudioEndpoint> stillConnectedHeadphones,
        AppSettings settings,
        string? previousDeviceId)
    {
        // Other headphones still on: use the one connected most recently.
        if (stillConnectedHeadphones.Count > 0)
        {
            var latest = stillConnectedHeadphones[^1];
            return activeRender.FirstOrDefault(e => e.Id == latest.Id);
        }

        if (settings.FallbackDevice.Length > 0)
        {
            var chosen = UniqueByName(activeRender, settings.FallbackDevice);
            if (chosen is not null) return chosen;
        }

        return previousDeviceId is null ? null : activeRender.FirstOrDefault(e => e.Id == previousDeviceId);
    }

    /// <summary>
    /// The one endpoint with this name, or null when there is none or more than one.
    /// Names come from devices, so two devices sharing a name means we can't tell which one the user meant.
    /// </summary>
    public static AudioEndpoint? UniqueByName(IReadOnlyList<AudioEndpoint> endpoints, string name)
    {
        AudioEndpoint? found = null;
        foreach (var endpoint in endpoints)
        {
            if (!string.Equals(endpoint.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            found = endpoint;
        }

        return found;
    }

    public static bool NameIsShared(IReadOnlyList<AudioEndpoint> endpoints, AudioEndpoint endpoint) =>
        endpoints.Any(e => e.Id != endpoint.Id && string.Equals(e.Name, endpoint.Name, StringComparison.OrdinalIgnoreCase));

    public static string VolumeGlyph(float level, bool muted) =>
        muted || level <= 0.001f ? Glyphs.Mute
        : level < 0.34f ? Glyphs.Volume1
        : level < 0.67f ? Glyphs.Volume2
        : Glyphs.Volume3;

    public static string BatteryGlyph(int percent)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        return step == 10 ? Glyphs.Battery10 : char.ConvertFromUtf32(Glyphs.Battery0Code + step);
    }
}

/// <summary>Segoe Fluent Icons code points.</summary>
internal static class Glyphs
{
    public const string Headphones = "";
    public const string Speakers = "";
    public const string Controller = "";
    public const string Mute = "";
    public const string Volume1 = "";
    public const string Volume2 = "";
    public const string Volume3 = "";
    public const string Microphone = "";
    public const string MicrophoneOff = "";
    public const string Bluetooth = "";
    public const string Checkmark = "";
    public const string Keyboard = "";
    public const string Brightness = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Music = "";
    public const string Video = ""; // a screen with a play arrow (YouTube)
    public const string Globe = "";
    public const string Camera = "";
    public const string Usb = "";
    public const string Wifi = "";
    public const string Ethernet = "";
    public const string Vpn = "";
    public const string NoInternet = "";
    public const string Gauge = "";
    public const string Warning = "";
    public const string HardDrive = "";
    public const string Charging = "";
    public const string Update = "";
    public const string Battery10 = "";
    public const int Battery0Code = 0xE850;
}
