using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Capsule.Settings;

namespace Capsule.Core;

/// <summary>A small pop-up from another app, found while teaching Capsule. Width and height are at 100% scaling.</summary>
internal sealed record PopupCandidate(IntPtr Window, string App, string ExePath, string WindowClass, int Width, int Height);

/// <summary>
/// Some keyboard apps show their own pop-up next to Capsule's. Right after Capsule shows its own (and only then) this
/// hides the duplicate for a moment: the built-in keyboard apps after a lock-key change, and apps the user taught after
/// a lock-key, volume, mic or brightness change. Nothing runs in the background between those moments.
/// </summary>
internal sealed class DuplicatePopupHider : IDisposable
{
    /// <summary>A keyboard app's pop-up: the process that owns it and a piece of its window class.</summary>
    private sealed record KnownPopup(string ProcessName, string ClassContains, string AppName, string SettingsExe);

    private static readonly KnownPopup[] Known =
    [
        new("logioptionsplus_agent", "QWindowIcon", "Logi Options+", "logioptionsplus.exe"),
    ];

    private const long ArmForMs = 1500;
    internal const int MaxPopupSize = 600; // pixels at 100% scaling; the pop-ups are small, the apps' main windows are not

    // The standard Windows dialog class. Many apps use it for their own small dialogs, so a rule for it would hide
    // more than the one pop-up the user picked.
    internal const string DialogClass = "#32770";

    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<TaughtPopup>> _taught;

    // Milliseconds on the system's uptime clock, which changing the date or time can't move.
    private long _until;
    private long _lockKeyUntil;

