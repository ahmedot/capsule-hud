using Capsule.Core;

namespace Capsule.Tests;

public class HealthTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0);

    private static AverageAlert NewAlert() => new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(20));

    [Fact]
    public void A_busy_cpu_that_dips_now_and_then_still_warns_after_30_seconds()
    {
        var alert = NewAlert();
        double[] readings = [95, 88, 97, 99, 86, 96, 98]; // every 5 s; an "above 90 every time" rule would never fire
        double? fired = null;
        for (var i = 0; i < readings.Length; i++) fired ??= alert.Update(readings[i], 90, Start.AddSeconds(5 * i));

        Assert.NotNull(fired);
        Assert.InRange(fired!.Value, 90, 100);
    }

    [Fact]
    public void Nothing_shows_before_30_seconds()
    {
        var alert = NewAlert();
        for (var i = 0; i < 6; i++) Assert.Null(alert.Update(100, 90, Start.AddSeconds(5 * i))); // 0 to 25 s
    }

    [Fact]
    public void A_burst_that_already_ended_does_not_warn()
    {
        var alert = NewAlert();
        double[] readings = [100, 100, 100, 100, 100, 100, 40]; // average still high, but it's quiet now
        double? fired = null;
        for (var i = 0; i < readings.Length; i++) fired = alert.Update(readings[i], 90, Start.AddSeconds(5 * i));

        Assert.Null(fired);
    }

    [Fact]
    public void After_a_warning_the_same_warning_waits_for_the_cool_down()
    {
        var alert = NewAlert();
        var fires = 0;
        for (var i = 0; i <= 120; i++) // ten minutes of 100%
        {
            if (alert.Update(100, 90, Start.AddSeconds(5 * i)) is not null) fires++;
        }

        Assert.Equal(1, fires);
    }

    [Fact]
    public void A_long_gap_starts_over()
    {
        var alert = NewAlert();
        Assert.Null(alert.Update(100, 90, Start));
        // Switched off (or asleep) for a minute: one new reading is not 30 s of being busy.
        Assert.Null(alert.Update(100, 90, Start.AddSeconds(60)));
    }

    [Fact]
    public void An_app_with_many_processes_is_counted_as_one()
    {
        var top = Busiest.App(
        [
            ("chrome.exe", 10), ("chrome.exe", 12), ("CHROME.EXE", 9), ("chrome.exe", 11),
            ("game.exe", 30),
        ]);

        Assert.Equal("chrome.exe", top!.Value.Name, ignoreCase: true);
        Assert.Equal(42.0 / 72.0, top.Value.Share, 3);
    }

    [Fact]
    public void No_app_is_blamed_when_none_stands_out()
    {
        var top = Busiest.App([("a.exe", 10), ("b.exe", 10), ("c.exe", 10), ("d.exe", 10)]);

        Assert.Equal("Several apps are busy", Busiest.Describe(top, "Several apps are busy"));
        Assert.Equal("game.exe", Busiest.Describe(("game.exe", 0.8), "Several apps are busy"));
        Assert.Null(Busiest.App([("a.exe", 0)]));
    }

    [Fact]
    public void An_app_that_just_closed_still_counts_in_the_total()
    {
        // The busy game exited before its name was read: the desktop window manager must not get all the blame.
        var top = Busiest.App([("", 90), ("dwm.exe", 10)]);

        Assert.Equal(0.1, top!.Value.Share, 3);
        Assert.Equal("Several apps are busy", Busiest.Describe(top, "Several apps are busy"));
        Assert.Null(Busiest.App([("", 50)]));
    }

    [Fact]
    public void Two_graphics_chips_are_never_added_together()
    {
        // A laptop: the built-in GPU at 60% and the gaming GPU at 70%. The old code said "100%".
        var gpu = GpuEngines.Busiest(
        [
            ("pid_100_luid_0x00000000_0x0000A001_phys_0_eng_0_engtype_3D", 60),
            ("pid_200_luid_0x00000000_0x0000B002_phys_0_eng_0_engtype_3D", 70),
        ]);

        Assert.Equal(70, gpu!.Value.Percent);
        Assert.Equal(200, Assert.Single(gpu.Value.Users).Pid);
    }

    [Fact]
    public void Apps_sharing_one_engine_add_up_and_video_or_copy_work_is_ignored()
    {
        var gpu = GpuEngines.Busiest(
        [
            ("pid_100_luid_0x00000000_0x0000A001_phys_0_eng_0_engtype_3D", 50),
            ("pid_101_luid_0x00000000_0x0000A001_phys_0_eng_0_engtype_3D", 30),
            ("pid_102_luid_0x00000000_0x0000A001_phys_0_eng_3_engtype_VideoDecode", 99),
            ("pid_103_luid_0x00000000_0x0000A001_phys_0_eng_9_engtype_copy", 99),
            ("not a gpu instance", 100),
        ]);

        Assert.Equal(80, gpu!.Value.Percent);
        Assert.Equal(2, gpu.Value.Users.Count);
    }

    [Fact]
    public void Compute_work_counts_as_the_gpu_working()
    {
        var gpu = GpuEngines.Busiest([("pid_7_luid_0x00000000_0x0000A001_phys_0_eng_4_engtype_Compute_0", 97)]);

        Assert.Equal(97, gpu!.Value.Percent);
    }

    [Fact]
    public void The_process_list_includes_this_process_with_its_name_and_memory()
    {
        var list = ProcessList.Snapshot();
        var self = Assert.Single(list, e => e.Pid == Environment.ProcessId);

        Assert.False(string.IsNullOrEmpty(self.Name));
        Assert.True(self.WorkingSet > 0);
        Assert.True(self.CpuTime > 0);
        Assert.Contains(list, e => e.Pid == 4); // the System process, which can't be opened but is listed
    }
}
