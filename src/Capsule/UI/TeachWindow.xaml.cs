using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Capsule.Core;
using Capsule.Settings;

namespace Capsule.UI;

/// <summary>
/// "Teach Capsule": the user presses the key that makes another app show its own pop-up, Capsule names that app, and
/// on "Yes" hides its small pop-ups from then on. Watches only while this window is open.
/// </summary>
public partial class TeachWindow : Window
{
    private const long WatchForMs = 15_000;

    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private HashSet<IntPtr> _baseline = [];
    private long _until;
    private Action? _primary;
    private Action? _secondary;

    internal TeachWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Look();
        Loaded += (_, _) => Watch();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Screenshot tool: show the "found it" step with a sample app.</summary>
    internal void ShowFoundForSnapshot()
    {
        _timer.Stop(); // so a real pop-up can't replace the sample
        ShowFound([new PopupCandidate(IntPtr.Zero, "Keyboard Utility", @"C:\Program Files\Vendor\KeyboardUtility.exe", "SampleOsd", 280, 72)]);
    }

    // ------------------------------------------------------------------ steps

    private void Watch()
    {
        Heading.Text = "Press Caps Lock now";
        Body.Text = "Or Num Lock, or change the volume: whichever makes the extra pop-up appear. " +
                    "Capsule is watching for the next 15 seconds.";
        Waiting.Visibility = Visibility.Visible;
        Found.Children.Clear();
        SetButtons(primary: null, secondary: ("Cancel", Close));
        _baseline = DuplicatePopupHider.VisibleWindows();
        _until = Environment.TickCount64 + WatchForMs;
        _timer.Start();
    }

    private void Look()
    {
        var found = DuplicatePopupHider.FindNewPopups(_baseline, _app.Settings.TaughtPopups);
        if (found.Count > 0)
        {
            _timer.Stop();
            ShowFound(found);
        }
        else if (Environment.TickCount64 >= _until)
        {
            _timer.Stop();
            ShowNothing();
        }
    }

    private void ShowFound(IReadOnlyList<PopupCandidate> found)
    {
        Heading.Text = found.Count == 1 ? $"Found it: {found[0].App}" : "Found these pop-ups";
        Body.Text = found.Count == 1
            ? $"Hide {found[0].App}'s pop-up from now on? Capsule hides it right after showing its own, and the app's main window isn't affected."
            : "Which app's pop-up should Capsule hide from now on?";
        Waiting.Visibility = Visibility.Collapsed;
        Found.Children.Clear();

        if (found.Count == 1)
        {
            Found.Children.Add(Detail(found[0]));
            SetButtons(primary: ("Yes, hide it", () => Teach(found[0])), secondary: ("Not this one", Watch));
            return;
        }

        foreach (var candidate in found)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Detail(candidate));
            var hide = new Button { Content = "Hide it", Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            hide.Click += (_, _) => Teach(candidate);
            Grid.SetColumn(hide, 1);
            row.Children.Add(hide);
            Found.Children.Add(row);
        }

        SetButtons(primary: null, secondary: ("Try again", Watch));
    }

    private void ShowNothing()
    {
        Heading.Text = "No other pop-up seen";
        Body.Text = "Capsule didn't see another app's pop-up this time. Try again, and press the key that shows it.";
        Waiting.Visibility = Visibility.Collapsed;
        Found.Children.Clear();
        SetButtons(primary: ("Try again", Watch), secondary: ("Close", Close));
    }

    private void Teach(PopupCandidate candidate)
    {
        var wasOn = _app.Settings.HideDuplicatePopups;
        _app.UpdateSettings(s =>
        {
            s.HideDuplicatePopups = true;
            s.TaughtPopups.RemoveAll(t => t.Matches(candidate.ExePath, candidate.WindowClass));
            s.TaughtPopups.Add(new TaughtPopup
            {
                App = candidate.App,
                ExePath = candidate.ExePath,
                WindowClass = candidate.WindowClass,
                Width = candidate.Width,
                Height = candidate.Height,
            });
        });
        Found.Children.Clear();
        if (!_app.Settings.TaughtPopups.Any(t => t.Matches(candidate.ExePath, candidate.WindowClass)))
        {
            Heading.Text = "Couldn't remember this app";
            Body.Text = "Capsule remembers up to 16 apps. Remove one under HUD look in Capsule's settings, then try again.";
            SetButtons(primary: ("Close", Close), secondary: null);
            return;
        }

        DuplicatePopupHider.HideOne(candidate);
        HeadingIcon.Text = Glyphs.Checkmark;
        Heading.Text = "Done";
        Body.Text = $"Capsule will hide {candidate.App}'s pop-up right after showing its own. " +
                    (wasOn ? "" : "This also turned on \"Hide duplicate pop-ups from keyboard apps\". ") +
                    "To undo it, click Remove under HUD look in Capsule's settings.";
        Found.Children.Clear();
        SetButtons(primary: ("Close", Close), secondary: null);
    }

    private static StackPanel Detail(PopupCandidate candidate)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = candidate.App, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = DisplayText.Clean(Path.GetFileName(candidate.ExePath), 60),
            FontSize = 12.5,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    // ------------------------------------------------------------------ buttons

    private void SetButtons((string Text, Action Run)? primary, (string Text, Action Run)? secondary)
    {
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary?.Text;
        _primary = primary?.Run;
        PrimaryButton.IsDefault = primary is not null;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary?.Text;
        _secondary = secondary?.Run;
        SecondaryButton.IsCancel = secondary is not null;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _primary?.Invoke();

    private void OnSecondary(object sender, RoutedEventArgs e) => _secondary?.Invoke();
}
