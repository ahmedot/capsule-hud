using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Capsule.Core;

/// <summary>
/// Switches the keyboard back to your usual language (Windows' default input language) once you haven't used the
/// keyboard or mouse for a while, for people who switch to another language now and then. While it's on, it looks once
/// every 30 seconds at how long ago the last key press or mouse move was (Windows' idle time; it never sees which keys)
/// and at the active window's language. There is no keyboard hook and nothing runs while the option is off.
/// </summary>
internal sealed class KeyboardReturn : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private const int WmInputLangChangeRequest = 0x0050;
    private const int MaxUnlockTries = 15; // every 2 s, so about 30 s
    private static readonly string[] ShellWindows = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly DispatcherTimer _timer;
    private readonly Func<int> _minutes;
    private readonly DispatcherTimer? _unlockTimer;
    private uint _handledIdleSince; // the idle stretch already acted on, so a window that refuses is asked only once
    private int _unlockTries;

    /// <param name="minutes">Minutes without keyboard or mouse use before switching back; 0 or less does nothing.</param>
    /// <param name="switchNow">Switch back soon whatever the idle time (the PC was locked long enough).</param>
    public KeyboardReturn(Dispatcher ui, Func<int> minutes, bool switchNow = false)
    {
        _minutes = minutes;
        _timer = new DispatcherTimer(CheckEvery, DispatcherPriority.Background, (_, _) => Check(force: false), ui);
        if (!switchNow) return;

        // Give the desktop a moment to come back after unlocking, and wait while only the desktop or taskbar is in
        // front: the app you return to is the one to switch.
        _unlockTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            if (Check(force: true) || ++_unlockTries >= MaxUnlockTries) _unlockTimer?.Stop();
        }, ui);
    }

    public void Dispose()
    {
        _timer.Stop();
        _unlockTimer?.Stop();
    }

    /// <summary>
    /// Whether to switch now: the active window's language isn't the usual one, the PC has been idle for long enough,
    /// and this idle stretch hasn't been acted on yet. Languages come from <see cref="LanguageKey"/>, so English (UK)
    /// and English (US) both count as English. An unknown language (empty) never switches.
    /// </summary>
    public static bool ShouldSwitch(TimeSpan idle, int minutes, string current, string usual, bool alreadyHandled) =>
        minutes > 0 && current.Length > 0 && usual.Length > 0 && !alreadyHandled
        && !string.Equals(current, usual, StringComparison.OrdinalIgnoreCase) && idle >= TimeSpan.FromMinutes(minutes);

    /// <summary>Windows' default input language and its name in English; null when it can't be read.</summary>
    public static (IntPtr Layout, string Name)? Usual()
    {
        var layout = IntPtr.Zero;
        if (!SystemParametersInfoW(SpiGetDefaultInputLang, 0, ref layout, 0) || layout == IntPtr.Zero) return null;
        return (layout, LanguageName(layout));
    }

    /// <summary>How many different keyboard languages are installed (two layouts of one language count once).</summary>
    public static int LanguageCount()
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return 0;
        var layouts = new IntPtr[count];
        count = GetKeyboardLayoutList(count, layouts);
        return layouts.Take(Math.Max(count, 0)).Select(LanguageKey).Where(k => k.Length > 0).Distinct().Count();
    }

    /// <summary>
    /// A keyboard layout's language without its country, as a culture name: "en" for both English (US) and (UK), while
    /// "sr-Latn" and "sr-Cyrl", or "zh-Hans" and "zh-Hant", stay apart. A language Windows has no fixed id for gets a key
    /// of its own; an empty layout gives "".
    /// </summary>
    public static string LanguageKey(IntPtr layout)
    {
        var id = LanguageId(layout);
        return id == 0 ? "" : Neutral(id)?.Name ?? $"#{id:X4}";
    }

    /// <summary>The layout's language in plain English ("English", "Arabic"), or a general phrase when unknown.</summary>
    public static string LanguageName(IntPtr layout) =>
        Neutral(LanguageId(layout)) is { } culture ? DisplayText.Clean(culture.EnglishName, 40) : "your usual language";

    private static ushort LanguageId(IntPtr layout) => (ushort)((long)layout & 0xFFFF);

    private static CultureInfo? Neutral(ushort id)
    {
        if (id == 0) return null;
        try
        {
            var culture = CultureInfo.GetCultureInfo(id);
            // Temporary ids (0x2000, 0x2400...) can come back as some other language, such as English (United States):
            // never mistake them for it.
            if (culture.LCID != id) return null;
            while (!culture.IsNeutralCulture && culture.Parent.Name.Length > 0) culture = culture.Parent;
            return culture.Name.Length > 0 ? culture : null;
        }
        catch (ArgumentException)
        {
            // Also CultureNotFoundException: ids such as 0x2000 or 0x1000 (languages added by tag) have no culture.
            return null;
        }
    }

    /// <summary>Returns false only while forced and there's no app window in front yet, so the unlock check retries.</summary>
    private bool Check(bool force)
    {
        try
        {
            var minutes = _minutes();
            if (minutes <= 0) return true;

            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return true;
            var idle = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));

            var window = GetForegroundWindow();
            if (window == IntPtr.Zero || IsShellWindow(window)) return !force;
            if (Usual() is not { } usual) return true;
            var current = LanguageKey(GetKeyboardLayout(GetWindowThreadProcessId(window, out _)));

            var handled = info.Time == _handledIdleSince && !force;
            if (!ShouldSwitch(force ? TimeSpan.MaxValue : idle, minutes, current, LanguageKey(usual.Layout), handled)) return true;

            _handledIdleSince = info.Time;
            // The same request Windows sends when you switch language: the window changes its own keyboard. Windows
            // refuses it for apps running as administrator, which then simply keep their language.
            var why = force ? "after the PC was locked" : $"after {minutes} min without keyboard or mouse use";
            if (PostMessageW(window, WmInputLangChangeRequest, IntPtr.Zero, usual.Layout)) Log.Info($"Keyboard switched back to {usual.Name} {why}");
            else Log.Info($"The active window didn't accept switching the keyboard back to {usual.Name}");
        }
        catch (Exception ex)
        {
            Log.Error("Switching the keyboard back failed", ex);
        }

        return true;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        var name = new char[64];
        var length = GetClassNameW(window, name, name.Length);
        return length > 0 && ShellWindows.Contains(new string(name, 0, length), StringComparer.Ordinal);
    }

    private const uint SpiGetDefaultInputLang = 0x0059;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, IntPtr[]? layouts);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref IntPtr value, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, char[] name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
