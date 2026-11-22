using System.Globalization;
using Microsoft.Win32;
using Windows.Gaming.Input;
using Capsule.Audio;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Core;

/// <summary>
/// What a quick look at this PC found. Used only to ask the right setup questions in the installer; it is never saved
/// or sent anywhere.
/// </summary>
internal sealed record PcProfile(
    string? Headphones,
    string? GamingHint,
    IReadOnlyList<string> Languages,
    string? KeyboardApp,
    bool IsLaptop)
{
    /// <summary>Sample answers for screenshots, so no real device names appear.</summary>
    public static PcProfile Sample { get; } = new("Studio Headphones", "a game controller", ["English", "العربية"], null, false);
}

internal static class PcScan
{
    private const ushort SteamVirtualVendorId = 10462;

    /// <summary>Takes well under a second; every part is optional and simply left out if it fails.</summary>
    public static PcProfile Run() => new(
        Try(FindHeadphones),
        Try(FindGaming),
        Try(FindLanguages) ?? [],
        Try(() => DuplicatePopupHider.DetectApp()?.Name),
        Try(() => PowerWatcher.HasBattery));

    private static string? FindHeadphones()
    {
        using var audio = new AudioSystem();
        var rules = new AppSettings();
        // Connected headphones first, then ones that are paired or plugged in later.
        foreach (var states in new[] { DeviceState.Active, DeviceState.Unplugged, DeviceState.NotPresent })
        {
            var found = audio.GetEndpoints(EDataFlow.Render, states).FirstOrDefault(e => DeviceRules.IsHeadphones(e, rules));
            if (found is null) continue;
            // Windows labels a re-paired device "2- Name"; people know it as just "Name".
            var name = System.Text.RegularExpressions.Regex.Replace(DisplayText.Clean(found.ShortName, 40), @"^\d+-\s*", "");
            return name.Length > 0 ? name : null;
        }

        return null;
    }

    private static string? FindGaming()
    {
        // Windows fills the controller list a moment after it is first asked for.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (RawGameController.RawGameControllers.Any(c => c.HardwareVendorId != SteamVirtualVendorId)) return "a game controller";
            if (attempt == 0) Thread.Sleep(500);
        }

        using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return steam is null ? null : "Steam";
    }

    private static List<string> FindLanguages()
    {
        var names = new List<string>();
        foreach (System.Windows.Forms.InputLanguage input in System.Windows.Forms.InputLanguage.InstalledInputLanguages)
        {
            try
            {
                var culture = input.Culture;
                var neutral = culture.IsNeutralCulture || culture.Parent == CultureInfo.InvariantCulture ? culture : culture.Parent;
                var name = DisplayText.Clean(neutral.NativeName, 40);
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            catch (CultureNotFoundException)
            {
                // One unknown layout shouldn't hide the others.
            }
        }

        return names;
    }

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Error("Part of the setup scan failed", ex);
            return default;
        }
    }
}
