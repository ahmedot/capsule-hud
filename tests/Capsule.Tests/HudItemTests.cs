using Capsule.Core;
using Capsule.Hud;

namespace Capsule.Tests;

public class HudItemTests
{
    [Fact]
    public void Device_pill_shows_battery_only_when_connected()
    {
        var on = HudItems.Device(Glyphs.Headphones, "Studio", connected: true, battery: 80);
        var off = HudItems.Device(Glyphs.Headphones, "Studio", connected: false, battery: 80);

        Assert.Equal("Connected", on.Status);
        Assert.Equal("80%", on.BatteryText);
        Assert.Equal("Disconnected", off.Status);
        Assert.True(off.IsNeutral); // disconnecting is normal, not an error
        Assert.False(off.HasBattery);
    }

    [Fact]
    public void Off_states_are_grey_and_only_warnings_are_red()
    {
        Assert.True(HudItems.Microphone("Mic", muted: true).IsNeutral);
        Assert.True(HudItems.Volume("Speakers", 0.5f, muted: true).IsNeutral);
        Assert.False(HudItems.Volume("Speakers", 0.5f, muted: false).IsNeutral);
        Assert.True(HudItems.LockKey("Caps Lock", on: false).IsNeutral);
        Assert.True(HudItems.Info(Glyphs.Warning, "GPU 90 °C", "Running hot", HudTone.Bad).IsBad);
    }

    [Theory]
    [InlineData(0.64f, false, "64%")]
    [InlineData(0.0f, false, "0%")]
    [InlineData(0.5f, true, "Muted")]
    public void Volume_pill_text(float level, bool muted, string text) =>
        Assert.Equal(text, HudItems.Volume("Speakers", level, muted).LevelText);

    [Theory]
    [InlineData(70, "70%", 0.7)]
    [InlineData(-10, "0%", 0.0)]
    [InlineData(150, "100%", 1.0)]
    public void Brightness_pill_is_clamped(int percent, string text, double scale)
    {
        var item = HudItems.Brightness(percent);
        Assert.Equal(text, item.LevelText);
        Assert.Equal(scale, item.LevelScale, 3);
    }

    [Fact]
    public void Lock_key_off_is_neutral_not_an_error()
    {
        var item = HudItems.LockKey("Caps Lock", on: false);
        Assert.Equal("Off", item.Status);
        Assert.True(item.IsNeutral);
        Assert.False(item.IsBad);
    }

    [Theory]
    [InlineData("Artist", true, "Playing · Artist")]
    [InlineData("  ", false, "Paused")]
    [InlineData("", true, "Playing")]
    public void Media_status_line(string artist, bool playing, string expected) =>
        Assert.Equal(expected, HudItems.MediaStatus(artist, playing));

    [Fact]
    public void Live_items_update_in_place()
    {
        var first = HudItems.Volume("Speakers", 0.2f, false);
        var second = HudItems.Volume("Speakers", 0.9f, false);
        var changed = new List<string?>();
        first.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        first.CopyFrom(second);

        Assert.Equal(first.ReplaceKey, second.ReplaceKey);
        Assert.Equal("90%", first.LevelText);
        Assert.Contains(nameof(HudItem.LevelScale), changed);
    }
}
