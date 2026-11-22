using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Connectivity = Windows.Networking.Connectivity;
using Forms = System.Windows.Forms;

namespace Capsule.Core;

/// <summary>Laptop charger plugged/unplugged and low battery.</summary>
internal sealed class PowerWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private Forms.PowerLineStatus _line;
    private int _lowWarnedAt = 101;

    public PowerWatcher(Dispatcher ui)
    {
        _ui = ui;
        _line = Forms.SystemInformation.PowerStatus.PowerLineStatus;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Check(), ui);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Title, status, battery percent, warning?</summary>
    public event Action<string, string, int, bool>? Changed;

    public static bool HasBattery =>
        (Forms.SystemInformation.PowerStatus.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0
        && Forms.SystemInformation.PowerStatus.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) _ui.BeginInvoke(Check);
    }

    private void Check()
    {
        if (!HasBattery) return;
        var status = Forms.SystemInformation.PowerStatus;
        var percent = (int)Math.Round(Math.Clamp(status.BatteryLifePercent, 0f, 1f) * 100);
        var line = status.PowerLineStatus;

        if (line != _line && line != Forms.PowerLineStatus.Unknown)
        {
            _line = line;
            _lowWarnedAt = 101;
            var plugged = line == Forms.PowerLineStatus.Online;
            Changed?.Invoke(plugged ? "Charging" : "On battery", plugged ? "Charger connected" : "Charger unplugged", percent, false);
            return;
        }

        // Warn at 20% and again at 10% while running on battery.
        if (line == Forms.PowerLineStatus.Offline)
        {
            var level = percent <= 10 ? 10 : percent <= 20 ? 20 : 101;
            if (level < _lowWarnedAt)
            {
                _lowWarnedAt = level;
                Changed?.Invoke("Battery low", "Plug in your charger", percent, true);
            }
        }
    }
}

/// <summary>Shows the input language when you switch it (for example English to Arabic).</summary>
internal sealed class KeyboardLanguageWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private IntPtr _window;
    private ushort _language;

    public KeyboardLanguageWatcher(Dispatcher ui)
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Check(), ui);
    }

    /// <summary>Language name, short code.</summary>
    public event Action<string, string>? Changed;

    public void Dispose() => _timer.Stop();

    private void Check()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        var thread = GetWindowThreadProcessId(window, out _);
        var language = (ushort)((long)GetKeyboardLayout(thread) & 0xFFFF);
        if (language == 0) return;

        // Only a change inside the same window counts as "you switched language";
        // moving between windows with different languages is not announced.
        if (window == _window && language != _language && _language != 0)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                var neutral = culture.IsNeutralCulture || culture.Parent == CultureInfo.InvariantCulture ? culture : culture.Parent;
                Changed?.Invoke(DisplayText.Clean(neutral.NativeName, 40), culture.TwoLetterISOLanguageName.ToUpperInvariant());
            }
            catch (CultureNotFoundException)
            {
                // Unknown layout id: say nothing.
            }
        }

        _window = window;
        _language = language;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);
}

