using System.Globalization;
using System.Text;

namespace Capsule.Core;

/// <summary>
/// Cleans text that comes from devices, drivers and other apps before Capsule shows or logs it:
/// one line, no control or direction-override characters, and a length cap.
/// </summary>
internal static class DisplayText
{
    public const int DefaultMax = 120;

    public static string Clean(string? text, int max = DefaultMax)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var builder = new StringBuilder(Math.Min(text.Length, max + 8));
        var lastWasSpace = false;

        // Work on whole characters (runes), so characters outside the basic range are judged correctly
        // and the length cap never splits one in half.
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var hidden = category is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned;
            if (hidden || Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            if (builder.Length + rune.Utf16SequenceLength > max) break;
            builder.Append(rune.ToString());
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>For WPF menu items and check boxes: '_' would otherwise become a keyboard shortcut.</summary>
    public static string ForAccessText(string text) => text.Replace("_", "__");
}

/// <summary>Fires once a condition has held for a while, then stays quiet for a cool-down.</summary>
internal sealed class SustainedAlert
{
    private readonly TimeSpan _holdFor;
    private readonly TimeSpan _coolDown;
    private DateTime? _since;
    private DateTime _lastFired = DateTime.MinValue;

    public SustainedAlert(TimeSpan holdFor, TimeSpan coolDown)
    {
        _holdFor = holdFor;
        _coolDown = coolDown;
    }

    /// <summary>Returns true at the moment the alert should be shown.</summary>
    public bool Update(bool condition, DateTime now)
    {
        if (!condition)
        {
            _since = null;
            return false;
        }

        _since ??= now;
        if (now - _since.Value < _holdFor || now - _lastFired < _coolDown) return false;

        _lastFired = now;
        return true;
    }
}
