using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Capsule.Core;

/// <summary>
/// Reports Caps Lock and Num Lock turning on or off. It only reads the on/off state of those two keys
/// a few times a second; it never sees what you type.
/// </summary>
internal sealed class LockKeyWatcher : IDisposable
{
    private const int CapsLockKey = 0x14;
    private const int NumLockKey = 0x90;

    private readonly DispatcherTimer _timer;
    private bool _caps;
    private bool _num;

    public LockKeyWatcher(Dispatcher ui)
    {
        _caps = IsOn(CapsLockKey);
        _num = IsOn(NumLockKey);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Check(), ui);
    }

    /// <summary>Key name and its new state.</summary>
    public event Action<string, bool>? Changed;

    public void Dispose() => _timer.Stop();

    private void Check()
    {
        var caps = IsOn(CapsLockKey);
        if (caps != _caps)
        {
            _caps = caps;
            Changed?.Invoke("Caps Lock", caps);
        }

        var num = IsOn(NumLockKey);
        if (num != _num)
        {
            _num = num;
            Changed?.Invoke("Num Lock", num);
        }
    }

    private static bool IsOn(int key) => (GetKeyState(key) & 1) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
