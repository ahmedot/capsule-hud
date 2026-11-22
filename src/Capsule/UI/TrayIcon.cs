using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Capsule.Audio;
using Capsule.Core;
using Capsule.Interop;
using Forms = System.Windows.Forms;

namespace Capsule.UI;

/// <summary>Notification-area icon. Left click opens settings, right click opens a quick menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _menuHost;
    private readonly ContextMenu _menu = new();

    public TrayIcon(App app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Capsule",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            try
            {
                if (e.Button == Forms.MouseButtons.Left) _app.ShowSettings();
                else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            }
            catch (Exception ex)
            {
                Log.Error("Tray action failed", ex);
            }
        };
        _icon.BalloonTipClicked += (_, _) => _app.InstallUpdate();

        // An invisible window owns the menu so it closes when you click elsewhere.
        _menuHost = new Window
        {
            Width = 0,
            Height = 0,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
            ResizeMode = ResizeMode.NoResize,
            Title = "Capsule menu",
        };
        _menu.Closed += (_, _) => _menuHost.Hide();
    }

    public void SetTooltip(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text;

    /// <summary>A Windows notification about a new version; clicking it asks to install.</summary>
    public void ShowUpdateBalloon(Version version) =>
        _icon.ShowBalloonTip(10_000, $"Capsule {version} is available", "Click here to install it. Capsule will ask before changing anything.",
            Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menuHost.Close();
    }

    private void ShowMenu()
    {
        BuildMenu();
        _menuHost.Show();
        _menuHost.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(_menuHost).Handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var settings = _app.Settings;
        var outputs = _app.Audio.GetEndpoints(Interop.EDataFlow.Render);
        var current = _app.Audio.GetDefaultId(Interop.EDataFlow.Render, Interop.ERole.Multimedia);

        var output = new MenuItem { Header = "Sound output" };
        foreach (var endpoint in outputs.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = DisplayText.ForAccessText(
                    _app.Settings.DeviceNames.ContainsKey(endpoint.ShortName) ? _app.Shown(endpoint.ShortName) : endpoint.Name),
                IsCheckable = true,
                IsChecked = endpoint.Id == current,
            };
            var id = endpoint.Id;
            item.Click += (_, _) => _app.SwitchOutput(id);
            output.Items.Add(item);
        }

        if (outputs.Count == 0) output.Items.Add(new MenuItem { Header = "No devices", IsEnabled = false });
        _menu.Items.Add(output);
        _menu.Items.Add(new Separator());

        _menu.Items.Add(Toggle("Switch to headphones automatically", settings.AutoSwitch,
            on => _app.UpdateSettings(s => s.AutoSwitch = on)));
        _menu.Items.Add(Toggle("Show HUD", settings.HudEnabled,
            on => _app.UpdateSettings(s => s.HudEnabled = on)));
        _menu.Items.Add(new Separator());

        if (_app.AvailableUpdate is { } update)
        {
            _menu.Items.Add(Action($"Install update {update.Version}…", _app.InstallUpdate));
        }

        _menu.Items.Add(Action("Preview HUD", _app.PreviewHud));
        _menu.Items.Add(Action("Settings", _app.ShowSettings));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Action("Quit Capsule", _app.Quit));
    }

    private static MenuItem Toggle(string header, bool isOn, Action<bool> changed)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn };
        item.Click += (_, _) => changed(item.IsChecked);
        return item;
    }

    private static MenuItem Action(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Capsule.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon image could not be loaded", ex);
        }

        return System.Drawing.SystemIcons.Application;
    }
}
