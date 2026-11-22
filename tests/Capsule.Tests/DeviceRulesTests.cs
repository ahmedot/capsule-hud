using Capsule.Audio;
using Capsule.Core;
using Capsule.Interop;
using Capsule.Settings;

namespace Capsule.Tests;

public class DeviceRulesTests
{
    private static AudioEndpoint Output(string id, string name, FormFactor formFactor = FormFactor.Speakers) =>
        new(id, name, name, EDataFlow.Render, DeviceState.Active, formFactor, Guid.Empty);

    [Theory]
    [InlineData(FormFactor.Headphones, true)]
    [InlineData(FormFactor.Headset, true)]
    [InlineData(FormFactor.Speakers, false)]
    [InlineData(FormFactor.DigitalAudioDisplayDevice, false)]
    [InlineData(FormFactor.Unknown, false)]
    public void Any_mode_follows_the_device_type(FormFactor formFactor, bool expected)
    {
        var settings = new AppSettings { HeadphoneMode = HeadphoneMode.Any };
        Assert.Equal(expected, DeviceRules.IsHeadphones("Headphones (Studio)", formFactor, EDataFlow.Render, settings));
    }

    [Theory]
    [InlineData("Headset (Studio Hands-Free)")]
    [InlineData("Headset (Studio Hands-Free AG Audio)")]
    [InlineData("Headset (Studio Hands Free)")]
    public void Hands_free_call_mode_is_never_used_for_sound(string name)
    {
        var settings = new AppSettings { HeadphoneMode = HeadphoneMode.Selected, SelectedHeadphones = [name] };
        Assert.False(DeviceRules.IsHeadphones(name, FormFactor.Headset, EDataFlow.Render, settings));
        Assert.False(DeviceRules.IsHeadphones(name, FormFactor.Headset, EDataFlow.Render, new AppSettings()));
    }

    [Fact]
    public void Microphones_are_never_headphones()
    {
        Assert.False(DeviceRules.IsHeadphones("Headset Microphone", FormFactor.Headset, EDataFlow.Capture, new AppSettings()));
    }

    [Fact]
    public void Selected_mode_matches_names_ignoring_case()
    {
        var settings = new AppSettings
        {
            HeadphoneMode = HeadphoneMode.Selected,
            SelectedHeadphones = ["Headphones (Studio Headphones)"],
        };

        Assert.True(DeviceRules.IsHeadphones("headphones (studio headphones)", FormFactor.Speakers, EDataFlow.Render, settings));
        Assert.False(DeviceRules.IsHeadphones("Headphones (Other)", FormFactor.Headphones, EDataFlow.Render, settings));
    }

    [Fact]
    public void Fallback_prefers_other_headphones_that_are_still_connected()
    {
        var speakers = Output("s", "Speakers");
        var first = Output("h1", "Headphones A", FormFactor.Headphones);
        var second = Output("h2", "Headphones B", FormFactor.Headphones);

        var target = DeviceRules.ChooseFallback([speakers, first, second], [first, second], new AppSettings(), "s");

        Assert.Equal("h2", target?.Id);
    }

    [Fact]
    public void Fallback_uses_the_chosen_device_when_it_is_available()
    {
        var speakers = Output("s", "Speakers");
        var monitor = Output("m", "Monitor");
        var settings = new AppSettings { FallbackDevice = "MONITOR" };

        Assert.Equal("m", DeviceRules.ChooseFallback([speakers, monitor], [], settings, "s")?.Id);
    }

    [Fact]
    public void Fallback_goes_back_to_the_previous_device_otherwise()
    {
        var speakers = Output("s", "Speakers");
        var settings = new AppSettings { FallbackDevice = "Unplugged Device" };

        Assert.Equal("s", DeviceRules.ChooseFallback([speakers], [], settings, "s")?.Id);
        Assert.Null(DeviceRules.ChooseFallback([speakers], [], new AppSettings(), "gone"));
        Assert.Null(DeviceRules.ChooseFallback([speakers], [], new AppSettings(), null));
    }

    [Theory]
    [InlineData(0f, false, Glyphs.Mute)]
    [InlineData(0.5f, true, Glyphs.Mute)]
    [InlineData(0.2f, false, Glyphs.Volume1)]
    [InlineData(0.5f, false, Glyphs.Volume2)]
    [InlineData(0.9f, false, Glyphs.Volume3)]
    public void Volume_icon_matches_the_level(float level, bool muted, string expected) =>
        Assert.Equal(expected, DeviceRules.VolumeGlyph(level, muted));

    [Theory]
    [InlineData(100, "")]
    [InlineData(0, "")]
    [InlineData(54, "")]
    [InlineData(150, "")]
    [InlineData(-5, "")]
    public void Battery_icon_matches_the_level(int percent, string expected) =>
        Assert.Equal(expected, DeviceRules.BatteryGlyph(percent));
}
