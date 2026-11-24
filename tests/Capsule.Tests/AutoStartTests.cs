using Capsule.Core;

namespace Capsule.Tests;

public class AutoStartTests
{
    [Theory]
    [InlineData(null, false)]                       // no flag: Windows treats the entry as on
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, false)]  // on
    [InlineData(new byte[] { 6, 0, 0, 0 }, false)]  // on
    [InlineData(new byte[] { 3, 0, 0, 0 }, true)]   // switched off in Task Manager or Settings
    [InlineData(new byte[] { 7, 0, 0, 0 }, true)]   // switched off
    public void Windows_startup_switch_is_read_from_its_first_byte(byte[]? flag, bool off) =>
        Assert.Equal(off, AutoStart.IsSwitchedOff(flag));
}