/// <summary>
/// Privacy indicator: an app started or stopped using the camera or microphone.
/// Reads Windows' own "recent activity" records (the same ones shown in Settings > Privacy); nothing is hooked.
/// </summary>
internal sealed class PrivacyWatcher : IDisposable
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private readonly Dispatcher _ui;
    private readonly List<RegistryChangeWatcher> _watchers = [];
    private readonly object _gate = new();
    private Dictionary<string, string>? _camera;
    private Dictionary<string, string>? _microphone;
    private bool _disposed;

    public PrivacyWatcher(Dispatcher ui) => _ui = ui;

    /// <summary>Device ("Camera"/"Microphone"), app name, in use?</summary>
    public event Action<string, string, bool>? Changed;

    /// <summary>Reads once, then sleeps until Windows says the records changed (no polling).</summary>
    public void Start() => Task.Run(() =>
    {
        Check();
        foreach (var capability in new[] { "webcam", "microphone" })
        {
            var watcher = RegistryChangeWatcher.TryCreate($@"{Store}\{capability}", Check);
            if (watcher is null) continue;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _watchers.Add(watcher);
                    continue;
                }
            }

            watcher.Dispose(); // switched off while starting
        }
    });

    public void Dispose()
    {
        RegistryChangeWatcher[] watchers;
        lock (_gate)
        {
            _disposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
        }

        foreach (var watcher in watchers) watcher.Dispose();
    }

    private void Check()
    {
        lock (_gate)
        {
            try
            {
                Compare("Camera", ref _camera, InUse("webcam"));
                Compare("Microphone", ref _microphone, InUse("microphone"));
            }
            catch (Exception ex)
            {
                Log.Error("Privacy check failed", ex);
            }
        }
    }

    private void Compare(string device, ref Dictionary<string, string>? previous, Dictionary<string, string> now)
    {
        if (previous is not null && !_disposed)
        {
            foreach (var (key, name) in now)
            {
                if (!previous.ContainsKey(key)) _ui.BeginInvoke(() => Changed?.Invoke(device, name, true));
            }

            foreach (var (key, name) in previous)
            {
                if (!now.ContainsKey(key)) _ui.BeginInvoke(() => Changed?.Invoke(device, name, false));
            }
        }

        previous = now;
    }

    // Windows keeps one record per app that ever used the device. Real PCs have a few hundred at most; the cap only
    // stops a flooded list from making each check slow.
    private const int MaxRecords = 5000;

    private static Dictionary<string, string> InUse(string capability)
    {
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey($@"{Store}\{capability}");
        if (root is null) return apps;

        foreach (var name in root.GetSubKeyNames().Take(MaxRecords))
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var exe in key.GetSubKeyNames().Take(MaxRecords))
                {
                    using var app = key.OpenSubKey(exe);
                    if (app is not null && IsActive(app)) apps[exe] = DesktopAppName(exe);
                }
            }
            else if (IsActive(key))
            {
                apps[name] = PackagedAppName(name);
            }
        }

        return apps;
    }

    // An app is using the device while it has a start time and no stop time.
    private static bool IsActive(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    private static string DesktopAppName(string keyName)
    {
        var path = keyName.Replace('#', '\\');
        var local = path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\'
            && !path.StartsWith(@"\\", StringComparison.Ordinal);
        try
        {
            if (local && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var described = DisplayText.Clean(info.FileDescription, 40);
                // A program writes its own description, so name the real file too unless the description already does.
                var file = DisplayText.Clean(Path.GetFileName(path), 32);
                if (described.Length > 0)
                {
                    return described.Contains(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)
                        ? described
                        : $"{described} ({file})";
                }
            }
        }
        catch
        {
            // Fall back to the file name.
        }

        return DisplayText.Clean(Path.GetFileNameWithoutExtension(path), 40);
    }

    private static string PackagedAppName(string packageFamily)
    {
        // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Windows Camera"
        var name = packageFamily.Split('_')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        var spaced = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return DisplayText.Clean(spaced, 40);
    }
}

/// <summary>USB drives and other volumes connected or removed (WMI volume events).</summary>
internal sealed class DriveWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private bool _disposed;

    public DriveWatcher(Dispatcher ui) => _ui = ui;

    /// <summary>Drive name, details, connected?</summary>
    public event Action<string, string, bool>? Changed;

    public void Start() => Task.Run(() =>
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives()) Remember(drive.Name);
            var watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3"));
            watcher.EventArrived += OnEvent;
            watcher.Start();
            lock (_gate)
            {
                if (!_disposed)
                {
                    _watcher = watcher;
                    return;
                }
            }

            Stop(watcher); // switched off while starting
        }
        catch (Exception ex)
        {
            Log.Error("Drive watching is unavailable", ex);
        }
    });

    public void Dispose()
    {
        ManagementEventWatcher? watcher;
        lock (_gate)
        {
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is not null) Stop(watcher);
    }

    private void Stop(ManagementEventWatcher watcher)
    {
        try
        {
            watcher.EventArrived -= OnEvent;
            watcher.Stop();
            watcher.Dispose();
        }
        catch
        {
            // Already stopped.
        }
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var letter = (e.NewEvent["DriveName"] as string ?? "").Trim();
            var type = Convert.ToInt32(e.NewEvent["EventType"]);
            if (letter.Length is < 2 or > 3 || !char.IsLetter(letter[0]) || letter[1] != ':') return;
            var root = letter[..2] + "\\";

            if (type == 2)
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram) return;
                var name = Remember(root);
                var size = drive.IsReady ? $" · {FormatSize(drive.TotalSize)}" : "";
                _ui.BeginInvoke(() => Changed?.Invoke(name, $"Connected{size}", true));
            }
            else if (type == 3)
            {
                string name;
                lock (_gate)
                {
                    name = _labels.TryGetValue(root, out var known) ? known : $"Drive {root.TrimEnd('\\')}";
                    _labels.Remove(root);
                }

                _ui.BeginInvoke(() => Changed?.Invoke(name, "Removed", false));
            }
        }
        catch
        {
            // The drive disappeared while we looked at it.
        }
    }

    private string Remember(string root)
    {
        var name = $"Drive {root.TrimEnd('\\')}";
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                var label = DisplayText.Clean(drive.VolumeLabel, 32);
                if (label.Length > 0) name = $"{label} ({root.TrimEnd('\\')})";
            }
        }
        catch
        {
            // Use the letter only.
        }

        lock (_gate)
        {
            _labels[root] = name;
        }

        return name;
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000_000 ? $"{bytes / 1_000_000_000_000.0:0.#} TB" : $"{bytes / 1_000_000_000.0:0} GB";
}

