using Capsule.Settings;

namespace Capsule.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "capsule-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = Path.Combine(_folder, "settings.json");
        var original = new AppSettings
        {
            AutoSwitch = false,
            HeadphoneMode = HeadphoneMode.Selected,
            SelectedHeadphones = ["Headphones (Studio)"],
            HudPosition = HudPosition.TopRight,
            HudAccent = "#8B7CFF",
            HudSeconds = 3.5,
        };

        SettingsStore.Save(original, path);
        var loaded = SettingsStore.Load(path);

        Assert.False(loaded.AutoSwitch);
        Assert.Equal(HeadphoneMode.Selected, loaded.HeadphoneMode);
        Assert.Equal(["Headphones (Studio)"], loaded.SelectedHeadphones);
        Assert.Equal(HudPosition.TopRight, loaded.HudPosition);
        Assert.Equal("#8B7CFF", loaded.HudAccent);
        Assert.Equal(3.5, loaded.HudSeconds);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void File_with_byte_order_mark_loads()
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "{ \"HudPosition\": \"TopRight\" }", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(HudPosition.TopRight, SettingsStore.Load(path).HudPosition);
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Missing_file_gives_defaults() =>
        Assert.True(SettingsStore.Load(Path.Combine(_folder, "nope.json")).AutoSwitch);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"HudPosition\": \"Sideways\" }")]
    [InlineData("{ \"HudSeconds\": \"many\" }")]
    [InlineData("[1, 2, 3]")]
    public void Damaged_file_gives_defaults_and_keeps_a_copy(string content)
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, content);

        var loaded = SettingsStore.Load(path);

        Assert.True(loaded.AutoSwitch);
        Assert.Equal(HudPosition.BottomCenter, loaded.HudPosition);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Oversized_file_is_ignored()
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "{\"FallbackDevice\":\"" + new string('a', 300_000) + "\"}");

        Assert.Equal("", SettingsStore.Load(path).FallbackDevice);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#34D17A; background:url(x)")]
    [InlineData("")]
    public void Bad_accent_colors_are_replaced(string accent) =>
        Assert.Equal(AppSettings.DefaultAccent, new AppSettings { HudAccent = accent }.Normalize().HudAccent);

    [Theory]
    [InlineData(0.1, 1.0)]
    [InlineData(99, 6.0)]
    [InlineData(double.NaN, 2.0)]
    [InlineData(double.PositiveInfinity, 2.0)]
    [InlineData(2.5, 2.5)]
    public void Hud_duration_is_kept_in_range(double input, double expected) =>
        Assert.Equal(expected, new AppSettings { HudSeconds = input }.Normalize().HudSeconds);

    [Fact]
    public void Names_are_trimmed_deduplicated_and_capped()
    {
        var settings = new AppSettings
        {
            SelectedHeadphones = ["  A  ", "a", "", "B", new string('x', 1000)],
            FallbackDevice = "   ",
        }.Normalize();

        Assert.Equal(3, settings.SelectedHeadphones.Count);
        Assert.Equal("A", settings.SelectedHeadphones[0]);
        Assert.Equal(256, settings.SelectedHeadphones[2].Length);
        Assert.Equal("", settings.FallbackDevice);
    }

    [Fact]
    public void Specific_mic_without_a_device_falls_back_to_keep_current() =>
        Assert.Equal(MicMode.KeepCurrent, new AppSettings { MicMode = MicMode.Specific, MicDevice = "" }.Normalize().MicMode);

    [Fact]
    public void Renamed_devices_show_the_custom_name()
    {
        var settings = new AppSettings();
        settings.Rename("WH Studio 5", "  My headphones  ");

        Assert.Equal("My headphones", settings.NameFor("wh studio 5"));
        Assert.Equal("Desk Speakers", settings.NameFor("Desk Speakers"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WH Studio 5")]
    public void Empty_or_unchanged_name_removes_the_rename(string custom)
    {
        var settings = new AppSettings();
        settings.Rename("WH Studio 5", "Mine");
        settings.Rename("WH Studio 5", custom);

        Assert.Empty(settings.DeviceNames);
        Assert.Equal("WH Studio 5", settings.NameFor("WH Studio 5"));
    }

    [Fact]
    public void Custom_names_are_cleaned_and_capped()
    {
        var settings = new AppSettings();
        settings.Rename("Pad", "Line\r\nbreak\u0007" + new string('z', 200));

        var name = settings.NameFor("Pad");
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain('\u0007', name);
        Assert.True(name.Length <= AppSettings.MaxCustomNameLength);
    }

    [Fact]
    public void Device_names_survive_a_round_trip_and_ignore_case()
    {
        var path = Path.Combine(_folder, "settings.json");
        var original = new AppSettings();
        original.Rename("Desk Speakers", "Room");
        SettingsStore.Save(original, path);

        var loaded = SettingsStore.Load(path);

        Assert.Equal("Room", loaded.NameFor("DESK SPEAKERS"));
    }

    [Fact]
    public void Hand_edited_device_names_are_cleaned_on_load()
    {
        var loaded = SettingsStore.Parse("{ \"DeviceNames\": { \"A\": \"\", \"\": \"x\", \"B\": \"  Bee  \" } }");

        Assert.Single(loaded.DeviceNames);
        Assert.Equal("Bee", loaded.NameFor("b"));
    }

    [Fact]
    public void Unknown_enum_numbers_are_reset()
    {
        var settings = new AppSettings { HudPosition = (HudPosition)42, HudTheme = (HudTheme)(-1) }.Normalize();
        Assert.Equal(HudPosition.BottomCenter, settings.HudPosition);
        Assert.Equal(HudTheme.Dark, settings.HudTheme);
    }
}
