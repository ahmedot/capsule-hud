using Capsule.Core;
using Capsule.Hud;

namespace Capsule.Tests;

public class YouTubeTests
{
    [Theory]
    [InlineData("Lofi beats to study to - YouTube - Google Chrome")]
    [InlineData("Lofi beats to study to - YouTube and 2 more pages - Personal - Microsoft Edge")]
    [InlineData("Lofi beats to study to - YouTube — Mozilla Firefox")]
    [InlineData("(3) Lofi beats to study to - YouTube - Google Chrome")]
    [InlineData("(99+) Lofi beats to study to - YouTube - Brave")]
    [InlineData("Lofi beats to study to - YouTube")]
    public void A_browser_window_on_the_video_is_recognized(string window) =>
        Assert.Equal(YouTubeTab.ThisTitle, YouTube.Classify(window, "Lofi beats to study to"));

    [Theory]
    [InlineData("(1) Fastest laps - YouTube - Google Chrome")]
    [InlineData("(4) (1) Fastest laps - YouTube - Google Chrome")]
    public void A_video_title_that_starts_with_a_number_in_brackets_still_matches(string window) =>
        Assert.Equal(YouTubeTab.ThisTitle, YouTube.Classify(window, "(1) Fastest laps"));

    [Fact]
    public void A_video_title_that_itself_mentions_YouTube_still_matches() =>
        Assert.Equal(YouTubeTab.ThisTitle,
            YouTube.Classify("Best of - YouTube Rewind - YouTube - Google Chrome", "Best of - YouTube Rewind"));

    [Theory]
    [InlineData("Night Drive - YouTube Music - Google Chrome")]
    [InlineData("Night Drive - YouTube Music")]
    [InlineData("YouTube Music - Google Chrome")]
    public void YouTube_Music_keeps_the_normal_music_pill(string window) =>
        Assert.Equal(YouTubeTab.None, YouTube.Classify(window, "Night Drive"));

    [Fact]
    public void Another_video_in_front_is_reported_as_another_title() =>
        Assert.Equal(YouTubeTab.OtherTitle, YouTube.Classify("Previous video - YouTube - Google Chrome", "Next video"));

    [Theory]
    [InlineData("Night Drive - Google Chrome")]
    [InlineData("YouTube - Google Chrome")]
    [InlineData("Night Drive - YouTuber diaries - Notepad")]
    [InlineData("")]
    public void Other_windows_are_not_YouTube(string window) =>
        Assert.Equal(YouTubeTab.None, YouTube.Classify(window, "Night Drive"));

    [Fact]
    public void Only_the_start_of_the_window_title_counts_as_this_video() =>
        Assert.NotEqual(YouTubeTab.ThisTitle, YouTube.Classify("Notes on Night Drive - YouTube - Google Chrome", "Night Drive"));

    [Fact]
    public void An_empty_media_title_never_matches() =>
        Assert.NotEqual(YouTubeTab.ThisTitle, YouTube.Classify(" - YouTube - Google Chrome", ""));

    [Theory]
    [InlineData(true, "Playing", HudTone.Good)]
    [InlineData(false, "Paused", HudTone.Neutral)]
    public void The_YouTube_pill_shows_no_video_title(bool playing, string status, HudTone tone)
    {
        var item = HudItems.YouTube(playing);

        Assert.Equal("YouTube", item.Title);
        Assert.Equal(status, item.Status);
        Assert.Equal(tone, item.Tone);
        Assert.Null(item.Art);
        Assert.Equal("media", item.ReplaceKey); // replaces a music pill on screen instead of queueing
    }
}
