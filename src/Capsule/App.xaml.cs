using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Capsule.Audio;
using Capsule.Core;
using Capsule.Hud;
using Capsule.Interop;
using Capsule.Settings;
using Capsule.UI;

namespace Capsule;

public partial class App : Application
{
    private const string InstanceName = @"Local\Capsule.Instance";
    private const string ShowSignalName = @"Local\Capsule.ShowSettings";
    private const string QuitSignalName = @"Local\Capsule.Quit";
    private const string QuitArgument = "--quit";
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);

    private Mutex? _instance;
    private bool _ownsInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    private AudioNotifications? _notifications;
    private AutoSwitcher? _switcher;
    private VolumeWatcher? _volume;
    private ControllerWatcher? _controllers;
    private LockKeyWatcher? _lockKeys;
    private BrightnessWatcher? _brightness;
    private MediaWatcher? _media;
    private WindowsOsdHider? _osdHider;
    private DuplicatePopupHider? _duplicateHider;
    private SystemMonitor? _system;
    private PowerWatcher? _power;
    private KeyboardLanguageWatcher? _keyboard;
    private PrivacyWatcher? _privacy;
    private DriveWatcher? _drives;
    private NetworkWatcher? _network;
    private HudPresenter? _hud;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _deviceBatteryTimer;
    private readonly HashSet<string> _lowBatteryWarned = [];
    private bool _sessionActive = true;
    private Version? _announcedUpdate;

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0";

    /// <summary>Project page, opened from Settings > About. Empty hides the button.</summary>
    public static string RepositoryUrl => ProjectInfo.IsConfigured ? ProjectInfo.Url : "";

    internal AppSettings Settings { get; private set; } = new();

    internal AudioSystem Audio { get; private set; } = null!;

    /// <summary>Snapshot mode: show sample device names instead of the real ones.</summary>
    internal bool DemoMode { get; set; }

    internal UpdateInfo? AvailableUpdate { get; private set; }

    internal string UpdateStatus { get; private set; } = "";

    internal bool UpdateBusy { get; private set; }

    internal event Action? UpdateStateChanged;

    // ------------------------------------------------------------------ startup

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unexpected error", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Fatal error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Background task error", args.Exception);
            args.SetObserved();
        };
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, args) => Log.Error("Tray error", args.Exception);

        var snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotIndex >= 0 && snapshotIndex + 1 < e.Args.Length)
        {
            Snapshot.Run(this, e.Args[snapshotIndex + 1]);
            return;
        }

        if (HasArgument(e.Args, Installer.UninstallArgument))
        {
            Installer.Uninstall(HasArgument(e.Args, Installer.QuietArgument), QuitRunningCopy, RunningCopyGone);
            Shutdown();
            return;
        }

        if (!HasArgument(e.Args, QuitArgument) && !HasArgument(e.Args, Updater.UpdatedArgument)
            && Installer.ShouldInstall(e.Args))
        {
            if (Installer.Compare() == Installer.State.Newer)
            {
                // A newer Capsule is already installed: just open it rather than going back a version.
                try
                {
                    Installer.LaunchInstalled(argument: null);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not start the installed Capsule", ex);
                }

                Shutdown();
                return;
            }

            if (HasArgument(e.Args, Installer.InstallArgument))
            {
                // Silent install for IT admins and package managers: no window, then start Capsule.
                try
                {
                    var startWithWindows = Installer.Compare() == Installer.State.NotInstalled
                        || AutoStart.IsEnabledFor(Installer.InstalledExe);
                    Installer.Install(startWithWindows, QuitRunningCopy, RunningCopyGone);
                    Installer.LaunchInstalled(Installer.InstalledArgument);
                    Shutdown();
                }
                catch (Exception ex)
                {
                    Log.Error("Silent install failed", ex);
                    Shutdown(1);
                }

                return;
            }

            new InstallerWindow(this, e.Args).Show();
            return;
        }

        StartCapsule(e.Args);
    }

    private static bool HasArgument(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts the tray app. Also called by the installer window for "Try it without installing".</summary>
    internal void StartCapsule(string[] args)
    {
        var quit = HasArgument(args, QuitArgument);
        var updated = HasArgument(args, Updater.UpdatedArgument);
        var installed = HasArgument(args, Installer.InstalledArgument);
        if (quit || !AcquireInstance(waitForPrevious: updated || installed))
        {
            SignalRunningCopy(quit ? QuitSignalName : ShowSignalName);
            Shutdown();
            return;
        }

        Efficiency.Enable();
        var atSignIn = HasArgument(args, AutoStart.StartupArgument);
        var firstRun = !SettingsStore.Exists;
        Settings = SettingsStore.Load();
        ApplyAppTheme();
        Log.Info($"Capsule {Version} starting{(atSignIn ? " at sign-in" : "")}{(updated ? " after an update" : "")}{(installed ? " after installing" : "")}");
        Log.Info(HudWindow.HasGpu ? "HUD draws on the GPU" : "No GPU acceleration available; HUD draws in software");
        if (updated || installed) Updater.CleanUpAfterUpdate();
        Installer.RefreshAppsEntry();

        _showSignal = CreateSignal(ShowSignalName);
        if (_showSignal is not null)
        {
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        _quitSignal = CreateSignal(QuitSignalName);
        if (_quitSignal is not null)
        {
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal,
                (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(Settings);
        };

        StartWatchers(atSignIn);
        _tray = new TrayIcon(this);
        UpdateTooltip();
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _updateTimer = new DispatcherTimer { Interval = FirstUpdateCheck };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckEvery;
            if (Settings.AutoUpdateCheck) _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        if (firstRun) SettingsStore.Save(Settings);
        if (installed)
        {
            // Straight after the installer: a friendly hello in the real HUD, nothing to set up.
            Show(HudItems.Info(Glyphs.Checkmark, "Capsule is ready", "Enjoy", HudTone.Good));
        }
        else if (firstRun)
        {
            ShowSettings();
        }
        else if (updated)
        {
            Show(HudItems.Info(Glyphs.Update, $"Capsule {Version}", "Updated", HudTone.Good));
        }
        else if (!atSignIn)
        {
            ShowSettings();
        }

        // Hand back the memory used while starting up.
        var trim = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            Efficiency.TrimMemory();
        }, Dispatcher);
    }

    private void StartWatchers(bool atSignIn)
    {
        Audio = new AudioSystem();
        _hud = new HudPresenter(() => Settings);
        _switcher = new AutoSwitcher(Audio, () => Settings, Dispatcher);
        _volume = new VolumeWatcher(Audio, Dispatcher);
        _notifications = new AudioNotifications(Dispatcher);
        _osdHider = new WindowsOsdHider(Dispatcher);
        _osdHider.SetEnabled(Settings.HideWindowsOsd);
        _duplicateHider = new DuplicatePopupHider(Dispatcher) { Enabled = Settings.HideDuplicatePopups };

        _switcher.HeadphonesChanged += OnHeadphonesChanged;
        _switcher.Switched += () => _volume.BeQuiet();
        _volume.VolumeChanged += OnVolumeChanged;
        _volume.MicMuteChanged += OnMicMuteChanged;
        _notifications.EndpointChanged += id =>
        {
            _switcher.OnEndpointChanged(id);
            _settingsWindow?.RefreshDevices();
        };
        _notifications.DefaultChanged += (flow, role, id) =>
        {
            _volume.BeQuiet();
            _switcher.OnDefaultChanged(flow, role, id);
            _volume.Refresh();
            UpdateTooltip();
        };

        Audio.Register(_notifications);
        _switcher.Start(atSignIn);
        _volume.Refresh();
        ApplyWatchers();
    }

    // ------------------------------------------------------------------ watchers follow the settings

    /// <summary>
    /// Runs only the watchers whose HUD is switched on. Turning a HUD off stops its watcher straight away (timers,
    /// polling and Windows subscriptions), so nothing keeps working in the background for it.
    /// </summary>
    private void ApplyWatchers()
    {
        var s = Settings;
        var hud = s.HudEnabled && _sessionActive;
        Keep(ref _lockKeys, hud && s.HudLockKeys, CreateLockKeys);
        Keep(ref _brightness, hud && s.HudBrightness, CreateBrightness);
        Keep(ref _media, hud && s.HudMedia, CreateMedia);
        Keep(ref _keyboard, hud && s.HudKeyboard, CreateKeyboard);
        Keep(ref _privacy, hud && s.HudPrivacy, CreatePrivacy);
        Keep(ref _drives, hud && s.HudUsb, CreateDrives);
        Keep(ref _network, hud && s.HudNetwork, CreateNetwork);
        Keep(ref _power, hud && s.HudPower, CreatePower);
        Keep(ref _system, hud && (s.HudCpuGpu || s.HudHeat || s.HudMemoryDisk), CreateSystem);
        // Controllers feed their own HUD and the "controller battery low" warning.
        Keep(ref _controllers, hud && (s.HudControllers || s.HudPower), CreateControllers);

        var batteryChecks = hud && s.HudPower;
        if (batteryChecks && _deviceBatteryTimer is null)
        {
            _deviceBatteryTimer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background,
                (_, _) => CheckDeviceBatteries(), Dispatcher);
        }
        else if (!batteryChecks && _deviceBatteryTimer is not null)
        {
            _deviceBatteryTimer.Stop();
            _deviceBatteryTimer = null;
        }
    }

    private static void Keep<T>(ref T? watcher, bool wanted, Func<T> create) where T : class, IDisposable
    {
        if (wanted && watcher is null)
        {
            watcher = create();
        }
        else if (!wanted && watcher is not null)
        {
            watcher.Dispose();
            watcher = null;
        }
    }

    private LockKeyWatcher CreateLockKeys()
    {
        var w = new LockKeyWatcher(Dispatcher);
        w.Changed += (key, on) =>
        {
            if (!Settings.HudLockKeys) return;
            _duplicateHider?.Arm(); // only Capsule's pop-up should show
            Show(HudItems.LockKey(key, on));
        };
        return w;
    }

    private BrightnessWatcher CreateBrightness()
    {
        var w = new BrightnessWatcher(Dispatcher);
        w.Changed += percent =>
        {
            _osdHider?.Arm();
            if (Settings.HudBrightness) Show(HudItems.Brightness(percent));
        };
        w.Start();
        return w;
    }

    private MediaWatcher CreateMedia()
    {
        var w = new MediaWatcher(Dispatcher, () => Settings.HudEnabled && Settings.HudMedia);
        w.Changed += media =>
        {
            _osdHider?.Arm();
            if (Settings.HudMedia) Show(HudItems.Media(media.Title, media.Artist, media.Playing, media.Art));
        };
        w.Start();
        return w;
    }

    private KeyboardLanguageWatcher CreateKeyboard()
    {
        var w = new KeyboardLanguageWatcher(Dispatcher);
        w.Changed += (language, code) =>
        {
            if (Settings.HudKeyboard) Show(HudItems.Info(Glyphs.Globe, language, code, HudTone.Good, replaceKey: "keyboard"));
        };
        return w;
    }

    private PrivacyWatcher CreatePrivacy()
    {
        var w = new PrivacyWatcher(Dispatcher);
        w.Changed += (device, app, inUse) =>
        {
            if (!Settings.HudPrivacy) return;
            var glyph = device == "Camera" ? Glyphs.Camera : Glyphs.Microphone;
            Show(HudItems.Info(glyph, inUse ? $"{device} in use" : $"{device} not in use", app, inUse ? HudTone.Bad : HudTone.Neutral));
        };
        w.Start();
        return w;
    }

    private DriveWatcher CreateDrives()
    {
        var w = new DriveWatcher(Dispatcher);
        w.Changed += (name, details, connected) =>
        {
            if (Settings.HudUsb) Show(HudItems.Info(Glyphs.Usb, name, details, connected ? HudTone.Good : HudTone.Neutral));
        };
        w.Start();
        return w;
    }

    private NetworkWatcher CreateNetwork()
    {
        var w = new NetworkWatcher(Dispatcher);
        w.Changed += (glyph, title, status, good) =>
        {
            if (Settings.HudNetwork) Show(HudItems.Info(glyph, title, status, good ? HudTone.Good : HudTone.Bad, replaceKey: "network"));
        };
        w.Start();
        return w;
    }

    private PowerWatcher CreatePower()
    {
        var w = new PowerWatcher(Dispatcher);
        w.Changed += (title, status, percent, warning) =>
        {
            if (!Settings.HudPower) return;
            var item = HudItems.Info(warning ? Glyphs.Warning : Glyphs.Charging, title, status, warning ? HudTone.Bad : HudTone.Good);
            item.Battery = percent;
            Show(item);
        };
        return w;
    }

    private SystemMonitor CreateSystem()
    {
        var w = new SystemMonitor(Dispatcher, () => Settings);
        w.Alert += alert =>
        {
            var glyph = alert.Kind switch
            {
                HealthKind.Disk => Glyphs.HardDrive,
                HealthKind.GpuHot or HealthKind.Throttling => Glyphs.Warning,
                _ => Glyphs.Gauge,
            };
            Show(HudItems.Info(glyph, alert.Title, alert.Status, HudTone.Bad));
        };
        if (_sessionActive) w.Start(); // paused while the PC is locked
        return w;
    }

    private ControllerWatcher CreateControllers()
    {
        var w = new ControllerWatcher(Dispatcher, () => Settings);
        w.Changed += OnControllerChanged;
        w.Start();
        return w;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_saveTimer?.IsEnabled == true) SettingsStore.Save(Settings);
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _showWait?.Unregister(null);
            _showSignal?.Dispose();
            _quitWait?.Unregister(null);
            _quitSignal?.Dispose();
            if (_notifications is not null) Audio.Unregister(_notifications);
            _volume?.Dispose();
            _controllers?.Dispose();
            _lockKeys?.Dispose();
            _brightness?.Dispose();
            _media?.Dispose();
            _osdHider?.Dispose(); // puts the Windows volume pop-up back
            _duplicateHider?.Dispose();
            _system?.Dispose();
            _power?.Dispose();
            _keyboard?.Dispose();
            _privacy?.Dispose();
            _drives?.Dispose();
            _network?.Dispose();
            _tray?.Dispose();
            Audio?.Dispose();
            if (_tray is not null) Log.Info("Capsule stopped");
            if (_ownsInstance) _instance?.ReleaseMutex();
            _instance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error while closing", ex);
        }

        base.OnExit(e);
    }

    /// <summary>One copy per user session. After an update, wait for the old copy to finish closing.</summary>
    private bool AcquireInstance(bool waitForPrevious)
    {
        var deadline = DateTime.UtcNow + (waitForPrevious ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
        while (true)
        {
            bool created;
            try
            {
                _instance = new Mutex(initiallyOwned: true, InstanceName, out created);
            }
            catch (UnauthorizedAccessException)
            {
                // A copy running as administrator holds the name: that copy is the one running.
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(250);
                continue;
            }

            if (created)
            {
                _ownsInstance = true;
                return true;
            }

            _instance.Dispose();
            _instance = null;

            // Someone holds the name but no Capsule answers on it: don't let that stop us from starting.
            if (!waitForPrevious && !SignalExists(ShowSignalName))
            {
                Log.Info("Instance name is taken but no running Capsule was found; starting anyway");
                return true;
            }

            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
    }

    private static bool SignalExists(string name)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void QuitRunningCopy() => SignalRunningCopy(QuitSignalName);

    /// <summary>True once no Capsule is running in this session (its single-instance lock is gone).</summary>
    internal static bool RunningCopyGone()
    {
        try
        {
            if (!Mutex.TryOpenExisting(InstanceName, out var mutex)) return true;
            mutex.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // held by a copy running as administrator
        }
    }

    private static void SignalRunningCopy(string name)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(name);
            signal.Set();
        }
        catch
        {
            // Not running, or starting/stopping.
        }
    }

    // Only listen on a control signal Capsule created itself; never adopt one another program made first.
    private static EventWaitHandle? CreateSignal(string name)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var created);
        if (created) return signal;
        signal.Dispose();
        Log.Error($"Control signal {name} already existed; ignoring it");
        return null;
    }

    // ------------------------------------------------------------------ settings, theme, session

    internal void UpdateSettings(Action<AppSettings> change)
    {
        var headphoneRules = (Settings.HeadphoneMode, string.Join("|", Settings.SelectedHeadphones));
        var theme = Settings.AppTheme;
        change(Settings);
        Settings.Normalize();

        if (headphoneRules != (Settings.HeadphoneMode, string.Join("|", Settings.SelectedHeadphones))) _switcher?.Rescan();
        if (theme != Settings.AppTheme) ApplyAppTheme();
        _osdHider?.SetEnabled(Settings.HideWindowsOsd);
        if (_duplicateHider is not null) _duplicateHider.Enabled = Settings.HideDuplicatePopups;
        if (_hud is not null) ApplyWatchers();

        _saveTimer?.Stop();
        _saveTimer?.Start();
        UpdateTooltip();
    }

    private void ApplyAppTheme()
    {
        ThemeMode = Settings.AppTheme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    // While the session is locked or switched away, don't touch audio, don't draw, and don't sample.
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var active = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => false,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => true,
            _ => _sessionActive,
        };

        Dispatcher.BeginInvoke(() =>
        {
            if (active == _sessionActive) return;
            _sessionActive = active;
            if (_switcher is not null) _switcher.Paused = !active;
            if (active) _switcher?.Rescan();
            ApplyWatchers(); // stops every pop-up watcher while locked, restarts them on unlock

            Log.Info(active ? "Session active again" : "Session locked or switched away; paused");
        });
    }

    internal void Show(HudItem item)
    {
        if (_sessionActive) _hud?.Show(item);
    }

    internal void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                Efficiency.TrimMemory(); // the settings window is the heaviest part of Capsule
            };
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    internal void SwitchOutput(string id)
    {
        _volume?.BeQuiet();
        Audio.SetDefault(id, Settings.SwitchCommunications);
    }

    /// <summary>Shows a short sequence of sample HUDs.</summary>
    internal void PreviewHud()
    {
        if (_hud is null) return;
        if (!Settings.HudEnabled) UpdateSettings(s => s.HudEnabled = true);

        Show(HudItems.Device(Glyphs.Headphones, "Studio Headphones", true, Settings.HudBattery ? 80 : null));
        Show(HudItems.Info(Glyphs.Globe, "العربية", "AR", HudTone.Good));
        Show(HudItems.Info(Glyphs.Gauge, "CPU 97%", "game.exe", HudTone.Bad));
        Show(HudItems.Device(Glyphs.Headphones, "Studio Headphones", false));
    }

    /// <summary>One sample HUD, used while changing look settings.</summary>
    internal void PreviewOne() =>
        _hud?.Show(new HudItem
        {
            ReplaceKey = "preview",
            Glyph = Glyphs.Headphones,
            Title = "Studio Headphones",
            Status = "Connected",
            Battery = Settings.HudBattery ? 80 : null,
        });

    internal void Quit() => Shutdown();

    /// <summary>The name shown for a device: the user's custom name if they set one.</summary>
    internal string Shown(string windowsName) => Settings.NameFor(windowsName);

    /// <summary>The name of the device sound is playing on right now (the user's own name if they set one).</summary>
    internal string CurrentOutputName()
    {
        if (DemoMode) return "Studio Headphones";
        var current = Audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia);
        return current is null ? "No sound device" : Shown(Audio.GetEndpoint(current)?.ShortName ?? "Unknown device");
    }

    internal string StatusLine()
    {
        if (DemoMode) return "Auto-switching is on  ·  Sound is playing on Studio Headphones";
        var current = Audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia);
        var name = current is null ? "no output" : Shown(Audio.GetEndpoint(current)?.ShortName ?? "unknown");
        var mode = Settings.AutoSwitch ? "Auto-switching is on" : "Auto-switching is off";
        return $"{mode}  ·  Sound is playing on {name}";
    }

    // ------------------------------------------------------------------ device lists for the settings window

    internal IReadOnlyList<string> OutputNames() => DemoMode
        ? ["Headphones (Studio Headphones)", "Speakers (Desktop Speakers)", "Monitor (HDMI Audio)"]
        : Audio.GetEndpoints(EDataFlow.Render, DeviceState.Active | DeviceState.Unplugged)
            .Where(e => !DeviceRules.IsHandsFree(e.Name) && e.Name.Length > 0)
            .Select(e => e.Name)
            .ToList();

    internal IReadOnlyList<string> MicNames() => DemoMode
        ? ["USB Microphone", "Webcam Microphone"]
        : Audio.GetEndpoints(EDataFlow.Capture)
            .Where(e => !DeviceRules.IsHandsFree(e.Name) && e.Name.Length > 0)
            .Select(e => e.Name)
            .ToList();

    /// <summary>Devices the user can rename: (Windows name, icon). Includes ones renamed earlier but not connected now.</summary>
    internal IReadOnlyList<(string Name, string Glyph)> RenameableDevices()
    {
        var list = new List<(string Name, string Glyph)>();
        if (DemoMode)
        {
            list.Add(("Studio Headphones", Glyphs.Headphones));
            list.Add(("Desktop Speakers", Glyphs.Speakers));
            list.Add(("USB Microphone", Glyphs.Microphone));
            list.Add(("Wireless Controller", Glyphs.Controller));
        }
        else
        {
            foreach (var e in Audio.GetEndpoints(EDataFlow.Render, DeviceState.Active | DeviceState.Unplugged))
            {
                if (DeviceRules.IsHandsFree(e.Name)) continue;
                var headphones = e.FormFactor is FormFactor.Headphones or FormFactor.Headset;
                list.Add((e.ShortName, headphones ? Glyphs.Headphones : Glyphs.Speakers));
            }

            foreach (var e in Audio.GetEndpoints(EDataFlow.Capture))
            {
                if (!DeviceRules.IsHandsFree(e.Name)) list.Add((e.ShortName, Glyphs.Microphone));
            }

            foreach (var name in _controllers?.PresentNames ?? []) list.Add((name, Glyphs.Controller));
        }

        foreach (var name in Settings.DeviceNames.Keys) list.Add((name, Glyphs.Bluetooth));

        return list
            .Where(d => d.Name.Length > 0)
            .DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ updates

    internal async Task CheckForUpdatesAsync(bool manual)
    {
        if (UpdateBusy) return;
        if (!ProjectInfo.IsConfigured)
        {
            SetUpdateStatus("Updates aren't set up in this build.");
            return;
        }

        UpdateBusy = true;
        SetUpdateStatus("Checking for updates…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            AvailableUpdate = await Updater.CheckAsync(timeout.Token);
            if (AvailableUpdate is null)
            {
                SetUpdateStatus($"You're up to date (Capsule {Version}).");
            }
            else
            {
                SetUpdateStatus($"Capsule {AvailableUpdate.Version} is available.");
                if (!manual && AvailableUpdate.Version != _announcedUpdate)
                {
                    _announcedUpdate = AvailableUpdate.Version;
                    Show(HudItems.Info(Glyphs.Update, $"Capsule {AvailableUpdate.Version}", "Update available", HudTone.Good));
                    _tray?.ShowUpdateBalloon(AvailableUpdate.Version);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            SetUpdateStatus("Couldn't check for updates. Are you online?");
        }
        finally
        {
            UpdateBusy = false;
            UpdateStateChanged?.Invoke();
        }
    }

    /// <summary>Asks the user, then downloads, verifies and installs the update.</summary>
    internal async void InstallUpdate()
    {
        var update = AvailableUpdate;
        if (update is null || UpdateBusy) return;

        var answer = MessageBox.Show(
            $"Install Capsule {update.Version} now?\n\nCapsule downloads the update from GitHub, checks its fingerprint, and restarts. It only takes a moment.",
            "Capsule update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;

        UpdateBusy = true;
        try
        {
            SetUpdateStatus("Downloading the update…");
            var progress = new Progress<double>(p => SetUpdateStatus($"Downloading the update… {p:P0}"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var file = await Updater.DownloadAsync(update, progress, timeout.Token);
            SetUpdateStatus("Installing…");
            if (Updater.InstallAndRestart(file))
            {
                Shutdown();
                return;
            }

            SetUpdateStatus("Couldn't install it automatically, so the download page is open.");
            OpenUrl(update.PageUrl);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            SetUpdateStatus("The update didn't finish. Nothing was changed; try again later.");
        }
        finally
        {
            UpdateBusy = false;
            UpdateStateChanged?.Invoke();
        }
    }

    private void SetUpdateStatus(string text)
    {
        UpdateStatus = text;
        UpdateStateChanged?.Invoke();
    }

    internal static void OpenUrl(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the page", ex);
        }
    }

    // ------------------------------------------------------------------ device events

    private void UpdateTooltip()
    {
        if (_tray is null) return;
        var current = Audio.GetDefaultId(EDataFlow.Render, ERole.Multimedia);
        var name = current is null ? "no output" : Shown(Audio.GetEndpoint(current)?.ShortName ?? "unknown");
        _tray.SetTooltip($"Capsule · {name}");
    }

    private async void OnHeadphonesChanged(AudioEndpoint endpoint, bool connected)
    {
        _settingsWindow?.RefreshDevices();
        if (!connected) _lowBatteryWarned.Remove(endpoint.Id);
        if (!Settings.HudHeadphones || _hud is null) return;

        var item = HudItems.Device(Glyphs.Headphones, Shown(endpoint.ShortName), connected);
        Show(item);

        if (!connected || !Settings.HudBattery) return;

        // Bluetooth battery level often shows up a moment after the connection.
        for (var attempt = 0; attempt < 3 && item.Battery is null; attempt++)
        {
            var facts = await ContainerInfo.ReadAsync(endpoint.ContainerId);
            if (facts.BatteryPercent is int percent)
            {
                item.Battery = percent;
                return;
            }

            await Task.Delay(700);
        }
    }

    private void OnControllerChanged(ControllerInfo controller, bool connected)
    {
        if (!connected) _lowBatteryWarned.Remove(controller.Key);
        if (!Settings.HudControllers) return;
        Show(HudItems.Device(Glyphs.Controller, Shown(controller.Name), connected,
            Settings.HudBattery ? controller.BatteryPercent : null));
    }

    private void OnVolumeChanged(AudioEndpoint endpoint, float level, bool muted)
    {
        _osdHider?.Arm();
        if (Settings.HudVolume) Show(HudItems.Volume(Shown(endpoint.ShortName), level, muted));
    }

    private void OnMicMuteChanged(AudioEndpoint endpoint, bool muted)
    {
        if (Settings.HudMicMute) Show(HudItems.Microphone(Shown(endpoint.ShortName), muted));
    }

    // Warn once per connection when headphones or a controller run low.
    private async void CheckDeviceBatteries()
    {
        if (!Settings.HudPower || !_sessionActive || _switcher is null || _controllers is null) return;
        try
        {
            var threshold = Settings.DeviceBatteryAlertPercent;
            foreach (var headphones in _switcher.Connected.ToList())
            {
                var facts = await ContainerInfo.ReadAsync(headphones.ContainerId);
                if (facts.BatteryPercent is int percent && percent <= threshold && _lowBatteryWarned.Add(headphones.Id))
                {
                    var item = HudItems.Info(Glyphs.Headphones, Shown(headphones.ShortName), "Battery low", HudTone.Bad);
                    item.Battery = percent;
                    Show(item);
                }
            }

            foreach (var (key, name, percent) in _controllers.Batteries())
            {
                if (percent <= threshold && _lowBatteryWarned.Add(key))
                {
                    var item = HudItems.Info(Glyphs.Controller, Shown(name), "Battery low", HudTone.Bad);
                    item.Battery = percent;
                    Show(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Battery check failed", ex);
        }
    }
}