/// <summary>Internet connection, network type and VPN changes. Reads Windows' state; sends nothing.</summary>
internal sealed class NetworkWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _settle;
    private NetworkState? _last;

    public NetworkWatcher(Dispatcher ui)
    {
        _ui = ui;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Check();
        };
    }

    /// <summary>Glyph, title, status, good news?</summary>
    public event Action<string, string, string, bool>? Changed;

    public void Start()
    {
        _last = Read();
        Connectivity.NetworkInformation.NetworkStatusChanged += OnChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public void Dispose()
    {
        Connectivity.NetworkInformation.NetworkStatusChanged -= OnChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _settle.Stop();
    }

    private void OnChanged(object? sender) => _ui.BeginInvoke(Restart);

    private void OnAddressChanged(object? sender, EventArgs e) => _ui.BeginInvoke(Restart);

    // Networks change in bursts; wait for it to settle before announcing.
    private void Restart()
    {
        _settle.Stop();
        _settle.Start();
    }

    private void Check()
    {
        var now = Read();
        var before = _last;
        _last = now;
        if (before is null) return;

        if (now.Vpn != before.Vpn)
        {
            Changed?.Invoke(Glyphs.Vpn, now.Vpn is null ? "VPN disconnected" : "VPN connected", now.Vpn ?? before.Vpn ?? "", now.Vpn is not null);
        }

        if (now.Online != before.Online || (now.Online && (now.Kind != before.Kind || now.Name != before.Name)))
        {
            if (now.Online)
            {
                Changed?.Invoke(now.Kind == "Wi-Fi" ? Glyphs.Wifi : Glyphs.Ethernet, now.Name.Length > 0 ? now.Name : now.Kind,
                    $"{now.Kind} · Connected", true);
            }
            else
            {
                Changed?.Invoke(Glyphs.NoInternet, "No internet", before.Name.Length > 0 ? $"{before.Name} lost" : "Connection lost", false);
            }
        }
    }

    private static NetworkState Read()
    {
        var online = false;
        string kind = "Network", name = "";
        try
        {
            var profile = Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null)
            {
                online = profile.GetNetworkConnectivityLevel() == Connectivity.NetworkConnectivityLevel.InternetAccess;
                kind = profile.IsWlanConnectionProfile ? "Wi-Fi" : profile.IsWwanConnectionProfile ? "Cellular" : "Ethernet";
                name = DisplayText.Clean(profile.ProfileName, 40);
            }
        }
        catch
        {
            // Treat as offline.
        }

        return new NetworkState(online, kind, name, VpnName());
    }

    private static string? VpnName()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var description = nic.Description;
                var looksLikeVpn = nic.NetworkInterfaceType == NetworkInterfaceType.Ppp
                    || description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase);
                if (looksLikeVpn) return DisplayText.Clean(nic.Name, 40);
            }
        }
        catch
        {
            // Can't list the adapters: report no VPN.
        }

        return null;
    }

    private sealed record NetworkState(bool Online, string Kind, string Name, string? Vpn);
}
