using Capsule.Audio;
using Capsule.Core;
using Capsule.Interop;

namespace Capsule.Tests;

public class DisplayTextTests
{
    // Special characters are built from their codes so this file itself contains none of them.
    private static string S(params object[] parts) =>
        string.Concat(parts.Select(p => p is int code ? ((char)code).ToString() : (string)p));

    public static TheoryData<string?, string> Samples() => new()
    {
        { S("Line 1", 0x0A, "Line 2"), "Line 1 Line 2" },
        { S("Line 1", 0x0D, 0x0A, "Line 2"), "Line 1 Line 2" },
        { S("A", 0x2028, "B", 0x2029, "C", 0x85, "D"), "A B C D" },
        { "  spaced   out  ", "spaced out" },
        { S("bidi", 0x202E, "evil"), "bidi evil" },
        { S("tab", 0x09, "bell", 0x07), "tab bell" },
        { "", "" },
        { null, "" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Text_becomes_one_clean_line(string? input, string expected) =>
        Assert.Equal(expected, DisplayText.Clean(input));

    [Fact]
    public void Text_is_capped()
    {
        var clean = DisplayText.Clean(new string('x', 500), 50);
        Assert.Equal(50, clean.Length);
    }

    [Fact]
    public void Arabic_and_emoji_survive() =>
        Assert.Equal("العربية 🎧", DisplayText.Clean("العربية 🎧"));

    [Fact]
    public void Underscores_are_escaped_for_menus() =>
        Assert.Equal("My__Headphones", DisplayText.ForAccessText("My_Headphones"));

    [Fact]
    public void Sustained_alert_waits_then_cools_down()
    {
        var alert = new SustainedAlert(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(alert.Update(true, t));
        Assert.False(alert.Update(true, t.AddSeconds(29)));
        Assert.True(alert.Update(true, t.AddSeconds(30)));
        Assert.False(alert.Update(true, t.AddMinutes(5)));      // cooling down
        Assert.False(alert.Update(false, t.AddMinutes(6)));     // condition cleared
        Assert.False(alert.Update(true, t.AddMinutes(11)));     // must hold again
        Assert.True(alert.Update(true, t.AddMinutes(11).AddSeconds(30)));
    }

    [Fact]
    public void Shared_names_are_never_resolved_by_name()
    {
        static AudioEndpoint E(string id, string name) =>
            new(id, name, name, EDataFlow.Render, DeviceState.Active, FormFactor.Speakers, Guid.Empty);

        var list = new[] { E("a", "Speakers"), E("b", "SPEAKERS"), E("c", "Monitor") };

        Assert.Null(DeviceRules.UniqueByName(list, "Speakers"));
        Assert.Equal("c", DeviceRules.UniqueByName(list, "monitor")?.Id);
        Assert.True(DeviceRules.NameIsShared(list, list[0]));
        Assert.False(DeviceRules.NameIsShared(list, list[2]));
    }
}
