using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Capsule.Core;

namespace Capsule.UI;

/// <summary>
/// First thing people see after downloading Capsule: one button installs it for this account and starts it.
/// </summary>
public partial class InstallerWindow : Window
{
    private const double ClosedWidth = 48;

    private static readonly DemoItem[] Demo =
    [
        new(Glyphs.Headphones, "Studio Headphones", "Connected", null, "80%"),
        new(Glyphs.Volume2, "Volume", null, 0.64, null),
        new(Glyphs.Controller, "Wireless Controller", "Connected", null, "55%"),
        new(Glyphs.Play, "Midnight Drive", "Neon Avenue", null, null),
        new(Glyphs.Keyboard, "Caps Lock", "On", null, null),
    ];

    private readonly App _app;
    private readonly string[] _args;
    private readonly DispatcherTimer _demoTimer;
    private int _demoIndex;
    private bool _continuePortable;
    private bool _busy;

    internal InstallerWindow(App app, string[] args, bool snapshot = false)
    {
        _app = app;
        _args = args;
        SnapshotMode = snapshot;
        InitializeComponent();
        UseWindowsAccent();

        var state = snapshot ? Installer.State.NotInstalled : Installer.Compare();
        VersionText.Text = $"Version {App.Version}";
        InstallText.Text = state switch
        {
            Installer.State.Older => "Update",
            Installer.State.Same => "Reinstall",
            _ => "Install",
        };
        if (state != Installer.State.NotInstalled) StartupSwitch.IsChecked = AutoStart.IsEnabledFor(Installer.InstalledExe);

        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4700) };
        _demoTimer.Tick += (_, _) => PlayDemo();
        Loaded += (_, _) =>
        {
            DriftGlows();
            PlayDemo();
            _demoTimer.Start();
        };
        // Round the hero's top corners to match the window.
        Frame.SizeChanged += (_, _) => Frame.Clip = new RectangleGeometry(new Rect(Frame.RenderSize), 18, 18);
        Closing += (_, e) => e.Cancel = _busy; // never stop half-way through an install
        Closed += (_, _) =>
        {
            _demoTimer.Stop();
            if (SnapshotMode) return;
            if (_continuePortable) _app.StartCapsule(_args);
            else _app.Shutdown();
        };
    }

    /// <summary>Set by the screenshot tool: closing the window then does nothing else.</summary>
    private bool SnapshotMode { get; }

    /// <summary>Snapshot mode: jump straight to the finished state.</summary>
    internal void ShowDoneForSnapshot() => ShowDone(animate: false);

    // ------------------------------------------------------------------ buttons

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetWorking(true);
        var startWithWindows = StartupSwitch.IsChecked == true;
        var started = DateTime.UtcNow;

        try
        {
            await Task.Run(() => Installer.Install(startWithWindows, _app.QuitRunningCopy, App.RunningCopyGone));

            // Let the progress sweep be seen; a too-fast install feels like nothing happened.
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromMilliseconds(1400)) await Task.Delay(TimeSpan.FromMilliseconds(1400) - elapsed);

            try
            {
                Installer.LaunchInstalled(Installer.InstalledArgument);
            }
            catch (Exception ex)
            {
                Log.Error("Installed, but could not start Capsule", ex);
                DoneText.Text = "Capsule is installed. Open it from the Start menu to start it.";
            }

            ShowDone(animate: true);
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            SetWorking(false);
            InstallText.Text = "Try again";
            ErrorText.Text = "Capsule couldn't be installed. Close other copies of Capsule and try again, " +
                             "or choose \"Try it without installing\".";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRunPortable(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _continuePortable = true;
        Close();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private string? _keyboardAppPath;

    private void OnOpenKeyboardApp(object sender, RoutedEventArgs e)
    {
        if (_keyboardAppPath is null || !File.Exists(_keyboardAppPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_keyboardAppPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the keyboard app", ex);
        }
    }

    // A keyboard app with its own Caps Lock pop-up: Capsule hides the duplicate, and says how to switch it off for good.
    private void ShowKeyboardAppTip()
    {
        if (SnapshotMode || DuplicatePopupHider.DetectApp() is not { } app) return;
        KeyboardAppText.Text = $"{app.Name} also shows Caps Lock pop-ups. Capsule hides them for you. " +
                               "For the cleanest look, turn them off in its settings too.";
        _keyboardAppPath = app.SettingsPath;
        KeyboardAppButton.Content = $"Open {app.Name}";
        KeyboardAppButton.Visibility = app.SettingsPath is null ? Visibility.Collapsed : Visibility.Visible;
        KeyboardAppCard.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ------------------------------------------------------------------ states

    private void SetWorking(bool working)
    {
        InstallButton.IsEnabled = !working;
        StartupSwitch.IsEnabled = !working;
        PortableButton.IsEnabled = !working;
        CloseButton.IsEnabled = !working;
        ErrorText.Visibility = Visibility.Collapsed;
        InstallText.Text = working ? "Installing…" : InstallText.Text;

        if (working)
        {
            Sweep.Opacity = 1;
            SweepMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.3, 1.1, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            Sweep.Opacity = 0;
            SweepMove.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    private void ShowDone(bool animate)
    {
        SetWorking(false);
        CloseButton.IsEnabled = true;
        SetupPanel.Visibility = Visibility.Collapsed;
        ShowKeyboardAppTip();
        DonePanel.Visibility = Visibility.Visible;
        DoneButton.IsDefault = true;

        if (!animate)
        {
            DonePanel.Opacity = 1;
            DoneShift.Y = 0;
            CheckScale.ScaleX = CheckScale.ScaleY = 1;
            CheckMark.StrokeDashOffset = 0;
            return;
        }

        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        DonePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(420)));
        DoneShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, Ms(620)) { EasingFunction = ease });
        var pop = new DoubleAnimation(0.6, 1, Ms(560)) { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } };
        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        CheckMark.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(12, 0, Ms(520))
        {
            BeginTime = Ms(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ------------------------------------------------------------------ colours

    /// <summary>Paints the button, switch, icons and background glow in the person's Windows accent colour.</summary>
    private void UseWindowsAccent()
    {
        var light = WindowsAccent.ForDark;   // Windows' own shade for accents on dark backgrounds
        var lighter = WindowsAccent.Light3;
        var mid = WindowsAccent.Light1;
        var deep = WindowsAccent.Dark2;

        Resources["Mint"] = Frozen(new SolidColorBrush(light));
        Resources["MintSoft"] = Frozen(new SolidColorBrush(Color.FromArgb(0x30, light.R, light.G, light.B)));
        Resources["BrandGradient"] = Frozen(new LinearGradientBrush(lighter, mid, new Point(0, 0), new Point(1, 1)));
        CheckGlow.Color = light;

        GlowA.Fill = Glow(mid, 0x80);
        GlowB.Fill = Glow(deep, 0x90);
        GlowC.Fill = Glow(lighter, 0x50);
    }

    private static RadialGradientBrush Glow(Color c, byte alpha) =>
        Frozen(new RadialGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)));

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ hero animation

    private void DriftGlows()
    {
        Drift(GlowMint, 26, 18, 9);
        Drift(GlowViolet, -30, -14, 11);
        Drift(GlowTeal, -22, 16, 13);
    }

    private static void Drift(TranslateTransform target, double x, double y, double seconds)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        target.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, x, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        });
        target.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, y, TimeSpan.FromSeconds(seconds * 0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        });
    }

    /// <summary>The same rhythm as the real HUD: icon pops in, the pill opens from the middle, then closes again.</summary>
    private void PlayDemo()
    {
        var item = Demo[_demoIndex++ % Demo.Length];
        DemoGlyph.Text = item.Glyph;
        DemoTitle.Text = item.Title;
        DemoStatus.Text = item.Status ?? "";
        DemoStatusPanel.Visibility = item.Status is null ? Visibility.Collapsed : Visibility.Visible;
        DemoLevelPanel.Visibility = item.Level is null ? Visibility.Collapsed : Visibility.Visible;
        DemoLevel.ScaleX = item.Level ?? 0;
        DemoChip.Visibility = item.Chip is null ? Visibility.Collapsed : Visibility.Visible;
        DemoChipText.Text = item.Chip ?? "";

        DemoDetails.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var open = Math.Ceiling(7 + 32 + 11 + DemoDetails.DesiredSize.Width);

        var popIn = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        var openEase = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var closeEase = new SineEase { EasingMode = EasingMode.EaseInOut };

        DemoHost.BeginAnimation(OpacityProperty, Frames(
            (0, 0), (280, 1), (3900, 1), (4250, 0)));
        DemoIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pop(popIn));
        DemoIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pop(popIn));

        var width = new DoubleAnimationUsingKeyFrames();
        width.KeyFrames.Add(new DiscreteDoubleKeyFrame(ClosedWidth, KeyTime.FromTimeSpan(Ms(0))));
        width.KeyFrames.Add(new DiscreteDoubleKeyFrame(ClosedWidth, KeyTime.FromTimeSpan(Ms(380))));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(open, KeyTime.FromTimeSpan(Ms(1140)), openEase));
        width.KeyFrames.Add(new DiscreteDoubleKeyFrame(open, KeyTime.FromTimeSpan(Ms(3240))));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(ClosedWidth, KeyTime.FromTimeSpan(Ms(3800)), closeEase));
        DemoPill.BeginAnimation(WidthProperty, width);

        DemoDetails.BeginAnimation(OpacityProperty, Frames((0, 0), (640, 0), (1080, 1), (3080, 1), (3360, 0)));
        DemoDetailsShift.BeginAnimation(TranslateTransform.XProperty, Frames((0, 10), (640, 10), (1140, 0)));
    }

    private static DoubleAnimationUsingKeyFrames Pop(IEasingFunction ease)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(Ms(0))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(460)), ease));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(3800))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(Ms(4250))));
        return a;
    }

    private static DoubleAnimationUsingKeyFrames Frames(params (int Ms, double Value)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, value) in frames)
        {
            a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(Ms(ms)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }

        return a;
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private sealed record DemoItem(string Glyph, string Title, string? Status, double? Level, string? Chip);
}
