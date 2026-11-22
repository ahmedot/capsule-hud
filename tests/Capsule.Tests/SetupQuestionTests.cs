using Capsule.Core;
using Capsule.Settings;
using Capsule.UI;
using Xunit.Abstractions;

namespace Capsule.Tests;

public class SetupQuestionTests(ITestOutputHelper output)
{
    private static readonly PcProfile Plain = new(null, null, ["English"], null, false);
    private static readonly PcProfile Everything = new("Studio Headphones", "Steam", ["English", "العربية"], "Keyboard App", true);

    [Fact]
    public void A_plain_desktop_gets_only_the_two_basic_questions()
    {
        var questions = SetupQuestions.For(Plain);
        Assert.Equal(2, questions.Count);
        Assert.Equal("When you connect headphones…", questions[1].Title);
    }

    [Fact]
    public void Questions_follow_what_the_pc_has()
    {
        var titles = SetupQuestions.For(Everything).Select(q => q.Title).ToList();
        Assert.Equal(6, titles.Count);
        Assert.Contains("We found your Studio Headphones", titles);
        Assert.Contains("Do you play games on this PC?", titles);
        Assert.Contains("You type in English and العربية", titles);
        Assert.Contains("Keyboard App also shows Caps Lock pop-ups", titles);
        Assert.Contains("This looks like a laptop", titles);
    }

    [Fact]
    public void Every_question_has_exactly_one_recommended_answer()
    {
        foreach (var q in SetupQuestions.For(Everything))
        {
            Assert.Single(q.Choices, c => c.Recommended);
        }
    }

    [Fact]
    public void Skipping_keeps_the_normal_defaults_apart_from_quieter_gaming_warnings()
    {
        var skipped = new AppSettings();
        foreach (var q in SetupQuestions.For(Everything)) q.Recommended.Apply(skipped);
        var defaults = new AppSettings();

        Assert.Equal(defaults.HudPosition, skipped.HudPosition);
        Assert.Equal(defaults.AutoSwitch, skipped.AutoSwitch);
        Assert.Equal(defaults.SwitchCommunications, skipped.SwitchCommunications);
        Assert.Equal(defaults.HudKeyboard, skipped.HudKeyboard);
        Assert.Equal(defaults.HideDuplicatePopups, skipped.HideDuplicatePopups);
        Assert.Equal(defaults.HudPower, skipped.HudPower);
        Assert.False(skipped.HudCpuGpu); // gamers: no "busy" warnings while games run
    }

    [Fact]
    public void Language_lists_read_naturally()
    {
        var three = Plain with { Languages = ["English", "Français", "العربية"] };
        Assert.Contains(SetupQuestions.For(three), q => q.Title == "You type in English, Français and العربية");
        var many = Plain with { Languages = ["a", "b", "c", "d"] };
        Assert.Contains(SetupQuestions.For(many), q => q.Title == "You type in 4 languages");
    }

    [Fact]
    public void The_pc_scan_never_throws()
    {
        var profile = PcScan.Run();
        Assert.NotNull(profile.Languages);
        output.WriteLine($"scan: headphones={profile.Headphones ?? "-"}, gaming={profile.GamingHint ?? "-"}, " +
                         $"languages={string.Join("/", profile.Languages)}, keyboardApp={profile.KeyboardApp ?? "-"}, laptop={profile.IsLaptop}");
    }
}
