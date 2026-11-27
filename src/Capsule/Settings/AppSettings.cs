using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Capsule.Core;

namespace Capsule.Settings;

public enum HeadphoneMode
{
    /// <summary>Any headphones or headset (by the device type Windows reports).</summary>
    Any,

    /// <summary>Only the devices the user ticked.</summary>
    Selected,
}

public enum MicMode
{
    /// <summary>Keep the mic that was in use before Bluetooth headphones connected.</summary>
    KeepCurrent,

    /// <summary>Leave the microphone alone.</summary>
    LetWindowsDecide,

    /// <summary>Always go back to one specific microphone.</summary>
    Specific,
}

public enum HudPosition { BottomCenter, TopCenter, TopLeft, TopRight, BottomLeft, BottomRight }

public enum HudScreen { MousePointer, Primary }

public enum HudSize { Compact, Normal, Large }

public enum HudTheme { Dark, Light, System }

/// <summary>Look of the settings window and menus.</summary>
public enum AppTheme { System, Light, Dark }

public sealed class AppSettings
{
    /// <summary>Accent value meaning "use the Windows accent color" (the default).</summary>
    public const string WindowsAccent = "windows";
    public const string DefaultAccent = WindowsAccent;
    public const double MinSeconds = 1.0;
    public const double MaxSeconds = 6.0;
    private const int MaxNames = 64;
    private const int MaxNameLength = 256;

    public static readonly string[] AccentPresets =
    [
        "#34D17A", // mint
        "#4CC2FF", // sky
        "#8B7CFF", // violet
        "#FF8A5B", // coral
        "#F5C04A", // amber
        "#F06BB0", // pink
    ];

    public int Version { get; set; } = 1;

    // Audio switching
    public bool AutoSwitch { get; set; } = true;
    public HeadphoneMode HeadphoneMode { get; set; } = HeadphoneMode.Any;
    public List<string> SelectedHeadphones { get; set; } = [];
    public bool SwitchCommunications { get; set; } = true;

    /// <summary>Device to go back to when headphones disconnect; empty means "the one used before".</summary>
    public string FallbackDevice { get; set; } = "";

    public MicMode MicMode { get; set; } = MicMode.KeepCurrent;
    public string MicDevice { get; set; } = "";

    // HUD
    public bool HudEnabled { get; set; } = true;
    public bool HudHeadphones { get; set; } = true;
    public bool HudControllers { get; set; } = true;
    public bool HudVolume { get; set; } = true;
    public bool HudMicMute { get; set; } = true;
    public bool HudBattery { get; set; } = true;
    public bool HudLockKeys { get; set; } = true;
    public bool HudBrightness { get; set; } = true;
    public bool HudMedia { get; set; } = true;

    public bool HudKeyboard { get; set; } = true;
    public bool HudPrivacy { get; set; } = true;
    public bool HudUsb { get; set; } = true;
    public bool HudNetwork { get; set; } = true;

    // System alerts
    public bool HudCpuGpu { get; set; } = true;
    public bool HudHeat { get; set; } = true;
    public bool HudMemoryDisk { get; set; } = true;
    public bool HudPower { get; set; } = true;
    public int CpuAlertPercent { get; set; } = 90;
    public int GpuAlertPercent { get; set; } = 95;
    public int GpuTempAlertC { get; set; } = 85;
    public int MemoryAlertPercent { get; set; } = 90;
    public int DiskFreeAlertGb { get; set; } = 10;
    public int DeviceBatteryAlertPercent { get; set; } = 15;

    /// <summary>Experimental: keep the built-in Windows volume/brightness pop-up hidden.</summary>
    public bool HideWindowsOsd { get; set; } = true;

    /// <summary>Minutes without keyboard or mouse use before the keyboard switches back to Windows' default language; 0 = never.</summary>
    public int KeyboardBackMinutes { get; set; }

    /// <summary>The choices offered in the settings window (0 = never).</summary>
    public static readonly int[] KeyboardBackChoices = [0, 1, 2, 5, 10, 30];

    /// <summary>Hide a keyboard app's own Caps Lock / Num Lock pop-up so only Capsule's shows.</summary>
    public bool HideDuplicatePopups { get; set; } = true;

