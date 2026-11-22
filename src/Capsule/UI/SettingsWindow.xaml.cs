using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capsule.Core;
using Capsule.Hud;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.UI;

public partial class SettingsWindow : Window
{
    private const string MicKeepCurrent = "\u0001keep";
    private const string MicLetWindows = "\u0001windows";

    private readonly App _app;
    private bool _loading;
    private string? _renameSignature;
    private HudWindow? _previewWindow;

    internal SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Load();
        Wire();
    }

    /// <summary>Re-reads devices and status (called when devices change while the window is open).</summary>
    public void RefreshDevices()
    {
        _loading = true;
        try
        {
            FillDeviceLists(_app.Settings);
            UpdateHome();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _app.Settings;
            AutoSwitchToggle.IsChecked = s.AutoSwitch;
            Select(HeadphoneModeCombo, s.HeadphoneMode.ToString());
            CallsToggle.IsChecked = s.SwitchCommunications;

            HudToggle.IsChecked = s.HudEnabled;
            HudHeadphonesCheck.IsChecked = s.HudHeadphones;
            HudControllersCheck.IsChecked = s.HudControllers;
            HudVolumeCheck.IsChecked = s.HudVolume;
            HudMicCheck.IsChecked = s.HudMicMute;
            HudMediaCheck.IsChecked = s.HudMedia;
            HudBrightnessCheck.IsChecked = s.HudBrightness;
            HudLockKeysCheck.IsChecked = s.HudLockKeys;
            HudKeyboardCheck.IsChecked = s.HudKeyboard;
            HudPrivacyCheck.IsChecked = s.HudPrivacy;
            HudUsbCheck.IsChecked = s.HudUsb;
            HudNetworkCheck.IsChecked = s.HudNetwork;
            HudCpuGpuCheck.IsChecked = s.HudCpuGpu;
            HudHeatCheck.IsChecked = s.HudHeat;
            HudMemoryDiskCheck.IsChecked = s.HudMemoryDisk;
            HudPowerCheck.IsChecked = s.HudPower;
            AutoUpdateToggle.IsChecked = s.AutoUpdateCheck;
            Select(AppThemeCombo, s.AppTheme.ToString());
            UpdateUpdateRow();
            HideOsdToggle.IsChecked = s.HideWindowsOsd;
            BatteryToggle.IsChecked = s.HudBattery;
            foreach (var spot in PositionSpots()) spot.IsChecked = (string)spot.Tag == s.HudPosition.ToString();
            Select(ScreenCombo, s.HudScreen.ToString());
            Select(SizeCombo, s.HudSize.ToString());
            Select(ThemeCombo, s.HudTheme.ToString());
            DurationSlider.Value = s.HudSeconds;
            DurationText.Text = $"{s.HudSeconds:0.0} s";
            BuildAccentSwatches(s.HudAccent);

            IgnoreVirtualToggle.IsChecked = s.IgnoreVirtualControllers;
            StartupToggle.IsChecked = AutoStart.IsEnabled();

            LogRow.Description = _app.DemoMode ? @"%LOCALAPPDATA%\Capsule" : Log.Folder;
            AboutRow.Description = $"Capsule {App.Version}. Open source (MIT). No telemetry, no admin rights. The only thing it ever downloads is its own updates from GitHub.";
            RepoButton.Visibility = App.RepositoryUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            FillDeviceLists(s);
            UpdateHome();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Wire()
    {
        Toggle(AutoSwitchToggle, (s, on) => s.AutoSwitch = on);
        Toggle(CallsToggle, (s, on) => s.SwitchCommunications = on);
        Toggle(HudToggle, (s, on) => s.HudEnabled = on);
        Toggle(HudHeadphonesCheck, (s, on) => s.HudHeadphones = on);
        Toggle(HudControllersCheck, (s, on) => s.HudControllers = on);
        Toggle(HudVolumeCheck, (s, on) => s.HudVolume = on);
        Toggle(HudMicCheck, (s, on) => s.HudMicMute = on);
        Toggle(HudMediaCheck, (s, on) => s.HudMedia = on);
        Toggle(HudBrightnessCheck, (s, on) => s.HudBrightness = on);
        Toggle(HudLockKeysCheck, (s, on) => s.HudLockKeys = on);
        Toggle(HideOsdToggle, (s, on) => s.HideWindowsOsd = on);
        Toggle(HudKeyboardCheck, (s, on) => s.HudKeyboard = on);
        Toggle(HudPrivacyCheck, (s, on) => s.HudPrivacy = on);
        Toggle(HudUsbCheck, (s, on) => s.HudUsb = on);
        Toggle(HudNetworkCheck, (s, on) => s.HudNetwork = on);
        Toggle(HudCpuGpuCheck, (s, on) => s.HudCpuGpu = on);
        Toggle(HudHeatCheck, (s, on) => s.HudHeat = on);
        Toggle(HudMemoryDiskCheck, (s, on) => s.HudMemoryDisk = on);
        Toggle(HudPowerCheck, (s, on) => s.HudPower = on);
        Toggle(AutoUpdateToggle, (s, on) => s.AutoUpdateCheck = on);
        Choice<AppTheme>(AppThemeCombo, (s, v) => s.AppTheme = v);

        _app.UpdateStateChanged += OnUpdateStateChanged;
        Closed += (_, _) => _app.UpdateStateChanged -= OnUpdateStateChanged;
        Toggle(BatteryToggle, (s, on) => s.HudBattery = on);
        Toggle(IgnoreVirtualToggle, (s, on) => s.IgnoreVirtualControllers = on);

        StartupToggle.Checked += (_, _) => { if (!_loading) AutoStart.Set(true); };
        StartupToggle.Unchecked += (_, _) => { if (!_loading) AutoStart.Set(false); };

        Choice<HeadphoneMode>(HeadphoneModeCombo, (s, v) => s.HeadphoneMode = v, after: UpdatePickerVisibility);
        foreach (var spot in PositionSpots())
        {
            spot.Checked += (_, _) =>
            {
                if (_loading || !Enum.TryParse<HudPosition>((string)spot.Tag, out var position)) return;
                _app.UpdateSettings(s => s.HudPosition = position);
                UpdateLook();
            };
        }

        Choice<HudScreen>(ScreenCombo, (s, v) => s.HudScreen = v, after: UpdateLook);
        Choice<HudSize>(SizeCombo, (s, v) => s.HudSize = v, after: UpdateLook);
        Choice<HudTheme>(ThemeCombo, (s, v) => s.HudTheme = v, after: UpdateLook);

        foreach (var item in Nav.Children.OfType<RadioButton>()) item.Checked += (_, _) => ShowPage(item);
        foreach (var toggle in new ToggleButton[] { AutoSwitchToggle, CallsToggle, HudToggle, BatteryToggle, AutoUpdateToggle, StartupToggle })
        {
            toggle.Checked += (_, _) => Dispatcher.BeginInvoke(UpdateLook);
            toggle.Unchecked += (_, _) => Dispatcher.BeginInvoke(UpdateLook);
        }

        Closed += (_, _) => _previewWindow?.Close();
        Loaded += (_, _) => UpdateLook();

        FallbackCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || FallbackCombo.SelectedItem is not ComboBoxItem item) return;
            _app.UpdateSettings(s => s.FallbackDevice = (string)item.Tag);
        };

        MicCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || MicCombo.SelectedItem is not ComboBoxItem item) return;
            var tag = (string)item.Tag;
            _app.UpdateSettings(s =>
            {
                s.MicMode = tag switch
                {
                    MicKeepCurrent => MicMode.KeepCurrent,
                    MicLetWindows => MicMode.LetWindowsDecide,
                    _ => MicMode.Specific,
                };
                s.MicDevice = s.MicMode == MicMode.Specific ? tag : "";
            });
        };

        DurationSlider.ValueChanged += (_, e) =>
        {
            DurationText.Text = $"{e.NewValue:0.0} s";
            if (!_loading) _app.UpdateSettings(s => s.HudSeconds = e.NewValue);
        };

        UpdatePickerVisibility();
    }

    private void FillDeviceLists(AppSettings s)
    {
        var outputs = _app.OutputNames()
            .Concat(s.SelectedHeadphones)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Headphone picker
        HeadphoneList.Items.Clear();
        foreach (var name in outputs)
        {
            var box = new CheckBox
            {
                Content = DisplayText.ForAccessText(name),
                IsChecked = s.SelectedHeadphones.Contains(name, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Checked += (_, _) => UpdateSelected(name, true);
            box.Unchecked += (_, _) => UpdateSelected(name, false);
            HeadphoneList.Items.Add(box);
        }

        HeadphoneListEmpty.Visibility = outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FillRenameList();

        // Fallback output
        FallbackCombo.Items.Clear();
        FallbackCombo.Items.Add(new ComboBoxItem { Content = "The device I was using before", Tag = "" });
        foreach (var name in outputs.Append(s.FallbackDevice).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FallbackCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }

        Select(FallbackCombo, s.FallbackDevice);

        // Microphone
        var mics = _app.MicNames()
            .Append(s.MicDevice)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase);

        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "Keep my current mic (recommended)", Tag = MicKeepCurrent });
        MicCombo.Items.Add(new ComboBoxItem { Content = "Let Windows decide", Tag = MicLetWindows });
        foreach (var name in mics)
        {
            MicCombo.Items.Add(new ComboBoxItem { Content = $"Always use {name}", Tag = name });
        }

        Select(MicCombo, s.MicMode switch
        {
            MicMode.LetWindowsDecide => MicLetWindows,
            MicMode.Specific => s.MicDevice,
            _ => MicKeepCurrent,
        });
    }

    private void FillRenameList()
    {
        var devices = _app.RenameableDevices();
        var signature = string.Join("\u0001", devices.Select(d => d.Name));

        // Don't rebuild under the user's fingers, or when nothing changed.
        if (signature == _renameSignature || RenameList.IsKeyboardFocusWithin) return;
        _renameSignature = signature;

        RenameList.Children.Clear();
        RenameEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, glyph) in devices) RenameList.Children.Add(RenameRow(name, glyph));
    }

    private FrameworkElement RenameRow(string original, string glyph)
    {
        var grid = new Grid { Margin = new Thickness(16, 4, 16, 4), MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var label = new TextBlock
        {
            Text = original,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 16, 0),
            ToolTip = original,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetColumn(label, 1);

        var current = _app.Settings.DeviceNames.TryGetValue(original, out var custom) ? custom : "";
        var box = new TextBox
        {
            Text = current,
            Width = 240,
            MaxLength = AppSettings.MaxCustomNameLength,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"Name for {original}");

        // Grey hint showing the Windows name while the box is empty.
        var hint = new TextBlock
        {
            Text = original,
            IsHitTestVisible = false,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = current.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var field = new Grid { Width = 240 };
        field.Children.Add(box);
        field.Children.Add(hint);
        Grid.SetColumn(field, 2);

        var reset = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13 },
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Use the Windows name",
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(reset, $"Reset the name for {original}");
        Grid.SetColumn(reset, 3);

        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_loading) _app.UpdateSettings(s => s.Rename(original, box.Text));
        };
        reset.Click += (_, _) => box.Text = "";

        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(field);
        grid.Children.Add(reset);
        return grid;
    }

    private void UpdateSelected(string name, bool selected)
    {
        if (_loading) return;
        _app.UpdateSettings(s =>
        {
            s.SelectedHeadphones.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (selected) s.SelectedHeadphones.Add(name);
        });
    }

    private void UpdatePickerVisibility() =>
        HeadphonePicker.Visibility = _app.Settings.HeadphoneMode == HeadphoneMode.Selected
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void BuildAccentSwatches(string current)
    {
        AccentPanel.Children.Clear();
        foreach (var hex in AppSettings.AccentPresets)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(HudItems.ParseColor(hex, Colors.Gray)),
                GroupName = "accent",
                IsChecked = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase),
                ToolTip = hex,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Accent color {hex}");
            swatch.Checked += (_, _) =>
            {
                if (_loading) return;
                _app.UpdateSettings(s => s.HudAccent = hex);
                UpdateLook();
            };
            AccentPanel.Children.Add(swatch);
        }
    }

    private void Toggle(System.Windows.Controls.Primitives.ToggleButton box, Action<AppSettings, bool> apply)
    {
        box.Checked += (_, _) => { if (!_loading) _app.UpdateSettings(s => apply(s, true)); };
        box.Unchecked += (_, _) => { if (!_loading) _app.UpdateSettings(s => apply(s, false)); };
    }

    private void Choice<T>(ComboBox combo, Action<AppSettings, T> apply, Action? after = null) where T : struct, Enum
    {
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not ComboBoxItem item) return;
            if (!Enum.TryParse<T>((string)item.Tag, out var value)) return;
            _app.UpdateSettings(s => apply(s, value));
            after?.Invoke();
        };
    }

    private static void Select(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => _app.PreviewHud();

    // ------------------------------------------------------------------ pages and home

    private RadioButton[] PositionSpots() => [PosTopLeft, PosTopCenter, PosTopRight, PosBottomLeft, PosBottomCenter, PosBottomRight];

    private void ShowPage(RadioButton nav)
    {
        PageHome.Visibility = nav == NavHome ? Visibility.Visible : Visibility.Collapsed;
        PageSound.Visibility = nav == NavSound ? Visibility.Visible : Visibility.Collapsed;
        PageHud.Visibility = nav == NavHud ? Visibility.Visible : Visibility.Collapsed;
        PageAlerts.Visibility = nav == NavAlerts ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = nav == NavDevices ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = nav == NavGeneral ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
        if (nav == NavHome) UpdateHome();
    }

    /// <summary>Opens a page by name (used by the snapshot tool).</summary>
    internal void Open(string page)
    {
        var nav = page switch
        {
            "sound" => NavSound,
            "hud" => NavHud,
            "alerts" => NavAlerts,
            "devices" => NavDevices,
            "general" => NavGeneral,
            _ => NavHome,
        };
        nav.IsChecked = true;
    }

    private void GoSound(object sender, RoutedEventArgs e) => NavSound.IsChecked = true;

    private void GoHud(object sender, RoutedEventArgs e) => NavHud.IsChecked = true;

    private void GoAlerts(object sender, RoutedEventArgs e) => NavAlerts.IsChecked = true;

    private void GoGeneral(object sender, RoutedEventArgs e) => NavGeneral.IsChecked = true;

    /// <summary>Refreshes the Home page: greeting, current output, status chips and the summaries.</summary>
    private void UpdateHome()
    {
        var s = _app.Settings;
        var hour = DateTime.Now.Hour;
        Greeting.Text = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        NavVersion.Text = $"Version {App.Version}";
        HomeOutput.Text = _app.CurrentOutputName();

        SetChip(AutoDot, AutoChipText, s.AutoSwitch, "Auto-switching on", "Auto-switching off");
        SetChip(HudDot, HudChipText, s.HudEnabled, "HUD on", "HUD off");

        SoundSummary.Text = s.AutoSwitch
            ? (s.SwitchCommunications ? "Headphones take over sound and calls" : "Headphones take over the sound")
            : "Automatic switching is off";
        var theme = s.HudTheme == HudTheme.System ? "Same as Windows" : s.HudTheme.ToString();
        HudSummary.Text = $"{Describe(s.HudPosition)} · {theme} · {s.HudSeconds:0.#} s";
        var shown = new[]
        {
            s.HudHeadphones, s.HudControllers, s.HudUsb, s.HudPrivacy, s.HudVolume, s.HudMicMute, s.HudMedia,
            s.HudKeyboard, s.HudLockKeys, s.HudBrightness, s.HudNetwork, s.HudCpuGpu, s.HudHeat, s.HudMemoryDisk, s.HudPower,
        }.Count(on => on);
        AlertsSummary.Text = $"{shown} of 15 kinds of pop-up are on";
        GeneralSummary.Text = (s.AutoUpdateCheck ? "Checks for updates daily" : "Update checks are off")
            + (AutoStart.IsEnabled() ? " · Starts with Windows" : "");
    }

    private static void SetChip(System.Windows.Shapes.Ellipse dot, TextBlock text, bool on, string onText, string offText)
    {
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
        text.Text = on ? onText : offText;
    }

    private static string Describe(HudPosition position) => position switch
    {
        HudPosition.TopCenter => "Top center",
        HudPosition.TopLeft => "Top left",
        HudPosition.TopRight => "Top right",
        HudPosition.BottomLeft => "Bottom left",
        HudPosition.BottomRight => "Bottom right",
        _ => "Bottom center",
    };

    /// <summary>Redraws the live HUD previews (HUD page and Home) with the current look settings.</summary>
    private void UpdateLook()
    {
        UpdateHome();
        try
        {
            _previewWindow ??= CreatePreviewWindow();
            var s = _app.Settings;
            _previewWindow.ApplyTheme(s);
            _previewWindow.DataContext = HudItems.Device(Glyphs.Headphones, "Studio Headphones", true, s.HudBattery ? 80 : null);
            _previewWindow.SetFrame(1, 1, 1);
            _previewWindow.UpdateLayout();

            var root = _previewWindow.Root;
            var width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
            var height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            HudPreview.Source = bitmap;
            HomePreview.Source = bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("HUD preview failed", ex);
        }
    }

    // An off-screen copy of the HUD, used only to draw the preview pictures.
    private static HudWindow CreatePreviewWindow()
    {
        var window = new HudWindow { Left = -6000, Top = -6000, ShowActivated = false };
        window.Show();
        return window;
    }

    private void OnUpdateStateChanged() => Dispatcher.BeginInvoke(UpdateUpdateRow);

    private void UpdateUpdateRow()
    {
        UpdateRow.Description = _app.UpdateStatus.Length > 0 ? _app.UpdateStatus : $"Capsule {App.Version}";
        InstallUpdateButton.Visibility = _app.AvailableUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_app.AvailableUpdate is { } update) InstallUpdateButton.Content = $"Install {update.Version}";
        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = !_app.UpdateBusy;
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = _app.CheckForUpdatesAsync(manual: true);

    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => _app.InstallUpdate();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            Process.Start(new ProcessStartInfo { FileName = Log.Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the log folder", ex);
        }
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        // Only ever opens the fixed project address compiled into the app.
        if (!Uri.TryCreate(App.RepositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the project page", ex);
        }
    }
}
