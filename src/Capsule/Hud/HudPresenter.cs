using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Hud;

/// <summary>
/// Shows HUD items one at a time: the pill opens from its icon, holds, then closes back into the icon.
/// A new item waiting in line closes the current pill and opens the next the same way.
/// Live items (volume, brightness) update the open pill in place.
/// </summary>
internal sealed class HudPresenter
{
    private const int MaxQueue = 4;
    private const double EdgeGap = 4; // extra gap from the screen edge, on top of the shadow margin

    private readonly Func<AppSettings> _settings;
    private readonly HudWindow _window = new();
    private readonly Queue<HudItem> _queue = new();
    private readonly DispatcherTimer _hold;
    private HudItem? _current;
    private bool _visible;
    private bool _closing;
    private int _generation;

    public HudPresenter(Func<AppSettings> settings)
    {
        _settings = settings;
        _hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hold.Tick += (_, _) => OnHoldElapsed();
        new WindowInteropHelper(_window).EnsureHandle();
        _window.SizeChanged += (_, _) =>
        {
            if (_visible) Position();
        };
    }

    public void Show(HudItem item)
    {
        if (!_settings().HudEnabled) return;

        if (item.ReplaceKey is not null && !_closing && _current?.ReplaceKey == item.ReplaceKey)
        {
            _current.CopyFrom(item); // update the open pill in place
            RestartHold();
            return;
        }

        if (_current is null && !_closing)
        {
            Open(item);
        }
        else if (item.ReplaceKey is not null || _current?.ReplaceKey is not null)
        {
            // Live items jump the line; a live pill gives way to anything new.
            _queue.Clear();
            _queue.Enqueue(item);
            CloseThenNext();
        }
        else if (_queue.Count < MaxQueue)
        {
            _queue.Enqueue(item);
        }
    }

    private void Open(HudItem item)
    {
        var settings = _settings();
        _generation++;
        _current = item;
        _window.ApplyTheme(settings);
        _window.DataContext = item;

        if (!_visible)
        {
            _visible = true;
            _window.Show();
        }

        _window.Relayout();
        Position();
        _window.PlayIn();
        RestartHold();
    }

    private void OnHoldElapsed()
    {
        _hold.Stop();
        if (_queue.Count > 0) CloseThenNext();
        else Close();
    }

    private void CloseThenNext()
    {
        if (_closing) return;
        _hold.Stop();
        _closing = true;
        var generation = ++_generation;
        _window.PlayOut(() =>
        {
            if (generation != _generation) return;
            _closing = false;
            _current = null;
            if (_queue.Count > 0) Open(_queue.Dequeue());
            else HideWindow();
        });
    }

    private void Close()
    {
        _hold.Stop();
        _closing = true;
        var generation = ++_generation;
        _window.PlayOut(() =>
        {
            if (generation != _generation) return;
            _closing = false;
            _current = null;
            if (_queue.Count > 0) Open(_queue.Dequeue());
            else HideWindow();
        });
    }

    private void HideWindow()
    {
        _window.Hide();
        _visible = false;
    }

    private void RestartHold()
    {
        _hold.Stop();
        var openingMs = HudWindow.ExpandDelayMs + HudWindow.ExpandMs;
        _hold.Interval = TimeSpan.FromSeconds(_settings().HudSeconds) + TimeSpan.FromMilliseconds(openingMs);
        _hold.Start();
    }

    private void Position()
    {
        var settings = _settings();
        var hwnd = new WindowInteropHelper(_window).Handle;
        var work = NativeMethods.GetWorkArea(settings.HudScreen == HudScreen.MousePointer);
        const uint flags = NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;

        // Move onto the target screen first so WPF picks up that screen's scaling, then measure.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, work.Left, work.Top, 0, 0, flags);
        _window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(_window);
        var width = (int)Math.Ceiling(_window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(Math.Min(_window.ActualHeight, _window.MaxHeight) * dpi.DpiScaleY);
        var gap = (int)Math.Round(EdgeGap * dpi.DpiScaleY);

        var x = settings.HudPosition switch
        {
            HudPosition.TopLeft or HudPosition.BottomLeft => work.Left + gap,
            HudPosition.TopRight or HudPosition.BottomRight => work.Right - width - gap,
            _ => work.Left + (work.Width - width) / 2,
        };
        var y = settings.HudPosition switch
        {
            HudPosition.TopCenter or HudPosition.TopLeft or HudPosition.TopRight => work.Top + gap,
            _ => work.Bottom - height - gap,
        };

        // Never let the HUD leave the work area.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, x, y, 0, 0, flags);
    }
}
