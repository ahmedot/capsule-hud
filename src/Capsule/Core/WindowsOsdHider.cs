using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Capsule.Core;

/// <summary>
/// Experimental: keeps the built-in Windows volume/brightness pop-up out of the way so only Capsule's HUD shows.
///
/// Windows offers no setting for this. The pop-up lives in a small, untitled shell window
/// (class "XamlExplorerHostIslandWindow" on Windows 11, "NativeHWNDHost" on Windows 10) owned by explorer.exe.
/// The same window class is used for other shell surfaces (for example the full-screen task switcher), so we
/// only ever touch a window that is small, untitled, owned by Explorer, and that appeared right after a
/// volume, brightness or media change. Once found, that one window is kept minimized. Turning the feature off
/// (or quitting) puts it back.
/// </summary>
internal sealed class WindowsOsdHider : IDisposable
{
    private const string Windows11Host = "XamlExplorerHostIslandWindow";
    private const string Windows11Bridge = "Windows.UI.Composition.DesktopWindowContentBridge";
    private const string Windows10Host = "NativeHWNDHost";
    private const string Windows10Child = "DirectUIHWND";
    private const int ShowMinimized = 6;      // SW_MINIMIZE
    private const int ShowRestored = 9;       // SW_RESTORE
    private const int Hide = 0;               // SW_HIDE
    private static readonly TimeSpan ArmedFor = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _fast;   // while a pop-up may be appearing
    private readonly DispatcherTimer _steady; // keeps the found window minimized
    private IntPtr _osdHost;
    private uint _osdOwner;
    private string _osdClass = "";
    private DateTime _armedUntil;
    private bool _enabled;

    public WindowsOsdHider(Dispatcher ui)
    {
        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (_, _) => FastTick(), ui);
        _fast.Stop();
        _steady = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => KeepHidden(), ui);
        _steady.Stop();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            _steady.Start();
            Log.Info("Hiding the Windows volume pop-up (experimental)");
        }
        else
        {
            _fast.Stop();
            _steady.Stop();
            Release();
        }
    }

    /// <summary>Call when volume, brightness or media changed: the Windows pop-up is about to appear.</summary>
    public void Arm()
    {
        if (!_enabled) return;
        _armedUntil = DateTime.UtcNow + ArmedFor;
        KeepHidden();
        _fast.Start();
    }

    public void Dispose() => SetEnabled(false);

    private void FastTick()
    {
        if (DateTime.UtcNow > _armedUntil)
        {
            _fast.Stop();
            return;
        }

        if (_osdHost == IntPtr.Zero || !IsWindow(_osdHost))
        {
            _osdHost = FindVisiblePopup();
            if (_osdHost != IntPtr.Zero)
            {
                GetWindowThreadProcessId(_osdHost, out _osdOwner);
                _osdClass = ClassOf(_osdHost);
                Log.Info("Found the Windows volume pop-up window");
            }
        }

        KeepHidden();
    }

    private void KeepHidden()
    {
        if (_osdHost == IntPtr.Zero) return;
        if (!IsWindow(_osdHost))
        {
            _osdHost = IntPtr.Zero; // Explorer restarted; it will be found again on the next change
            return;
        }

        // Re-check it is still the same Explorer window before touching it.
        if (!IsSameWindow(_osdHost))
        {
            _osdHost = IntPtr.Zero;
            return;
        }

        if (IsWindowVisible(_osdHost) && !IsIconic(_osdHost)) ShowWindow(_osdHost, ShowMinimized);
    }

    private bool IsSameWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return pid == _osdOwner && ClassOf(window) == _osdClass;
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    // Explorer by its real location, not just by process name.
    private static bool IsExplorer(uint pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return false;
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return string.Equals(buffer.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private void Release()
    {
        if (_osdHost != IntPtr.Zero && IsWindow(_osdHost) && IsSameWindow(_osdHost) && IsIconic(_osdHost))
        {
            // Back to how Windows keeps it between pop-ups: restored but hidden.
            ShowWindow(_osdHost, ShowRestored);
            ShowWindow(_osdHost, Hide);
        }

        _osdHost = IntPtr.Zero;
    }

    private static IntPtr FindVisiblePopup()
    {
        var found = Find(Windows11Host, Windows11Bridge);
        return found != IntPtr.Zero ? found : Find(Windows10Host, Windows10Child);
    }

    private static IntPtr Find(string hostClass, string childClass)
    {
        var window = IntPtr.Zero;
        while ((window = FindWindowExW(IntPtr.Zero, window, hostClass, null)) != IntPtr.Zero)
        {
            if (!IsWindowVisible(window) || IsIconic(window)) continue;
            if (FindWindowExW(window, IntPtr.Zero, childClass, null) == IntPtr.Zero) continue;
            if (GetWindowTextLengthW(window) != 0) continue;

            GetWindowThreadProcessId(window, out var pid);
            if (!IsExplorer(pid)) continue;

            // The pop-up is small. Full-screen shell surfaces (task switcher, etc.) are never touched.
            if (!GetWindowRect(window, out var rect)) continue;
            var screen = Interop.NativeMethods.GetWorkArea(screenWithMouse: false);
            if (rect.Right - rect.Left > screen.Width / 3 || rect.Bottom - rect.Top > screen.Height / 4) continue;

            return window;
        }

        return IntPtr.Zero;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
