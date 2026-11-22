using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Capsule.Core;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Hud;

/// <summary>
/// The HUD pill. It opens in three beats: the round icon pops in, the capsule grows out from the middle
/// while the icon glides to its place, and the text fades in. Closing plays the same in reverse.
/// </summary>
public partial class HudWindow : Window
{
    // Kept slow on purpose, with soft easing: the icon settles before the capsule opens.
    public const double IconFadeMs = 240;
    public const double IconPopMs = 460;
    public const double ExpandDelayMs = 330;
    public const double ExpandMs = 720;
    public const double CollapseMs = 540;
    public const double VanishDelayMs = 450;
    public const double VanishMs = 300;
    private const double DetailsSlide = 10;
    private const double MinPillSize = 46;
    private const double IconCenter = 8 + 16; // content margin + half the icon bubble

    /// <summary>0 = closed to the round icon, 1 = fully open pill.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(HudWindow),
        new PropertyMetadata(1.0, (d, _) => ((HudWindow)d).ApplyProgress()));

    private readonly RectangleGeometry _clip = new();
    private HudItem? _item;
    private double _fullWidth = MinPillSize;
    private double _height = MinPillSize;
    private double _iconShift;

    public HudWindow()
    {
        InitializeComponent();
        ContentHost.Clip = _clip;
        SourceInitialized += (_, _) => NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
        DataContextChanged += OnDataContextChanged;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>WPF render tier 2 means full hardware (GPU) rendering is available.</summary>
    public static bool HasGpu => (RenderCapability.Tier >> 16) >= 2;

    // ------------------------------------------------------------------ animation

    /// <summary>Icon pops in, then the capsule opens from the middle.</summary>
    public void PlayIn(Action? done = null)
    {
        StopAnimations();
        Relayout();
        Stage.CacheMode = null;
        Progress = 0;
        Stage.Opacity = 0;
        Pop.ScaleX = Pop.ScaleY = 0.55;

        Stage.BeginAnimation(OpacityProperty, Make(0, 1, IconFadeMs, 0, EaseOut()));
        var pop = Make(0.55, 1, IconPopMs, 0, PopEase());
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        var expand = Make(0, 1, ExpandMs, ExpandDelayMs, OpenEase());
        expand.Completed += (_, _) =>
        {
            // Hold still as one GPU-cached bitmap, so fading or moving it costs almost no CPU.
            if (HasGpu) Stage.CacheMode = new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX * SizeScale.ScaleX };
            done?.Invoke();
        };
        BeginAnimation(ProgressProperty, expand);
    }

    /// <summary>Text fades, the capsule closes back to the icon, then the icon shrinks away.</summary>
    public void PlayOut(Action? done = null)
    {
        var from = Progress;
        StopAnimations();
        Stage.CacheMode = null;
        BeginAnimation(ProgressProperty, Make(from, 0, CollapseMs, 0, CloseEase()));

        var vanish = Make(1, 0, VanishMs, VanishDelayMs, EaseIn());
        vanish.Completed += (_, _) => done?.Invoke();
        Stage.BeginAnimation(OpacityProperty, vanish);
        var shrink = Make(1, 0.6, VanishMs, VanishDelayMs, EaseIn());
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Sets one exact frame (used to render the animation preview).</summary>
    public void SetFrame(double opacity, double pop, double progress)
    {
        StopAnimations();
        Relayout();
        Stage.Opacity = opacity;
        Pop.ScaleX = Pop.ScaleY = pop;
        Progress = progress;
    }

    /// <summary>The state of the opening and closing animations at a moment, for rendering previews.</summary>
    public static (double Opacity, double Pop, double Progress) FrameAt(double ms, double holdMs)
    {
        var openEnd = ExpandDelayMs + ExpandMs;
        if (ms <= openEnd + holdMs)
        {
            var opacity = EaseOut().Ease(Math.Clamp(ms / IconFadeMs, 0, 1));
            var pop = 0.55 + 0.45 * PopEase().Ease(Math.Clamp(ms / IconPopMs, 0, 1));
            var progress = OpenEase().Ease(Math.Clamp((ms - ExpandDelayMs) / ExpandMs, 0, 1));
            return (opacity, pop, progress);
        }

        var t = ms - openEnd - holdMs;
        var close = 1 - CloseEase().Ease(Math.Clamp(t / CollapseMs, 0, 1));
        var vanish = EaseIn().Ease(Math.Clamp((t - VanishDelayMs) / VanishMs, 0, 1));
        return (1 - vanish, 1 - 0.4 * vanish, close);
    }

    public static double TotalMs(double holdMs) => ExpandDelayMs + ExpandMs + holdMs + VanishDelayMs + VanishMs;

    private static IEasingFunction EaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };
    private static IEasingFunction EaseIn() => new CubicEase { EasingMode = EasingMode.EaseIn };
    private static IEasingFunction PopEase() => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
    private static IEasingFunction OpenEase() => new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
    private static IEasingFunction CloseEase() => new SineEase { EasingMode = EasingMode.EaseInOut };

    private static DoubleAnimation Make(double from, double to, double ms, double delayMs, IEasingFunction ease) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

    private void StopAnimations()
    {
        var progress = Progress;
        var opacity = Stage.Opacity;
        var pop = Pop.ScaleX;
        BeginAnimation(ProgressProperty, null);
        Stage.BeginAnimation(OpacityProperty, null);
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Progress = progress;
        Stage.Opacity = opacity;
        Pop.ScaleX = Pop.ScaleY = pop;
    }

    /// <summary>Measures the fully open pill (its size changes with the text) and re-applies the current frame.</summary>
    public void Relayout()
    {
        // Let new text and battery bindings settle first, so the measured width includes them.
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        PillContent.InvalidateMeasure();
        PillContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _fullWidth = Math.Ceiling(Math.Max(MinPillSize, PillContent.DesiredSize.Width));
        _height = Math.Ceiling(Math.Max(MinPillSize, PillContent.DesiredSize.Height));
        Stage.Width = ContentHost.Width = _fullWidth;
        Stage.Height = ContentHost.Height = _height;
        _iconShift = _fullWidth / 2 - IconCenter;
        ApplyProgress();
    }

    private void ApplyProgress()
    {
        var e = Math.Clamp(Progress, 0, 1);
        var width = _height + (_fullWidth - _height) * e;
        var shift = _iconShift * (1 - e);

        Pill.Width = width;
        Pill.CornerRadius = new CornerRadius(_height / 2);
        ContentShift.X = shift;

        // The clip stays centred on the stage while the content slides underneath it.
        _clip.Rect = new Rect((_fullWidth - width) / 2 - shift, 0, width, _height);
        _clip.RadiusX = _clip.RadiusY = _height / 2;
        // Text comes in last, sliding a few pixels as it fades in.
        var text = Math.Clamp((e - 0.4) / 0.55, 0, 1);
        Details.Opacity = text;
        DetailsShift.X = DetailsSlide * (1 - text);
    }

    // ------------------------------------------------------------------ look

    /// <summary>Applies colors, accent and size from settings.</summary>
    public void ApplyTheme(AppSettings settings)
    {
        var light = settings.HudTheme == HudTheme.Light
            || (settings.HudTheme == HudTheme.System && IsWindowsLight());
        var followWindows = AppSettings.IsWindowsAccent(settings.HudAccent);
        var accent = followWindows
            ? (light ? WindowsAccent.ForLight : WindowsAccent.ForDark)
            : HudItems.ParseColor(settings.HudAccent, Color.FromRgb(0x34, 0xD1, 0x7A));

        if (light)
        {
            SetBrush("HudBackground", Color.FromArgb(0xF2, 0xFB, 0xFB, 0xFC));
            SetBorder(Color.FromArgb(0x1F, 0, 0, 0), Color.FromArgb(0x0F, 0, 0, 0));
            SetBrush("HudText", Color.FromRgb(0x17, 0x18, 0x1C));
            SetBrush("HudSubtext", Color.FromArgb(0xA6, 0x17, 0x18, 0x1C));
            SetBrush("HudTrack", Color.FromArgb(0x1C, 0, 0, 0));
            SetBrush("HudChip", Color.FromArgb(0x0F, 0, 0, 0));
            if (!followWindows) accent = Darken(accent, 0.18); // the Windows shade for light backgrounds is already darker
        }
        else
        {
            SetBrush("HudBackground", Color.FromArgb(0xF0, 0x14, 0x15, 0x19));
            SetBorder(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            SetBrush("HudText", Colors.White);
            SetBrush("HudSubtext", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            SetBrush("HudTrack", Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
            SetBrush("HudChip", Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
        }

        SetBrush("HudAccent", accent);
        SetBrush("HudAccentSoft", Color.FromArgb(0x59, accent.R, accent.G, accent.B));

        var scale = settings.HudSize switch
        {
            HudSize.Compact => 0.86,
            HudSize.Large => 1.22,
            _ => 1.0,
        };
        SizeScale.ScaleX = scale;
        SizeScale.ScaleY = scale;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = e.NewValue as HudItem;
        if (_item is not null) _item.PropertyChanged += OnItemChanged;
        UpdateTone();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudItem.Tone)) UpdateTone();

        // Text or battery changed while showing: re-measure after the bindings update.
        Dispatcher.BeginInvoke(() =>
        {
            Stage.CacheMode = null;
            Relayout();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateTone()
    {
        var (dot, icon, bubble) = _item?.Tone switch
        {
            HudTone.Bad => ("HudBad", "HudText", "HudBadSoft"),
            HudTone.Neutral => ("HudSubtext", "HudText", "HudChip"),
            _ => ("HudAccent", "HudText", "HudAccentSoft"),
        };
        StatusDot.SetResourceReference(Shape.FillProperty, dot);
        IconText.SetResourceReference(TextBlock.ForegroundProperty, icon);
        IconBubble.SetResourceReference(Shape.FillProperty, bubble);
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    private void SetBorder(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        Resources["HudBorder"] = brush;
    }

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    private static bool IsWindowsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }
}
