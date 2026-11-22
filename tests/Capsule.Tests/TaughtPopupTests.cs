using Capsule.Settings;

namespace Capsule.Tests;

public class TaughtPopupTests
{
    [Fact]
    public void Taught_apps_are_cleaned_and_limited()
    {
        var settings = new AppSettings
        {
            TaughtPopups =
            [
                new() { App = " Keyboard Utility ", ExePath = @"C:\Program Files\Vendor\kb.exe", WindowClass = "Osd" },
                new() { App = "Same again", ExePath = @"c:\program files\vendor\KB.EXE", WindowClass = "Osd" },
                new() { App = "Relative", ExePath = "kb.exe", WindowClass = "Osd" },
                new() { App = "No class", ExePath = @"C:\Tools\x.exe", WindowClass = "" },
                new() { App = "Not an exe", ExePath = @"C:\Tools\x.dll", WindowClass = "Osd" },
            ],
        }.Normalize();

        var only = Assert.Single(settings.TaughtPopups);
        Assert.Equal("Keyboard Utility", only.App);
    }

    [Fact]
    public void A_taught_app_matches_only_its_own_program_and_window_type()
    {
        var taught = new TaughtPopup { App = "Keyboard Utility", ExePath = @"C:\Program Files\Vendor\kb.exe", WindowClass = "Osd" };

        Assert.True(taught.Matches(@"C:\PROGRAM FILES\VENDOR\KB.EXE", "Osd"));
        Assert.False(taught.Matches(@"C:\Program Files\Vendor\kb.exe", "osd"));       // window class must match exactly
        Assert.False(taught.Matches(@"C:\Users\me\Downloads\kb.exe", "Osd"));         // a look-alike elsewhere never matches
    }

    [Fact]
    public void A_part_of_windows_is_never_kept_as_a_taught_app()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var settings = new AppSettings
        {
            TaughtPopups = [new() { App = "Explorer", ExePath = explorer, WindowClass = "Osd" }],
        }.Normalize();

        Assert.Empty(settings.TaughtPopups);
    }

    [Fact]
    public void At_most_sixteen_apps_are_kept()
    {
        var settings = new AppSettings
        {
            TaughtPopups = Enumerable.Range(0, 30)
                .Select(i => new TaughtPopup { App = $"App {i}", ExePath = $@"C:\Apps\{i}.exe", WindowClass = "Osd" })
                .ToList(),
        }.Normalize();

        Assert.Equal(16, settings.TaughtPopups.Count);
    }
}
