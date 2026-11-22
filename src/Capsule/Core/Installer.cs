using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using Capsule.Settings;

namespace Capsule.Core;

/// <summary>
/// One-click install: the downloaded exe copies itself to %LOCALAPPDATA%\Programs\Capsule, starts from there, and
/// registers a Start menu shortcut, start with Windows and an entry in Settings > Apps. No admin rights needed.
/// </summary>
internal static class Installer
{
    public const string InstallArgument = "--install";
    public const string InstalledArgument = "--installed";
    public const string UninstallArgument = "--uninstall";
    public const string PortableArgument = "--portable";
    public const string QuietArgument = "--quiet";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Capsule";

    public static string InstallFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Capsule");

    public static string InstalledExe => Path.Combine(InstallFolder, "Capsule.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Capsule.lnk");

    /// <summary>True for the single-file release build (a developer build runs in place).</summary>
    private static bool IsReleaseBuild => string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);

    public static bool IsInstalledCopy => SamePath(Environment.ProcessPath, InstalledExe);

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool ShouldInstall(string[] args) =>
        IsReleaseBuild
        && !IsInstalledCopy
        && !args.Contains(PortableArgument, StringComparer.OrdinalIgnoreCase)
        && !args.Contains(AutoStart.StartupArgument, StringComparer.OrdinalIgnoreCase); // a portable copy started with Windows

    // ------------------------------------------------------------------ install

    public enum State { NotInstalled, Older, Same, Newer }

    /// <summary>How the installed copy (if any) compares with this one.</summary>
    public static State Compare()
    {
        if (!File.Exists(InstalledExe)) return State.NotInstalled;
        var installed = InstalledVersion();
        var running = Updater.CurrentVersion;
        if (installed is null || running is null) return State.Older;
        return installed < running ? State.Older : installed == running ? State.Same : State.Newer;
    }

    public static Version? InstalledVersion()
    {
        try
        {
            if (!File.Exists(InstalledExe)) return null;
            var info = FileVersionInfo.GetVersionInfo(InstalledExe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies this exe into the install folder and registers it (Settings > Apps, Start menu, start with Windows).
    /// Runs off the UI thread. Throws on failure.
    /// </summary>
    public static void Install(bool startWithWindows, Action quitRunningCopy, Func<bool> runningCopyGone)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path");

        var wasRunning = !runningCopyGone();
        quitRunningCopy();
        for (var i = 0; i < 100 && !runningCopyGone(); i++) Thread.Sleep(100);

        var temp = InstalledExe + ".new";
        var old = InstalledExe + ".old";
        var swapped = false;
        try
        {
            Directory.CreateDirectory(InstallFolder);

            // A plain byte copy: the installed program doesn't inherit the download's "from the internet" mark, so
            // Windows doesn't warn again at every sign-in. The person already chose to run this file.
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                from.CopyTo(to);
            }

            if (File.Exists(InstalledExe))
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(InstalledExe, old); // works even if a copy is still closing
                swapped = true;
            }

            File.Move(temp, InstalledExe);
        }
        catch
        {
            // Put the previous version back exactly as it was, and start it again if it was running.
            TryRun(() => File.Delete(temp));
            if (swapped && !File.Exists(InstalledExe)) TryRun(() => File.Move(old, InstalledExe));
            if (wasRunning && File.Exists(InstalledExe)) TryRun(() => LaunchInstalled(argument: null));
            throw;
        }

        WriteAppsEntry(InstalledExe);
        CreateShortcut();
        AutoStart.Set(startWithWindows, InstalledExe);
        Log.Info($"Installed Capsule {App.Version} to {InstallFolder}");
    }

    /// <summary>Starts the installed copy (it opens settings if a copy is already running).</summary>
    public static void LaunchInstalled(string? argument)
    {
        var start = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallFolder };
        if (argument is not null) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    /// <summary>The installed copy refreshes its Settings > Apps entry at start, so the version shown stays current.</summary>
    public static void RefreshAppsEntry()
    {
        if (IsInstalledCopy) WriteAppsEntry(InstalledExe);
    }

    private static void WriteAppsEntry(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Capsule");
            key.SetValue("DisplayVersion", App.Version);
            key.SetValue("Publisher", "Capsule contributors");
            key.SetValue("DisplayIcon", $"\"{exe}\",0");
            key.SetValue("InstallLocation", InstallFolder);
            key.SetValue("UninstallString", $"\"{exe}\" {UninstallArgument}");
            key.SetValue("QuietUninstallString", $"\"{exe}\" {UninstallArgument} {QuietArgument}");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            if (ProjectInfo.IsConfigured)
            {
                key.SetValue("URLInfoAbout", ProjectInfo.Url);
                key.SetValue("URLUpdateInfo", ProjectInfo.ReleasesUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not add Capsule to Settings > Apps", ex);
        }
    }

    private static void CreateShortcut()
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(InstalledExe);
            link.SetWorkingDirectory(InstallFolder);
            link.SetDescription("Capsule settings");
            link.SetIconLocation(InstalledExe, 0);
            ((IPersistFile)link).Save(ShortcutPath, true);
            Marshal.ReleaseComObject(link);
        }
        catch (Exception ex)
        {
            Log.Error("Could not add the Start menu shortcut", ex);
        }
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>Run from Settings > Apps > Capsule > Uninstall.</summary>
    public static void Uninstall(bool quiet, Action quitRunningCopy, Func<bool> runningCopyGone)
    {
        if (!quiet)
        {
            var answer = System.Windows.MessageBox.Show(
                "Remove Capsule and its settings from this PC?", "Uninstall Capsule",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        quitRunningCopy(); // it puts the Windows volume pop-up back as it closes
        for (var i = 0; i < 100 && !runningCopyGone(); i++) Thread.Sleep(100);

        AutoStart.Set(false);
        TryRun(() => File.Delete(ShortcutPath));
        TryRun(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        // Any copy other than the installed one (for example the downloaded setup file) removes the folder now.
        if (!IsInstalledCopy) TryRun(() => Directory.Delete(InstallFolder, recursive: true));
        TryRun(() => Directory.Delete(SettingsStore.Folder, recursive: true));
        TryRun(() => Directory.Delete(Log.Folder, recursive: true)); // last, so nothing writes the log again

        if (!quiet)
        {
            System.Windows.MessageBox.Show("Capsule has been removed.", "Uninstall Capsule",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        // The installed copy can't delete itself while running: a helper removes the folder once it has exited.
        if (IsInstalledCopy) DeleteInstallFolderAfterExit();
    }

    // A running program can't delete itself, so a hidden command prompt removes the folder once this copy has exited.
    private static void DeleteInstallFolderAfterExit()
    {
        var folder = InstallFolder;
        if (folder.IndexOfAny(['"', '%', '^', '&', '|', '<', '>']) >= 0) return;

        try
        {
            // Try once a second for up to a minute: this copy exits a moment after starting the helper.
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = $"/d /c for /l %i in (1,1,60) do @(ping -n 2 127.0.0.1 >nul & rd /s /q \"{folder}\" 2>nul & if not exist \"{folder}\" exit /b 0)",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove the program folder", ex);
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Install step failed", ex);
        }
    }

    // ------------------------------------------------------------------ shell link COM interop

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
