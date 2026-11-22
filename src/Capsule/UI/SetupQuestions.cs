using Capsule.Core;
using Capsule.Settings;

namespace Capsule.UI;

/// <summary>One answer on a setup screen: what it says and what it changes.</summary>
internal sealed record SetupChoice(string Glyph, string Title, string Description, Action<AppSettings> Apply, bool Recommended = false);

/// <summary>One quick setup question in the installer.</summary>
internal sealed record SetupQuestion(string Title, string Hint, IReadOnlyList<SetupChoice> Choices)
{
    public SetupChoice Recommended => Choices.FirstOrDefault(c => c.Recommended) ?? Choices[0];
}

/// <summary>
/// The few questions worth asking on this PC, in plain words. Questions about things the PC doesn't have (a laptop
/// battery, a second keyboard language, a game controller) are simply not asked.
/// </summary>
internal static class SetupQuestions
{
    private const string Cancel = "";

    public static List<SetupQuestion> For(PcProfile pc)
    {
        var list = new List<SetupQuestion>
        {
            new("Where should Capsule pop up?", "Pick a spot. You can change it any time in Capsule's settings.",
            [
                new("", "Bottom center", "Just above the taskbar, easy to glance at",
                    s => s.HudPosition = HudPosition.BottomCenter, Recommended: true),
                new("", "Top center", "At the top edge of your screen", s => s.HudPosition = HudPosition.TopCenter),
                new("", "Bottom right", "In the corner, next to the clock", s => s.HudPosition = HudPosition.BottomRight),
            ]),
            new(pc.Headphones is { } headphones ? $"We found your {headphones}" : "When you connect headphones…",
                pc.Headphones is null
                    ? "Should your sound move to them by itself?"
                    : "When they connect, should your sound move to them by itself?",
            [
                new(Glyphs.Headphones, "Yes, sound and calls", "Music, videos and calls all follow your headphones",
                    s => { s.AutoSwitch = true; s.SwitchCommunications = true; }, Recommended: true),
                new(Glyphs.Volume3, "Yes, but keep calls where they are", "Only music and videos move",
                    s => { s.AutoSwitch = true; s.SwitchCommunications = false; }),
                new(Cancel, "No, I'll switch myself", "Capsule still shows when they connect", s => s.AutoSwitch = false),
            ]),
        };

        if (pc.GamingHint is { } gaming)
        {
            list.Add(new("Do you play games on this PC?", $"We noticed {gaming} here.",
            [
                new(Glyphs.Controller, "Yes, I play games",
                    "No \"CPU or GPU is busy\" warnings, since games keep them busy. Heat warnings stay on.",
                    s => s.HudCpuGpu = false, Recommended: true),
                new(Glyphs.Gauge, "Not really", "Warn me when an app keeps my CPU or GPU busy", s => s.HudCpuGpu = true),
            ]));
        }

        if (pc.Languages.Count >= 2)
        {
            list.Add(new($"You type in {Join(pc.Languages)}", "Show which language is active when you switch?",
            [
                new(Glyphs.Globe, "Yes, show it", "A small pop-up each time you switch", s => s.HudKeyboard = true, Recommended: true),
                new(Cancel, "No, thanks", "Switch languages quietly", s => s.HudKeyboard = false),
            ]));
        }

        if (pc.KeyboardApp is { } app)
        {
            list.Add(new($"{app} also shows Caps Lock pop-ups", "Would you like just one pop-up, Capsule's?",
            [
                new(Glyphs.Checkmark, "Yes, only Capsule's", $"Capsule hides the duplicate from {app}",
                    s => s.HideDuplicatePopups = true, Recommended: true),
                new(Cancel, "No, leave it", "Both pop-ups keep showing", s => s.HideDuplicatePopups = false),
            ]));
        }

        if (pc.IsLaptop)
        {
            list.Add(new("This looks like a laptop", "Show pop-ups for the charger and low battery?",
            [
                new(Glyphs.Charging, "Yes, please", "Charger plugged in or out, and battery at 20% and 10%",
                    s => s.HudPower = true, Recommended: true),
                new(Cancel, "No, thanks", "No battery pop-ups, including for headphones and controllers",
                    s => s.HudPower = false),
            ]));
        }

        return list;
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and {names[2]}",
        _ => $"{names.Count} languages",
    };
}