    public HudPosition HudPosition { get; set; } = HudPosition.BottomCenter;
    public HudScreen HudScreen { get; set; } = HudScreen.MousePointer;
    public HudSize HudSize { get; set; } = HudSize.Normal;
    public HudTheme HudTheme { get; set; } = HudTheme.Dark;
    public string HudAccent { get; set; } = DefaultAccent;
    public double HudSeconds { get; set; } = 2.0;

    // General
    public AppTheme AppTheme { get; set; } = AppTheme.System;

    /// <summary>Check GitHub once a day for a new version (installing always asks first).</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    // Controllers
    public bool IgnoreVirtualControllers { get; set; } = true;

    /// <summary>Apps taught with "Teach Capsule": their small pop-ups are hidden right after Capsule shows its own.</summary>
    public List<TaughtPopup> TaughtPopups { get; set; } = [];

    private const int MaxTaughtPopups = 16;

    /// <summary>Your own names for devices: Windows name -> name shown in the HUD and tray.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxCustomNameLength = 64;
    private const int MaxRenamedDevices = 128;

    /// <summary>The name to show for a device: yours if you renamed it, otherwise the Windows name.</summary>
    public string NameFor(string original) =>
        DeviceNames.TryGetValue(original, out var custom) && custom.Length > 0 ? custom : original;

    /// <summary>Sets or clears (empty / same as the Windows name) a custom name.</summary>
    public void Rename(string original, string? custom)
    {
        var key = CleanName(original);
        if (key.Length == 0) return;

        var value = CleanCustomName(custom);
        if (value.Length == 0 || string.Equals(value, key, StringComparison.Ordinal))
        {
            DeviceNames.Remove(key);
        }
        else if (DeviceNames.ContainsKey(key) || DeviceNames.Count < MaxRenamedDevices)
        {
            DeviceNames[key] = value;
        }
    }

    /// <summary>Repairs anything out of range so a hand-edited or damaged file can never break the app.</summary>
    public AppSettings Normalize()
    {
        TaughtPopups = (TaughtPopups ?? [])
            .Where(t => t is not null)
            .Select(t => new TaughtPopup
            {
                App = DisplayText.Clean(t.App, 60),
                ExePath = (t.ExePath ?? "").Trim(),
                WindowClass = (t.WindowClass ?? "").Trim(),
                Width = t.Width,
                Height = t.Height,
            })
            .Where(t => t.IsValid)
            .DistinctBy(t => (t.ExePath.ToUpperInvariant(), t.WindowClass))
            .Take(MaxTaughtPopups)
            .ToList();

        SelectedHeadphones = CleanNames(SelectedHeadphones);
        FallbackDevice = CleanName(FallbackDevice);
        MicDevice = CleanName(MicDevice);

        if (!Enum.IsDefined(HeadphoneMode)) HeadphoneMode = HeadphoneMode.Any;
        if (!Enum.IsDefined(MicMode)) MicMode = MicMode.KeepCurrent;
        if (!Enum.IsDefined(HudPosition)) HudPosition = HudPosition.BottomCenter;
        if (!Enum.IsDefined(HudScreen)) HudScreen = HudScreen.MousePointer;
        if (!Enum.IsDefined(HudSize)) HudSize = HudSize.Normal;
        if (!Enum.IsDefined(HudTheme)) HudTheme = HudTheme.Dark;
        if (!Enum.IsDefined(AppTheme)) AppTheme = AppTheme.System;
        if (MicMode == MicMode.Specific && MicDevice.Length == 0) MicMode = MicMode.KeepCurrent;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (original, custom) in DeviceNames ?? [])
        {
            var key = CleanName(original);
            var value = CleanCustomName(custom);
            if (key.Length == 0 || value.Length == 0 || names.Count >= MaxRenamedDevices) continue;
            names[key] = value;
        }

        DeviceNames = names;

        CpuAlertPercent = Math.Clamp(CpuAlertPercent, 50, 100);
        GpuAlertPercent = Math.Clamp(GpuAlertPercent, 50, 100);
        GpuTempAlertC = Math.Clamp(GpuTempAlertC, 50, 110);
        MemoryAlertPercent = Math.Clamp(MemoryAlertPercent, 50, 99);
        DiskFreeAlertGb = Math.Clamp(DiskFreeAlertGb, 1, 500);
        DeviceBatteryAlertPercent = Math.Clamp(DeviceBatteryAlertPercent, 5, 50);
        // Only the choices the settings window offers, so what it shows is always what's in effect.
        KeyboardBackMinutes = KeyboardBackMinutes <= 0 ? 0 : KeyboardBackChoices.Skip(1).MinBy(c => Math.Abs((long)c - KeyboardBackMinutes));

