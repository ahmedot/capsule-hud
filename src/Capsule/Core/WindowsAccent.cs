using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace Capsule.Core;

/// <summary>
/// The accent colour chosen in Windows Settings > Personalization > Colors, in the shades Windows itself uses:
/// a lighter one on dark backgrounds and a darker one on light backgrounds. Read fresh each time, so a change in
/// Windows shows up the next time Capsule draws something.
/// </summary>
internal static class WindowsAccent
{
    private static readonly Color Fallback = Color.FromRgb(0x4C, 0xC2, 0xFF);

    /// <summary>How bright an accent must be to read well on Capsule's dark pill (about as bright as its own green).</summary>
    public const double MinBrightness = 0.45;

    public static Color ForDark => Bright(Read(UIColorType.AccentLight2));

    public static Color ForLight => Read(UIColorType.AccentDark1);

    public static Color Light1 => Bright(Read(UIColorType.AccentLight1));

    public static Color Light3 => Bright(Read(UIColorType.AccentLight3));

    public static Color Dark2 => Read(UIColorType.AccentDark2);

    /// <summary>
    /// Keeps the hue but mixes in white until the colour is at least <see cref="MinBrightness"/> (relative
    /// luminance), so dark accents such as deep purple stay readable on a dark background.
    /// </summary>
    internal static Color Bright(Color c)
    {
        if (Luminance(c) >= MinBrightness) return c;
        double low = 0, high = 1;
        for (var i = 0; i < 20; i++)
        {
            var mid = (low + high) / 2;
            if (Luminance(Mix(c, mid)) >= MinBrightness) high = mid;
            else low = mid;
        }

        return Mix(c, high);
    }

    internal static double Luminance(Color c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte v)
    {
        var x = v / 255.0;
        return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
    }

    private static Color Mix(Color c, double white) => Color.FromRgb(
        (byte)Math.Round(c.R + (255 - c.R) * white),
        (byte)Math.Round(c.G + (255 - c.G) * white),
        (byte)Math.Round(c.B + (255 - c.B) * white));

    private static Color Read(UIColorType type)
    {
        try
        {
            // A fresh lookup each time (only when something is drawn): no watcher, and never a stale colour.
            var c = new UISettings().GetColorValue(type);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return Fallback;
        }
    }
}
