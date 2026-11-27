using System.Runtime.InteropServices;

namespace Capsule.Core;

/// <summary>What the browser windows say about a title that is playing.</summary>
internal enum YouTubeTab
{
    /// <summary>No window shows a YouTube tab.</summary>
    None,

    /// <summary>A window shows YouTube's tab title for this title: it's a YouTube video.</summary>
    ThisTitle,

    /// <summary>A YouTube tab is showing, but with another title (it may not have caught up yet).</summary>
    OtherTitle,
}

/// <summary>
/// Tells a YouTube video in a browser apart from other media. Windows only says which browser is playing, not which
/// site, so this looks for a window titled like YouTube's own tab: the playing title, then " - YouTube". Window titles
/// are only compared at that moment, never kept or logged.
/// </summary>
internal static class YouTube
{
    private const string Suffix = " - YouTube";
    private const int MaxTitle = 1000;

    /// <summary>
    /// Which kind of YouTube tab title <paramref name="windowTitle"/> is. Browsers add their own name after it
    /// (" - Google Chrome", " and 2 more pages - Microsoft Edge"), and YouTube may put unread notifications in front
    /// ("(3) "). YouTube Music never counts: songs keep their normal music pill.
    /// </summary>
    public static YouTubeTab Classify(string windowTitle, string mediaTitle)
    {
        var full = windowTitle.AsSpan().Trim();
        var title = SkipNotificationCount(full);
        var media = mediaTitle.AsSpan().Trim();

        // With and without the notification count: a video's own title may start with "(1) " too.
        if (IsThisTitle(title, media) || IsThisTitle(full, media)) return YouTubeTab.ThisTitle;

        // Any other YouTube tab title. The video's own title may contain " - YouTube" too, so look at every one.
        for (var at = title.IndexOf(Suffix, StringComparison.Ordinal); at >= 0;)
        {
            if (IsBrowserPart(title[(at + Suffix.Length)..])) return YouTubeTab.OtherTitle;
            var next = title[(at + 1)..].IndexOf(Suffix, StringComparison.Ordinal);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return YouTubeTab.None;
    }

    /// <summary>The best answer across every visible window (a match anywhere wins).</summary>
    public static YouTubeTab Find(string mediaTitle)
    {
        if (mediaTitle.Length is 0 or > MaxTitle) return YouTubeTab.None;

        var result = YouTubeTab.None;
        var buffer = new char[MaxTitle + 64];
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window)) return true;
                // For another program's window this reads the stored caption; it never waits on that program.
                var length = GetWindowTextW(window, buffer, buffer.Length);
                if (length < Suffix.Length) return true;
                var kind = Classify(new string(buffer, 0, length), mediaTitle);
                if (kind == YouTubeTab.ThisTitle)
                {
                    result = kind;
                    return false;
                }

                if (kind == YouTubeTab.OtherTitle) result = kind;
            }
            catch
            {
                // A window that closed mid-check is fine to skip.
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsThisTitle(ReadOnlySpan<char> title, ReadOnlySpan<char> media)
    {
        if (media.Length == 0 || !title.StartsWith(media, StringComparison.Ordinal)) return false;
        var after = title[media.Length..];
        return after.StartsWith(Suffix, StringComparison.Ordinal) && IsBrowserPart(after[Suffix.Length..]);
    }

    // Nothing after " - YouTube", or a space and the browser's part, but not "YouTube Music" or "YouTuber".
    private static bool IsBrowserPart(ReadOnlySpan<char> rest) =>
        rest.Length == 0
        || (char.IsWhiteSpace(rest[0]) && !rest.TrimStart().StartsWith("Music", StringComparison.OrdinalIgnoreCase));

    private static ReadOnlySpan<char> SkipNotificationCount(ReadOnlySpan<char> title)
    {
        if (title.Length < 4 || title[0] != '(') return title;
        var close = title.IndexOf(") ", StringComparison.Ordinal);
        if (close is < 2 or > 5) return title;
        foreach (var c in title[1..close])
        {
            if (!char.IsAsciiDigit(c) && c != '+') return title;
        }

        return title[(close + 2)..];
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, char[] text, int maxCount);
}