        if (IsWindowsAccent(HudAccent)) HudAccent = WindowsAccent;
        else if (!IsColor(HudAccent)) HudAccent = DefaultAccent;
        HudSeconds = double.IsFinite(HudSeconds) ? Math.Clamp(HudSeconds, MinSeconds, MaxSeconds) : 2.0;
        Version = 1;
        return this;
    }

    public static bool IsWindowsAccent(string? value) =>
        string.Equals(value, WindowsAccent, StringComparison.OrdinalIgnoreCase);

    public static bool IsColor(string? value) =>
        value is not null && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");

    // Device names are cleaned the same way when they are read from Windows, so stored names still match.
    private static string CleanName(string? name) => DisplayText.Clean(name, MaxNameLength);

    private static string CleanCustomName(string? name) => DisplayText.Clean(name, MaxCustomNameLength);

    private static List<string> CleanNames(List<string>? names) =>
        (names ?? [])
            .Select(CleanName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNames)
            .ToList();
}

/// <summary>Reads and writes %APPDATA%\Capsule\settings.json.</summary>
public static class SettingsStore
{
    private const long MaxFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capsule");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static bool Exists => File.Exists(FilePath);

    public static AppSettings Load() => Load(FilePath);

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var text = ReadBounded(path);
            if (text is null)
            {
                Log.Error("Settings file is too large; using defaults");
                return new AppSettings();
            }

            return Parse(text);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // Probably locked for a moment (sync tool, antivirus): use defaults now, but don't mark the file bad.
            Log.Error("Settings file is busy; using defaults for now", ex);
            return new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings file could not be read; using defaults", ex);
            TryKeepBrokenCopy(path);
            return new AppSettings();
        }
    }

    public static AppSettings Parse(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings()).Normalize();

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Json);

    public static void Save(AppSettings settings) => Save(settings, FilePath);

    public static void Save(AppSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(settings));
            File.Move(temp, path, overwrite: true); // atomic replace: never leaves a half-written file
        }
        catch (Exception ex)
        {
            Log.Error("Settings could not be saved", ex);
        }
    }

    // Reads through one handle and counts the bytes actually read, retrying briefly if the file is locked.
    private static string? ReadBounded(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[MaxFileBytes + 1];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                if (total > MaxFileBytes) return null;
                // Notepad and PowerShell may add a byte-order mark; skip it.
                var start = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
                return new System.Text.UTF8Encoding(false).GetString(buffer, start, total - start);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(150);
            }
        }
    }

    private static void TryKeepBrokenCopy(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bad", overwrite: true);
        }
        catch
        {
            // Best effort only.
        }
    }
}

/// <summary>
/// One app whose own pop-up the user asked Capsule to hide: matched by its full program path, window class and size.
/// </summary>
public sealed class TaughtPopup
{
    public string App { get; set; } = "";

    public string ExePath { get; set; } = "";

    public string WindowClass { get; set; } = "";

    /// <summary>The pop-up's size at 100% scaling, so other small windows of the same app aren't mistaken for it.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        App.Length > 0
        && WindowClass.Length is > 0 and <= 256
        && WindowClass != Core.DuplicatePopupHider.DialogClass
        && Width is > 0 and <= Core.DuplicatePopupHider.MaxPopupSize
        && Height is > 0 and <= Core.DuplicatePopupHider.MaxPopupSize
        && ExePath.Length <= 1024
        && Path.IsPathFullyQualified(ExePath)
        && ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !ExePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase);

    public bool Matches(string exePath, string windowClass) =>
        string.Equals(WindowClass, windowClass, StringComparison.Ordinal)
        && string.Equals(ExePath, exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// About the taught size: a pop-up's text can make it a little wider or narrower, but a picture-in-picture window or
    /// a notification of the same app is a different size and is left alone.
    /// </summary>
    public bool FitsSize(int width, int height) =>
        Math.Abs(width - Width) <= Width / 4 + 16 && Math.Abs(height - Height) <= Height / 4 + 16;
}