    public DuplicatePopupHider(Dispatcher ui, Func<IReadOnlyList<TaughtPopup>> taught)
    {
        _taught = taught;
        _timer = new DispatcherTimer(DispatcherPriority.Send, ui) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            HideNow();
            if (Environment.TickCount64 >= _until) _timer.Stop();
        };
    }

    public bool Enabled { get; set; }

    /// <summary>
    /// Capsule has just shown its own pop-up: hide a duplicate now and for the next moment. Lock keys cover the
    /// built-in keyboard apps; volume, mic and brightness only matter for apps the user taught.
    /// </summary>
    public void Arm(bool lockKey = true)
    {
        if (!Enabled || (!lockKey && _taught().Count == 0)) return;
        _until = Environment.TickCount64 + ArmForMs;
        if (lockKey) _lockKeyUntil = _until; // the built-in keyboard apps only show lock-key pop-ups
        HideNow();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>
    /// The keyboard app, if it runs in this user's session from an installed location. Only a process in the current
    /// session whose program sits under Program Files (which only administrators can change) counts, so a look-alike
    /// process started from a folder other people or lower-trust programs can write never gets an "Open" button.
    /// </summary>
    public static (string Name, string? SettingsPath)? DetectApp()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var app in Known)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(app.ProcessName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != session) continue;
                        var image = ImagePath((uint)process.Id);
                        if (image is null || !IsInstalledLocation(image)) continue;
                        var settings = Path.Combine(Path.GetDirectoryName(image)!, app.SettingsExe);
                        return (app.AppName, File.Exists(settings) ? settings : null);
                    }
                    catch
                    {
                        // Can't look at that process: skip it.
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the keyboard app's settings program. The path is checked again at click time, so only an installed program
    /// under Program Files is ever started, and it starts in its own folder rather than the one Capsule was run from.
    /// </summary>
    public static void OpenSettingsApp(string? path)
    {
        if (path is null || !File.Exists(path) || !IsInstalledLocation(path)) return;
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the keyboard app", ex);
        }
    }

    /// <summary>True for a file under Program Files (64- or 32-bit), where only administrators can write.</summary>
    public static bool IsInstalledLocation(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // Not a usable path.
        }

        return false;
    }

    private void HideNow()
    {
        var taught = _taught();
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window)) return true;
                var className = ClassOf(window);
                if (className.Length == 0) return true;
                var known = Environment.TickCount64 < _lockKeyUntil
                    ? Known.FirstOrDefault(k => className.Contains(k.ClassContains, StringComparison.Ordinal))
                    : null;
                // Taught apps: only their small always-on-top windows, like the one the user picked.
                var classTaught = IsTopmost(window) && taught.Any(t => string.Equals(t.WindowClass, className, StringComparison.Ordinal));
                if ((known is null && !classTaught) || !IsSmall(window)) return true;

                GetWindowThreadProcessId(window, out var pid);
                var image = ImagePath(pid);
                if (image is null) return true;
                var isKnown = known is not null
                    && string.Equals(Path.GetFileNameWithoutExtension(image), known.ProcessName, StringComparison.OrdinalIgnoreCase);
                var isTaught = classTaught && TrySize(window, out var width, out var height)
                    && taught.Any(t => t.Matches(image, className) && t.FitsSize(width, height));
                if (isKnown || isTaught) ShowWindow(window, SwHide);
            }
            catch
            {
                // A window that vanished mid-check is fine to skip.
            }

            return true;
        }, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ teaching

    /// <summary>The visible top-level windows right now (the "before" picture while teaching).</summary>
    public static HashSet<IntPtr> VisibleWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window)) set.Add(window);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// Small always-on-top windows that appeared since <paramref name="before"/> and belong to another app: not Capsule,
    /// no program from the Windows folder, no standard dialog, and not an app Capsule already handles.
    /// </summary>
    public static List<PopupCandidate> FindNewPopups(HashSet<IntPtr> before, IReadOnlyList<TaughtPopup> taught)
    {
        var found = new List<PopupCandidate>();
        var self = (uint)Environment.ProcessId;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        EnumWindows((window, _) =>
        {
            try
            {
                if (before.Contains(window) || !IsWindowVisible(window) || !IsSmall(window)) return true;
                if (!IsTopmost(window)) return true;
                GetWindowThreadProcessId(window, out var pid);
                if (pid == self) return true;
                var image = ImagePath(pid);
                if (image is null || image.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase)) return true;
                var className = ClassOf(window);
                if (className.Length == 0 || className == DialogClass) return true;
                if (Known.Any(k => className.Contains(k.ClassContains, StringComparison.Ordinal)
                                   && string.Equals(Path.GetFileNameWithoutExtension(image), k.ProcessName, StringComparison.OrdinalIgnoreCase))) return true;
                if (taught.Any(t => t.Matches(image, className))) return true;
                if (found.Any(f => string.Equals(f.ExePath, image, StringComparison.OrdinalIgnoreCase) && f.WindowClass == className)) return true;
                if (!TrySize(window, out var width, out var height)) return true;
                found.Add(new PopupCandidate(window, FriendlyName(image), image, className, width, height));
            }
            catch
            {
                // Skip windows that vanish or can't be read.
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Hides the pop-up the user just taught, if it is still showing. The window is checked again first (same program,
    /// same class, still small and on top) because it may have closed and its handle gone to another window since.
    /// </summary>
    public static void HideOne(PopupCandidate popup)
    {
        var window = popup.Window;
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return;
        if (ClassOf(window) != popup.WindowClass || !IsSmall(window) || !IsTopmost(window)) return;
        GetWindowThreadProcessId(window, out var pid);
        if (!string.Equals(ImagePath(pid), popup.ExePath, StringComparison.OrdinalIgnoreCase)) return;
        ShowWindow(window, SwHide);
    }

    private static string FriendlyName(string exe)
    {
        // Version text is only read from a program on a drive letter, the same rule the camera and mic alerts use.
        if (OnDriveLetter(exe))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                foreach (var name in new[] { info.FileDescription, info.ProductName })
                {
                    var clean = DisplayText.Clean(name, 60);
                    if (clean.Length > 0) return clean;
                }
            }
            catch
            {
                // Fall back to the file name.
            }
        }

        return DisplayText.Clean(Path.GetFileNameWithoutExtension(exe), 60);
    }

    private static bool OnDriveLetter(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) == 0 ? "" : name.ToString();
    }

    private static bool IsSmall(IntPtr window) =>
        TrySize(window, out var width, out var height) && width <= MaxPopupSize && height <= MaxPopupSize;

    // The window's size at 100% scaling. It's measured against its screen's scaling rather than the window's own,
    // because Windows stretches apps that don't handle scaling themselves, so they look bigger than they think they are.
    private static bool TrySize(IntPtr window, out int width, out int height)
    {
        width = height = 0;
        if (!GetWindowRect(window, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
        var dpi = GetDpiForMonitor(MonitorFromWindow(window, MonitorDefaultToNearest), EffectiveDpi, out var x, out _) == 0 && x > 0
            ? (int)x
            : 96;
        width = (r.Right - r.Left) * 96 / dpi;
        height = (r.Bottom - r.Top) * 96 / dpi;
        return true;
    }

    private static bool IsTopmost(IntPtr window) => (GetWindowLongW(window, GwlExStyle) & WsExTopmost) != 0;

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0; // MDT_EFFECTIVE_DPI
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x8;
    private const int SwHide = 0;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
