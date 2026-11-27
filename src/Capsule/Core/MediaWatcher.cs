using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Capsule.Core;

/// <summary>What is playing. For a YouTube video only <see cref="Playing"/> is filled in: the pill shows no title.</summary>
internal sealed record MediaInfo(string Title, string Artist, bool Playing, ImageSource? Art, bool YouTube = false);

/// <summary>Reports play/pause and track changes from the app Windows shows in its media controls.</summary>
internal sealed class MediaWatcher : IDisposable
{
    private const long MaxArtBytes = 2 * 1024 * 1024;
    private const int MaxArtSide = 3000;
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TabCatchUp = TimeSpan.FromMilliseconds(700);

    private readonly Dispatcher _ui;
    private readonly Func<bool> _wanted;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _lastKey = "";
    private string? _youTubeTitle; // the title last recognized as a YouTube video in this session
    private int _refreshes;
    private DateTime _quietUntil;
    private bool _disposed;

    public MediaWatcher(Dispatcher ui, Func<bool> wanted)
    {
        _ui = ui;
        _wanted = wanted;
    }

    public event Action<MediaInfo>? Changed;

    public async void Start()
    {
        try
        {
            _quietUntil = DateTime.UtcNow + StartupQuiet;
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return; // switched off while starting
            _manager = manager;
            _manager.CurrentSessionChanged += OnSessionChanged;
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            Log.Error("Media HUD is unavailable", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager is not null) _manager.CurrentSessionChanged -= OnSessionChanged;
        Attach(null);
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager manager, CurrentSessionChangedEventArgs args) =>
        _ui.BeginInvoke(() => Attach(manager.GetCurrentSession()));

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed) session = null;
        if (ReferenceEquals(session, _session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }

        _session = session;
        _youTubeTitle = null;
        if (session is null) return;

        session.MediaPropertiesChanged += OnPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        _ = RefreshAsync(session, announce: false);
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession session, MediaPropertiesChangedEventArgs args) =>
        _ui.BeginInvoke(() => _ = RefreshAsync(session, announce: true));

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args) =>
        _ui.BeginInvoke(() => _ = RefreshAsync(session, announce: true));

    private async Task RefreshAsync(GlobalSystemMediaTransportControlsSession session, bool announce)
    {
        try
        {
            if (!ReferenceEquals(session, _session)) return;

            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!playing && status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) return;

            // Refreshes overlap (the YouTube check can wait); only the newest may announce, so an older one can't
            // show a play state that has already changed.
            var refresh = ++_refreshes;
            bool Stale() => refresh != _refreshes || !ReferenceEquals(session, _session);

            var properties = await session.TryGetMediaPropertiesAsync();
            // Any app (even a web page) can set these: keep them to one short clean line.
            var title = DisplayText.Clean(properties?.Title, 150);
            if (title.Length == 0 || Stale()) return;
            var artist = DisplayText.Clean(properties!.Artist, 100);

            // A YouTube video shows as "YouTube" and Playing or Paused, without the video's title or picture.
            var youTube = _wanted() && await IsYouTubeAsync(session, properties.Title?.Trim() ?? "");
            if (Stale()) return;

            // Apps send several updates per change; only a new title or a play/pause flip is news. The YouTube pill
            // shows no title, so there only the flip is.
            var key = youTube ? $"\u0002YouTube\u0001{playing}" : $"{title}\u0001{artist}\u0001{playing}";
            if (key == _lastKey) return;
            _lastKey = key;
            if (!announce || DateTime.UtcNow < _quietUntil) return;

            if (!_wanted()) return; // media HUD is off: don't even load the picture
            if (youTube)
            {
                Changed?.Invoke(new MediaInfo("YouTube", "", playing, null, YouTube: true));
                return;
            }

            var art = await LoadArtAsync(properties.Thumbnail);
            if (Stale()) return;
            Changed?.Invoke(new MediaInfo(title, artist, playing, art));
        }
        catch (Exception ex)
        {
            Log.Error("Reading the media info failed", ex);
        }
    }

    private async Task<bool> IsYouTubeAsync(GlobalSystemMediaTransportControlsSession session, string title)
    {
        if (title.Length == 0) return false;
        if (title == _youTubeTitle) return true; // the same video, even once its tab is no longer in front

        var tab = YouTube.Find(title);
        if (tab == YouTubeTab.OtherTitle)
        {
            // When the next video starts, its tab title can follow the media info a moment later.
            await Task.Delay(TabCatchUp);
            if (!ReferenceEquals(session, _session)) return false;
            tab = YouTube.Find(title);
        }

        if (tab != YouTubeTab.ThisTitle) return false;
        _youTubeTitle = title;
        return true;
    }

    private static async Task<ImageSource?> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > MaxArtBytes) return null;

            // Count the bytes actually read, not the size the publisher reported.
            var buffer = new MemoryStream((int)stream.Size);
            using (var source = stream.AsStreamForRead())
            {
                var chunk = new byte[16384];
                int read;
                while ((read = await source.ReadAsync(chunk)) > 0)
                {
                    if (buffer.Length + read > MaxArtBytes) return null;
                    buffer.Write(chunk, 0, read);
                }
            }

            // Decode off the UI thread so audio switching never waits on a picture.
            return await Task.Run(() => Decode(buffer));
        }
        catch
        {
            return null; // no art is fine
        }
    }

    private static ImageSource? Decode(MemoryStream buffer)
    {
        try
        {
            // Look at the size first; refuse absurd dimensions before decoding pixels.
            buffer.Position = 0;
            var probe = BitmapDecoder.Create(buffer, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = probe.Frames.Count > 0 ? probe.Frames[0] : null;
            if (frame is null || frame.PixelWidth is < 1 or > MaxArtSide || frame.PixelHeight is < 1 or > MaxArtSide) return null;

            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96; // shown at 32 px
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze(); // frozen images can be used from the UI thread
            return image;
        }
        catch
        {
            return null;
        }
    }
}
