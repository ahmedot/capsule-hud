using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Capsule.Core;

namespace Capsule.Hud;

public enum HudTone
{
    Good,
    Bad,
    Neutral,
}

/// <summary>What one HUD pill shows. Properties can change while it is on screen (battery, volume).</summary>
public sealed class HudItem : INotifyPropertyChanged
{
    private string _glyph = Glyphs.Headphones;
    private string _title = "";
    private string _status = "";
    private HudTone _tone = HudTone.Good;
    private int? _battery;
    private double? _level;
    private string _levelText = "";
    private ImageSource? _art;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Items with the same key update the pill in place instead of queueing (volume, mic).</summary>
    public string? ReplaceKey { get; init; }

    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }

    public string Title { get => _title; set => Set(ref _title, value); }

    public string Status
    {
        get => _status;
        set
        {
            Set(ref _status, value);
            OnChanged(nameof(HasStatus));
        }
    }

    public HudTone Tone
    {
        get => _tone;
        set
        {
            Set(ref _tone, value);
            OnChanged(nameof(IsBad));
            OnChanged(nameof(IsNeutral));
        }
    }

    public bool HasStatus => _status.Length > 0;

    public bool IsBad => _tone == HudTone.Bad;

    public int? Battery
    {
        get => _battery;
        set
        {
            Set(ref _battery, value);
            OnChanged(nameof(HasBattery));
            OnChanged(nameof(BatteryText));
            OnChanged(nameof(BatteryGlyph));
        }
    }

    public bool HasBattery => _battery is not null;

    public string BatteryText => _battery is int b ? $"{b}%" : "";

    public string BatteryGlyph => _battery is int b ? DeviceRules.BatteryGlyph(b) : "";

    /// <summary>0..1 for a level bar (volume), or null for none.</summary>
    public double? Level
    {
        get => _level;
        set
        {
            Set(ref _level, value);
            OnChanged(nameof(HasLevel));
            OnChanged(nameof(LevelScale));
        }
    }

    public bool HasLevel => _level is not null;

    public double LevelScale => Math.Clamp(_level ?? 0, 0, 1);

    public string LevelText { get => _levelText; set => Set(ref _levelText, value); }

    /// <summary>Optional picture (album art) shown in place of the icon.</summary>
    public ImageSource? Art
    {
        get => _art;
        set
        {
            Set(ref _art, value);
            OnChanged(nameof(HasArt));
        }
    }

    public bool HasArt => _art is not null;

    public bool IsNeutral => _tone == HudTone.Neutral;

    public void CopyFrom(HudItem other)
    {
        Glyph = other.Glyph;
        Title = other.Title;
        Status = other.Status;
        Tone = other.Tone;
        Battery = other.Battery;
        Level = other.Level;
        LevelText = other.LevelText;
        Art = other.Art;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Builds the standard HUD items.</summary>
internal static class HudItems
{
    public static HudItem Device(string glyph, string name, bool connected, int? battery = null) => new()
    {
        Glyph = glyph,
        Title = name,
        Status = connected ? "Connected" : "Disconnected",
        Tone = connected ? HudTone.Good : HudTone.Bad,
        Battery = connected ? battery : null,
    };

    public static HudItem Volume(string device, float level, bool muted) => new()
    {
        ReplaceKey = "volume",
        Glyph = DeviceRules.VolumeGlyph(level, muted),
        Title = device,
        Level = muted ? 0 : level,
        LevelText = muted ? "Muted" : $"{Math.Round(level * 100)}%",
    };

    public static HudItem Microphone(string device, bool muted) => new()
    {
        ReplaceKey = "mic",
        Glyph = muted ? Glyphs.MicrophoneOff : Glyphs.Microphone,
        Title = device,
        Status = muted ? "Muted" : "On",
        Tone = muted ? HudTone.Bad : HudTone.Good,
    };

    public static HudItem LockKey(string key, bool on) => new()
    {
        ReplaceKey = "lock:" + key,
        Glyph = Glyphs.Keyboard,
        Title = key,
        Status = on ? "On" : "Off",
        Tone = on ? HudTone.Good : HudTone.Neutral,
    };

    public static HudItem Brightness(int percent) => new()
    {
        ReplaceKey = "brightness",
        Glyph = Glyphs.Brightness,
        Title = "Brightness",
        Level = Math.Clamp(percent, 0, 100) / 100.0,
        LevelText = $"{Math.Clamp(percent, 0, 100)}%",
    };

    public static HudItem Media(string title, string artist, bool playing, ImageSource? art) => new()
    {
        ReplaceKey = "media",
        Glyph = playing ? Glyphs.Play : Glyphs.Pause,
        Title = title,
        Status = MediaStatus(artist, playing),
        Tone = playing ? HudTone.Good : HudTone.Neutral,
        Art = art,
    };

    public static string MediaStatus(string artist, bool playing)
    {
        var state = playing ? "Playing" : "Paused";
        return string.IsNullOrWhiteSpace(artist) ? state : $"{state} · {artist.Trim()}";
    }

    /// <summary>A general pill: icon, title, status dot and text.</summary>
    public static HudItem Info(string glyph, string title, string status, HudTone tone, string? replaceKey = null) => new()
    {
        ReplaceKey = replaceKey,
        Glyph = glyph,
        Title = DisplayText.Clean(title, 80),
        Status = DisplayText.Clean(status, 80),
        Tone = tone,
    };

    public static HudItem Message(string title, string status) => new()
    {
        Glyph = Glyphs.Checkmark,
        Title = title,
        Status = status,
        Tone = HudTone.Good,
    };

    public static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return fallback;
        }
    }
}
