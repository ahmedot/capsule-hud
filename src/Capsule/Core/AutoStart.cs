using Microsoft.Win32;

namespace Capsule.Core;

/// <summary>Start with Windows through the per-user Run key (no admin rights, no scheduled task).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Where Task Manager's and Settings' "Startup apps" switch keeps its on/off flag for each Run entry.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "Capsule";
    public const string StartupArgument = "--startup";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Capsule.exe");

    private static string Command => CommandFor(ExePath);

    private static string CommandFor(string exe) => $"\"{exe}\" {StartupArgument}";

    public static bool IsEnabled() => IsEnabledFor(ExePath);

    /// <summary>
    /// Whether start with Windows is on for the given exe (the installer asks about the installed copy): the Run entry
    /// points at it, and it hasn't been switched off in Task Manager or Settings › Apps › Startup.
    /// </summary>
    public static bool IsEnabledFor(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string value
                || !string.Equals(value, CommandFor(exePath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return !IsSwitchedOff(approved?.GetValue(ValueName) as byte[]);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the startup setting", ex);
            return false;
        }
    }

    /// <summary>Turns start with Windows on or off, for this exe or the one given (used by the installer).</summary>
    public static void Set(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, exePath is null ? Command : CommandFor(exePath), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // Turning it on in Capsule is the person's latest choice, so it also undoes a switch-off in Task Manager.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            {
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info($"Start with Windows: {(enabled ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the startup setting", ex);
        }
    }

    /// <summary>
    /// Windows' "Startup apps" flag: the first byte is even (2 or 6) when the entry is on and odd (3 or 7) when it was
    /// switched off. No flag means on.
    /// </summary>
    internal static bool IsSwitchedOff(byte[]? flag) => flag is { Length: > 0 } && (flag[0] & 1) == 1;
}
