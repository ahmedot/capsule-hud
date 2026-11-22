using Microsoft.Win32;

namespace Capsule.Core;

/// <summary>Start with Windows through the per-user Run key (no admin rights, no scheduled task).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Capsule";
    public const string StartupArgument = "--startup";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Capsule.exe");

    private static string Command => CommandFor(ExePath);

    private static string CommandFor(string exe) => $"\"{exe}\" {StartupArgument}";

    public static bool IsEnabled() => IsEnabledFor(ExePath);

    /// <summary>Whether start with Windows is on for the given exe (the installer asks about the installed copy).</summary>
    public static bool IsEnabledFor(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && string.Equals(value, CommandFor(exePath), StringComparison.OrdinalIgnoreCase);
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

            Log.Info($"Start with Windows: {(enabled ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the startup setting", ex);
        }
    }
}
