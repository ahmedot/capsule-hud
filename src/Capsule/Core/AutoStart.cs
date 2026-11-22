using Microsoft.Win32;

namespace Capsule.Core;

/// <summary>Start with Windows through the per-user Run key (no admin rights, no scheduled task).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Capsule";
    public const string StartupArgument = "--startup";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Capsule.exe");

    private static string Command => $"\"{ExePath}\" {StartupArgument}";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the startup setting", ex);
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
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
