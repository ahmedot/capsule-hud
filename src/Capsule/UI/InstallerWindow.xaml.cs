using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Capsule.Core;
using Capsule.Settings;

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
    private string? _keyboardAppPath;

    // First install only: a quick look at this PC (done in the background) decides which questions to ask.
    private readonly Task<PcProfile>? _scan;
    private List<SetupQuestion> _questions = [];
    private readonly List<SetupChoice> _answers = [];
    private TaskCompletionSource<List<SetupChoice>>? _asking;
    private DateTime _questionShownAt;

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
        if (!snapshot && state == Installer.State.NotInstalled && !SettingsStore.Exists) _scan = Task.Run(PcScan.Run);

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
        Closing += (_, e) =>
        {
            if (_asking is not null)
            {
                e.Cancel = true; // closing during the questions means "use the recommended answers"
                SkipQuestions();
                return;
            }

            e.Cancel = _busy; // never stop half-way through an install
        };
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

    /// <summary>Snapshot mode: show a setup question with sample devices.</summary>
    internal void ShowQuestionForSnapshot()
    {
        _questions = SetupQuestions.For(PcProfile.Sample);
        SetupPanel.Visibility = Visibility.Collapsed;
        QuestionsPanel.Visibility = Visibility.Visible;
        ShowQuestion(1, animate: false);
    }

    // ------------------------------------------------------------------ buttons

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetWorking(true);
        var startWithWindows = StartupSwitch.IsChecked == true;
        var started = DateTime.UtcNow;
        var install = Task.Run(() => Installer.Install(startWithWindows, _app.QuitRunningCopy, App.RunningCopyGone));
        // If copying fails while questions are on screen, stop asking and show the error.
        _ = install.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(SkipQuestions)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try
        {
            // First install: a few quick questions while the files are copied.
            List<SetupChoice>? answers = null;
            // The scan normally takes well under a second; never let a stuck one hold up the install.
            if (_scan is not null && await Task.WhenAny(_scan, Task.Delay(3000)) == _scan
                && SetupQuestions.For(await _scan) is { Count: > 0 } questions)
            {
                answers = await AskAsync(questions);
            }

            await install;

            // Let the progress sweep be seen; a too-fast install feels like nothing happened.
            var elapsed = DateTime.UtcNow - started;
            if (answers is null && elapsed < TimeSpan.FromMilliseconds(1400))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1400) - elapsed);
            }

            if (answers is not null) SaveAnswers(answers);

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
            QuestionsPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
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

    private void OnOpenKeyboardApp(object sender, RoutedEventArgs e)
    {
        // Checked again at click time: only an installed program under Program Files is ever started.
        if (_keyboardAppPath is null || !File.Exists(_keyboardAppPath) || !DuplicatePopupHider.IsInstalledLocation(_keyboardAppPath)) return;
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
        KeyboardAppText.Text = SettingsStore.Load().HideDuplicatePopups
            ? $"{app.Name} also shows Caps Lock pop-ups. Capsule hides them for you. " +
              "For the cleanest look, turn them off in its settings too."
            : $"{app.Name} also shows Caps Lock pop-ups. To see only Capsule's, turn them off in its settings.";
        _keyboardAppPath = app.SettingsPath;
        KeyboardAppButton.Content = $"Open {app.Name}";
        KeyboardAppButton.Visibility = app.SettingsPath is null ? Visibility.Collapsed : Visibility.Visible;
        KeyboardAppCard.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_asking is not null) SkipQuestions();
        else if (!_busy) Close();
    }

    // ------------------------------------------------------------------ setup questions

    private Task<List<SetupChoice>> AskAsync(List<SetupQuestion> questions)
    {
        _questions = questions;
        _answers.Clear();
        _asking = new TaskCompletionSource<List<SetupChoice>>();
        SetupPanel.Visibility = Visibility.Collapsed;
        QuestionsPanel.Visibility = Visibility.Visible;
        CloseButton.IsEnabled = true; // closing now just means "use the recommended answers"
        ShowQuestion(0, animate: true);
        return _asking.Task;
    }

    private void ShowQuestion(int index, bool animate)
    {
        _questionShownAt = DateTime.UtcNow;
        var question = _questions[index];
        StepText.Text = $"Make it yours  ·  {index + 1} of {_questions.Count}";
        QuestionTitle.Text = question.Title;
        QuestionHint.Text = question.Hint;
        ChoicesPanel.Children.Clear();
        foreach (var choice in question.Choices) ChoicesPanel.Children.Add(ChoiceButton(choice));

        StepDots.Children.Clear();
        for (var i = 0; i < _questions.Count; i++)
        {
            StepDots.Children.Add(new Border
            {
                Width = i == index ? 18 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
                Background = i <= index ? (Brush)FindResource("Mint") : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            });
        }

        if (!animate) return;
        QuestionBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)));
        QuestionShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, Ms(380))
        {
            EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut },
        });
    }

    private Button ChoiceButton(SetupChoice choice)
    {
        var icon = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new Ellipse { Fill = (Brush)FindResource("MintSoft") });
        icon.Children.Add(new TextBlock
        {
            Text = choice.Glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 15,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = choice.Title,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = choice.Description,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Subtext"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (choice.Recommended)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = (Brush)FindResource("MintSoft"),
                Padding = new Thickness(8, 2, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Recommended", FontSize = 11.5, Foreground = (Brush)FindResource("Mint") },
            };
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);
        }

        var button = new Button { Style = (Style)FindResource("ChoiceCard"), Content = row };
        System.Windows.Automation.AutomationProperties.SetName(button,
            $"{choice.Title}{(choice.Recommended ? " (recommended)" : "")}. {choice.Description}");
        button.Click += (_, _) => Pick(choice);
        return button;
    }

    private void Pick(SetupChoice choice)
    {
        // The next question's cards appear where the last ones were: ignore the second click of a double-click.
        if (_asking is null || DateTime.UtcNow - _questionShownAt < TimeSpan.FromMilliseconds(400)) return;
        _answers.Add(choice);
        if (_answers.Count < _questions.Count) ShowQuestion(_answers.Count, animate: true);
        else FinishQuestions();
    }

    private void OnSkipQuestions(object sender, RoutedEventArgs e) => SkipQuestions();

    // Anything not answered yet gets its recommended answer.
    private void SkipQuestions()
    {
        if (_asking is null) return;
        for (var i = _answers.Count; i < _questions.Count; i++) _answers.Add(_questions[i].Recommended);
        FinishQuestions();
    }

    private void FinishQuestions()
    {
        var asking = _asking;
        _asking = null;
        QuestionsPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible; // shows "Installing…" if the copy is still running
        CloseButton.IsEnabled = false;
        asking?.TrySetResult([.. _answers]);
    }

    private static void SaveAnswers(List<SetupChoice> answers)
    {
        try
        {
            var settings = SettingsStore.Load();
            foreach (var answer in answers) answer.Apply(settings);
            SettingsStore.Save(settings.Normalize());
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the setup answers; using the defaults", ex);
        }
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
        QuestionsPanel.Visibility = Visibility.Collapsed;
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

        // Open width: left margin, icon and gap (as in the XAML), plus the text.
        DemoDetails.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var open = Math.Ceiling(7 + 32 + 11 + DemoDetails.DesiredSize.Width);

        var popIn = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        var openEase = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var closeEase = new SineEase { EasingMode = EasingMode.EaseInOut };

        DemoHost.BeginAnimation(OpacityProperty, Frames((0, 0), (280, 1), (3900, 1), (4250, 0)));
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
