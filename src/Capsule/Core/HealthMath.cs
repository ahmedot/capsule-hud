using System.Text.RegularExpressions;

namespace Capsule.Core;

/// <summary>
/// Fires when a reading stays high on average over a time window. Real work bounces around the limit (95, 88, 97...),
/// so one dip must not reset the clock the way an "above the limit at every check" rule would.
/// </summary>
internal sealed class AverageAlert
{
    private readonly TimeSpan _holdFor;
    private readonly TimeSpan _coolDown;
    private readonly TimeSpan _maxGap;
    private readonly List<(DateTime Time, double Value)> _samples = [];
    private DateTime _lastFired = DateTime.MinValue;

    /// <param name="holdFor">How long the average must stay at or above the limit.</param>
    /// <param name="coolDown">Quiet time after an alert before the same alert can show again.</param>
    /// <param name="maxGap">A longer gap between readings (alert switched off, PC asleep) starts over.</param>
    public AverageAlert(TimeSpan holdFor, TimeSpan coolDown, TimeSpan maxGap)
    {
        _holdFor = holdFor;
        _coolDown = coolDown;
        _maxGap = maxGap;
    }

    /// <summary>Adds a reading. Returns the window's average at the moment the alert should show, otherwise null.</summary>
    public double? Update(double value, double limit, DateTime now)
    {
        if (_samples.Count > 0 && now - _samples[^1].Time > _maxGap) _samples.Clear();
        _samples.Add((now, value));

        // Keep the newest reading taken at or before the start of the window, and everything after it.
        while (_samples.Count > 1 && _samples[1].Time <= now - _holdFor) _samples.RemoveAt(0);
        if (now - _samples[0].Time < _holdFor) return null; // not watched for long enough yet

        var average = _samples.Average(s => s.Value);
        // It must still be high now: an average held up by a burst that already ended doesn't count.
        if (average < limit || value < limit - 10 || now - _lastFired < _coolDown) return null;

        _lastFired = now;
        return average;
    }
}

/// <summary>Who is using the most of something, counting an app's processes together (a browser runs dozens).</summary>
internal static class Busiest
{
    /// <summary>Below this share of the total, no single app is to blame.</summary>
    public const double MinShare = 0.3;

    /// <summary>The app with the largest total and its share of everything measured; null when nothing was used.</summary>
    public static (string Name, double Share)? App(IEnumerable<(string Name, double Amount)> usage)
    {
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double total = 0;
        foreach (var (name, amount) in usage)
        {
            if (amount <= 0) continue;
            // An app without a name (it closed a moment ago) still counts in the total, so whatever is left can't
            // look like it's doing all the work.
            total += amount;
            if (!string.IsNullOrEmpty(name)) byApp[name] = byApp.GetValueOrDefault(name) + amount;
        }

        if (total <= 0 || byApp.Count == 0) return null;
        var top = byApp.MaxBy(p => p.Value);
        return (top.Key, top.Value / total);
    }

    /// <summary>The app to name in an alert, or the fallback when no app clearly stands out.</summary>
    public static string Describe((string Name, double Share)? top, string fallback) =>
        top is { } t && t.Share >= MinShare ? DisplayText.Clean(t.Name, 40) : fallback;
}

/// <summary>Reads the "GPU Engine" counters the way Task Manager does.</summary>
internal static partial class GpuEngines
{
    // Engines that don't mean "the GPU is working hard": copying memory, decoding a video, and so on.
    private static readonly string[] Skipped = ["Copy", "Video", "Security", "Crypto", "LegacyOverlay", "Overlay"];

    /// <summary>
    /// Per graphics chip, every app's use of each engine is added up; the busiest engine is how busy that chip is,
    /// and the busiest chip is the answer. Chips are never added together, so a laptop's two GPUs can't add up to
    /// more than either one. Returns the percentage and each process's share of that engine.
    /// </summary>
    public static (double Percent, List<(int Pid, double Value)> Users)? Busiest(IEnumerable<(string Instance, double Value)> samples)
    {
        var engines = new Dictionary<string, List<(int Pid, double Value)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
        {
            var match = InstancePattern().Match(instance);
            if (!match.Success || value <= 0 || !int.TryParse(match.Groups["pid"].ValueSpan, out var pid)) continue;
            var type = match.Groups["type"].Value;
            if (Skipped.Any(s => type.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

            var engine = $"{match.Groups["luid"].Value}/{match.Groups["phys"].Value}/{match.Groups["eng"].Value}";
            if (!engines.TryGetValue(engine, out var users)) engines[engine] = users = [];
            users.Add((pid, value));
        }

        if (engines.Count == 0) return (0, []);
        var busiest = engines.Values.MaxBy(users => users.Sum(u => u.Value))!;
        return (Math.Min(100, busiest.Sum(u => u.Value)), busiest);
    }

    // For example "pid_1234_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D".
    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<luid>0x[0-9a-f]+_0x[0-9a-f]+)_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();
}
