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
                new() { App = " Keyboard Utility ", ExePath = @"C:\Program Files\Vendor\kb.exe", WindowClass = "Osd", Width = 280, Height = 72 },
                new() { App = "Same again", ExePath = @"c:\program files\vendor\KB.EXE", WindowClass = "Osd", Width = 280, Height = 72 },
                new() { App = "Relative", ExePath = "kb.exe", WindowClass = "Osd", Width = 280, Height = 72 },
                new() { App = "No class", ExePath = @"C:\Tools\x.exe", WindowClass = "", Width = 280, Height = 72 },
                new() { App = "Not an exe", ExePath = @"C:\Tools\x.dll", WindowClass = "Osd", Width = 280, Height = 72 },
                new() { App = "No size", ExePath = @"C:\Tools\y.exe", WindowClass = "Osd" },
                new() { App = "Too big", ExePath = @"C:\Tools\z.exe", WindowClass = "Osd", Width = 1280, Height = 72 },
            ],
        }.Normalize();

        var only = Assert.Single(settings.TaughtPopups);
        Assert.Equal("Keyboard Utility", only.App);
        Assert.Equal((280, 72), (only.Width, only.Height));
    }

    [Fact]
    public void A_taught_app_matches_only_windows_about_its_own_size()
    {
        var taught = new TaughtPopup { App = "Keyboard Utility", ExePath = @"C:\Program Files\Vendor\kb.exe", WindowClass = "Osd", Width = 280, Height = 72 };

        Assert.True(taught.FitsSize(280, 72));
        Assert.True(taught.FitsSize(320, 72));    // longer text makes the pop-up a little wider
        Assert.False(taught.FitsSize(480, 270));  // a picture-in-picture window of the same app
        Assert.False(taught.FitsSize(364, 110));  // a notification of the same app
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
            TaughtPopups = [new() { App = "Explorer", ExePath = explorer, WindowClass = "Osd", Width = 280, Height = 72 }],
        }.Normalize();

        Assert.Empty(settings.TaughtPopups);
    }

    [Fact]
    public void The_standard_dialog_class_is_never_kept()
    {
        // Many apps use it for their own small dialogs, so a rule for it would hide more than the one pop-up.
        var settings = new AppSettings
        {
            TaughtPopups = [new() { App = "Some App", ExePath = @"C:\Program Files\Vendor\app.exe", WindowClass = "#32770", Width = 280, Height = 72 }],
        }.Normalize();

        Assert.Empty(settings.TaughtPopups);
    }

    [Fact]
    public void At_most_sixteen_apps_are_kept()
    {
        var settings = new AppSettings
        {
            TaughtPopups = Enumerable.Range(0, 30)
                .Select(i => new TaughtPopup { App = $"App {i}", ExePath = $@"C:\Apps\{i}.exe", WindowClass = "Osd", Width = 280, Height = 72 })
                .ToList(),
        }.Normalize();

        Assert.Equal(16, settings.TaughtPopups.Count);
    }
}
