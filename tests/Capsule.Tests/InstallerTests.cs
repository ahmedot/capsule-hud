using System.Windows.Media;
using Capsule.Core;
using Capsule.Settings;

namespace Capsule.Tests;

public class InstallerTests
{
    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", @"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", true)]
    [InlineData(@"C:\USERS\A\APPDATA\LOCAL\PROGRAMS\CAPSULE\CAPSULE.EXE", @"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Capsule\..\Capsule\Capsule.exe", @"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", true)]
    [InlineData(@"C:\Users\a\Downloads\Capsule-Setup.exe", @"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", false)]
    [InlineData(null, @"C:\Users\a\AppData\Local\Programs\Capsule\Capsule.exe", false)]
    [InlineData("", "", false)]
    public void Same_path_ignores_case_and_dots(string? a, string? b, bool expected) =>
        Assert.Equal(expected, Installer.SamePath(a, b));

    [Fact]
    public void Install_folder_is_per_user_and_needs_no_admin()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, Installer.InstallFolder, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"Programs\Capsule", Installer.InstallFolder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Capsule.exe", Path.GetFileName(Installer.InstalledExe));
    }

    [Fact]
    public void Test_runs_are_never_treated_as_a_download_to_install() =>
        Assert.False(Installer.ShouldInstall([]));
}

public class AccentTests
{
    [Theory]
    [InlineData(0x3B, 0x1F, 0x6B)] // deep purple
    [InlineData(0x8E, 0x0E, 0x1E)] // dark red
    [InlineData(0x00, 0x33, 0x66)] // navy
    [InlineData(0x10, 0x10, 0x10)] // near black
    public void Dark_accents_are_brightened_for_the_dark_pill(byte r, byte g, byte b)
    {
        var bright = WindowsAccent.Bright(Color.FromRgb(r, g, b));
        Assert.True(WindowsAccent.Luminance(bright) >= WindowsAccent.MinBrightness - 0.005);
        Assert.True(bright.R >= r && bright.G >= g && bright.B >= b); // only lighter, never a different colour
    }

    [Fact]
    public void Bright_accents_are_left_alone()
    {
        var mint = Color.FromRgb(0x4B, 0xE3, 0xA2);
        Assert.Equal(mint, WindowsAccent.Bright(mint));
    }

    [Fact]
    public void New_settings_follow_the_windows_accent() =>
        Assert.Equal(AppSettings.WindowsAccent, new AppSettings().HudAccent);

    [Theory]
    [InlineData("windows")]
    [InlineData("Windows")]
    [InlineData("WINDOWS")]
    public void Windows_accent_survives_cleanup_in_any_case(string value) =>
        Assert.Equal(AppSettings.WindowsAccent, new AppSettings { HudAccent = value }.Normalize().HudAccent);

    [Fact]
    public void Picked_colours_are_kept()
    {
        foreach (var hex in AppSettings.AccentPresets)
        {
            Assert.Equal(hex, new AppSettings { HudAccent = hex }.Normalize().HudAccent);
        }
    }
}
