using Capsule.Core;
using Capsule.Interop;

namespace Capsule.Tests;

public class HardeningTests
{
    [Theory]
    [InlineData(1, false)] // Default: UAC off or the built-in Administrator, where every program runs this way
    [InlineData(2, true)]  // Full: the person chose "Run as administrator"
    [InlineData(3, false)] // Limited: a normal start
    public void Only_a_chosen_administrator_start_is_refused(int elevationType, bool refused) =>
        Assert.Equal(refused, Installer.IsRaisedByChoice(elevationType));

    [Fact]
    public void Keyboard_app_must_live_under_program_files()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(DuplicatePopupHider.IsInstalledLocation(Path.Combine(programFiles, "Vendor", "app.exe")));
        Assert.False(DuplicatePopupHider.IsInstalledLocation(@"C:\ProgramData\Vendor\app.exe"));
        Assert.False(DuplicatePopupHider.IsInstalledLocation(Path.Combine(Path.GetTempPath(), "app.exe")));
        Assert.False(DuplicatePopupHider.IsInstalledLocation(programFiles + "Evil\app.exe")); // "Program FilesEvil"
        Assert.False(DuplicatePopupHider.IsInstalledLocation(Path.Combine(programFiles, "..", "Users", "app.exe")));
    }

    [Theory]
    [InlineData("user32.dll")]
    [InlineData("KERNEL32.DLL")]
    [InlineData("shcore")]
    [InlineData("ntdll.dll")]
    public void System_libraries_load_from_system32(string name)
    {
        var handle = SystemLibraries.Resolve(name, typeof(SystemLibraries).Assembly, null);
        Assert.NotEqual(IntPtr.Zero, handle);
    }

    [Theory]
    [InlineData("evil.dll")]
    [InlineData(@"..\user32.dll")]
    [InlineData(@"C:\temp\user32.dll")]
    public void Anything_else_is_refused(string name) =>
        Assert.Throws<DllNotFoundException>(() => SystemLibraries.Resolve(name, typeof(SystemLibraries).Assembly, null));
}
