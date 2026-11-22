using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capsule.Audio;
using Capsule.Core;
using Capsule.Hud;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.UI;

/// <summary>
/// Developer tool: "Capsule.exe --snapshot &lt;folder&gt;" renders sample HUDs and the settings window to PNG
/// files (used for the README). Shows no real device names.
/// </summary>
internal static class Snapshot
{
    public static void Run(App app, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var dark = new AppSettings();
            var light = new AppSettings { HudTheme = HudTheme.Light, HudAccent = "#4CC2FF" };
            var violet = new AppSettings { HudAccent = "#8B7CFF" };

            RenderHud(folder, "hud-headphones", dark, HudItems.Device(Glyphs.Headphones, "Studio Headphones", true, 80));
            RenderHud(folder, "hud-headphones-off", dark, HudItems.Device(Glyphs.Headphones, "Studio Headphones", false));
            RenderHud(folder, "hud-controller", violet, HudItems.Device(Glyphs.Controller, "Wireless Controller", true, 55));
            RenderHud(folder, "hud-volume", dark, HudItems.Volume("Studio Headphones", 0.64f, false));
            RenderHud(folder, "hud-mic", dark, HudItems.Microphone("USB Microphone", true));
            RenderHud(folder, "hud-light", light, HudItems.Device(Glyphs.Headphones, "Studio Headphones", true, 80));
            RenderHud(folder, "hud-media", dark, HudItems.Media("Midnight Drive", "Neon Avenue", true, SampleArt()));
            RenderHud(folder, "hud-brightness", new AppSettings { HudAccent = "#F5C04A" }, HudItems.Brightness(70));
            RenderHud(folder, "hud-capslock", dark, HudItems.LockKey("Caps Lock", true));
            RenderHud(folder, "hud-language", dark, HudItems.Info(Glyphs.Globe, "العربية", "AR", HudTone.Good));
            RenderHud(folder, "hud-cpu", dark, HudItems.Info(Glyphs.Gauge, "CPU 97%", "game.exe", HudTone.Bad));
            RenderHud(folder, "hud-gpu-hot", dark, HudItems.Info(Glyphs.Warning, "GPU 88 °C", "Running hot", HudTone.Bad));
            RenderHud(folder, "hud-camera", dark, HudItems.Info(Glyphs.Camera, "Camera in use", "Video Call", HudTone.Bad));
            RenderHud(folder, "hud-usb", violet, HudItems.Info(Glyphs.Usb, "Backup (E:)", "Connected · 128 GB", HudTone.Good));
            RenderHud(folder, "hud-network", new AppSettings { HudAccent = "#4CC2FF" }, HudItems.Info(Glyphs.Wifi, "Home Wi-Fi", "Wi-Fi · Connected", HudTone.Good));
            var charging = HudItems.Info(Glyphs.Charging, "Charging", "Charger connected", HudTone.Good);
            charging.Battery = 64;
            RenderHud(folder, "hud-charging", new AppSettings { HudAccent = "#F5C04A" }, charging);
            RenderHud(folder, "hud-update", dark, HudItems.Info(Glyphs.Update, "Capsule 1.1.0", "Update available", HudTone.Good));
            RenderAnimation(folder, dark, HudItems.Device(Glyphs.Headphones, "Studio Headphones", true, 80));

            app.DemoMode = true;
            var window = new SettingsWindow(app) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            window.Show();
            // One picture per page; each page gets a moment to finish drawing.
            string[] pages = ["home", "sound", "hud", "alerts", "devices", "general"];
            var step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                step++;
                if (step < 3) return;
                var index = step - 3;
                if (index > 0) CaptureWindow(window, Path.Combine(folder, $"settings-{pages[index - 1]}.png"));
                if (index < pages.Length)
                {
                    window.Open(pages[index]);
                    return;
                }

                timer.Stop();
                window.Close();
                app.Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Snapshot failed", ex);
            app.Shutdown(1);
        }
    }

    private static void RenderHud(string folder, string name, AppSettings settings, HudItem item)
    {
        var window = new HudWindow { DataContext = item, Left = -4000, Top = -4000, Opacity = 1 };
        window.ApplyTheme(settings);
        window.Show();
        window.SetFrame(1, 1, 1);
        window.UpdateLayout();

        const double scale = 2.0;
        var root = window.Root;
        var size = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right,
            root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window.Content as Visual ?? root);
        Save(bitmap, Path.Combine(folder, name + ".png"));
        window.Close();
    }

    /// <summary>Renders the open / hold / close animation as numbered PNG frames (30 fps).</summary>
    private static void RenderAnimation(string folder, AppSettings settings, HudItem item)
    {
        var frames = Path.Combine(folder, "anim");
        Directory.CreateDirectory(frames);
        var window = new HudWindow { DataContext = item, Left = -4000, Top = -4000 };
        window.ApplyTheme(settings);
        window.Show();
        window.SetFrame(1, 1, 1);
        window.UpdateLayout();

        const double hold = 900;
        var root = window.Root;
        var size = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var index = 0;
        for (double ms = 0; ms <= HudWindow.TotalMs(hold) + 1; ms += 1000.0 / 30)
        {
            var (opacity, pop, progress) = HudWindow.FrameAt(ms, hold);
            window.SetFrame(opacity, pop, progress);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(window.Content as Visual ?? root);
            Save(bitmap, Path.Combine(frames, $"frame-{index++:000}.png"));
        }

        window.Close();
    }

    // A gradient square standing in for album art.
    private static ImageSource SampleArt()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0xFF, 0x5F, 0x8F), Color.FromRgb(0x5B, 0x4B, 0xFF), 45);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, 96, 96));
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, new Point(48, 48), 22, 22);
        }

        var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void CaptureWindow(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)(window.ActualHeight * dpi.DpiScaleY);

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
