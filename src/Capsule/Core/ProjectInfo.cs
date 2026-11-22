namespace Capsule.Core;

/// <summary>Where Capsule lives on GitHub. The updater only ever talks to this repository.</summary>
internal static class ProjectInfo
{
    public const string Owner = "ahmedot";
    public const string Repository = "capsule-hud";

    public static bool IsConfigured => Owner.Length > 0 && Owner != "OWNER";

    public static string Url => $"https://github.com/{Owner}/{Repository}";

    public static string ReleasesUrl => $"{Url}/releases";

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>Every release download must start with this (before GitHub redirects to its file host).</summary>
    public static string DownloadPrefix => $"https://github.com/{Owner}/{Repository}/releases/download/";
}
