using Capsule.Core;
using Capsule.Settings;

namespace Capsule.Tests;

public class KeyboardReturnTests
{
    private const string English = "en"; // as KeyboardReturn.LanguageKey gives them
    private const string Arabic = "ar";

    [Fact]
    public void Switches_back_after_the_chosen_time_without_input() =>
        Assert.True(KeyboardReturn.ShouldSwitch(TimeSpan.FromMinutes(2), 2, Arabic, English, alreadyHandled: false));

    [Fact]
    public void Waits_while_you_are_still_using_the_PC() =>
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromSeconds(90), 2, Arabic, English, alreadyHandled: false));

    [Fact]
    public void Leaves_the_usual_language_alone() =>
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromHours(1), 2, English, English, alreadyHandled: false));

    [Fact]
    public void Asks_only_once_per_break() =>
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromHours(1), 2, Arabic, English, alreadyHandled: true));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Never_when_switched_off(int minutes) =>
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromHours(1), minutes, Arabic, English, alreadyHandled: false));

    [Fact]
    public void Unknown_languages_do_nothing()
    {
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromHours(1), 2, "", English, alreadyHandled: false));
        Assert.False(KeyboardReturn.ShouldSwitch(TimeSpan.FromHours(1), 2, Arabic, "", alreadyHandled: false));
    }

    [Fact]
    public void Two_layouts_of_one_language_count_as_one()
    {
        Assert.Equal(English, KeyboardReturn.LanguageKey(0x04090409)); // English (United States)
        Assert.Equal(English, KeyboardReturn.LanguageKey(0x08090809)); // English (United Kingdom)
        Assert.Equal(English, KeyboardReturn.LanguageKey(unchecked((IntPtr)0xF0020409))); // a layout with its own id part
        Assert.Equal(Arabic, KeyboardReturn.LanguageKey(0x04010401));
    }

    [Theory]
    [InlineData(0x081A, 0x0C1A)] // Serbian (Latin) and Serbian (Cyrillic)
    [InlineData(0x041A, 0x0C1A)] // Croatian and Serbian (Cyrillic)
    [InlineData(0x0404, 0x0804)] // Chinese (Traditional) and Chinese (Simplified)
    [InlineData(0x0414, 0x0814)] // Norwegian Bokmål and Nynorsk
    public void Languages_that_share_a_family_stay_apart(int first, int second) =>
        Assert.NotEqual(KeyboardReturn.LanguageKey(first), KeyboardReturn.LanguageKey(second));

    [Theory]
    [InlineData(0x2000)] // languages Windows has no fixed id for (added by tag, or by a keyboard tool)
    [InlineData(0x2400)]
    [InlineData(0x1000)] // a custom locale
    public void Languages_without_a_fixed_id_never_throw(int layout)
    {
        Assert.Equal("your usual language", KeyboardReturn.LanguageName(layout));
        Assert.NotEqual("", KeyboardReturn.LanguageKey(layout)); // still a key of its own
        Assert.NotEqual(KeyboardReturn.LanguageKey(0x2000), KeyboardReturn.LanguageKey(0x2400));
    }

    [Fact]
    public void An_empty_layout_is_unknown()
    {
        Assert.Equal("", KeyboardReturn.LanguageKey(0));
        Assert.Equal("your usual language", KeyboardReturn.LanguageName(0));
    }

    [Theory]
    [InlineData(0x04090409, "English")]
    [InlineData(0x08090809, "English")]
    [InlineData(0x04010401, "Arabic")]
    [InlineData(0x0C1A0C1A, "Serbian (Cyrillic)")]
    public void Languages_are_named_in_plain_English(int layout, string name) =>
        Assert.Equal(name, KeyboardReturn.LanguageName(layout));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(15, 10)]
    [InlineData(30, 30)]
    [InlineData(10_000, 30)]
    public void A_hand_edited_setting_becomes_one_of_the_offered_choices(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { KeyboardBackMinutes = stored }.Normalize().KeyboardBackMinutes);

    [Fact]
    public void Off_by_default() => Assert.Equal(0, new AppSettings().KeyboardBackMinutes);
}
