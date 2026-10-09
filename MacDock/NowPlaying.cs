using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MacDock;

/// <summary>
/// "Now playing" card for the sound panel: artwork, title, artist and previous / play-pause / next for whatever
/// Windows considers the current media session (Apple Music, Spotify, a browser tab...). Collapses itself,
/// including its divider, when nothing is playing.
/// </summary>
sealed class NowPlayingCard : StackPanel
{
    readonly Dispatcher _ui;
    readonly Image _art = new() { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
    readonly Border _artFrame;
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _artist = new() { FontSize = 12, Foreground = Ui.SecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
    readonly Border _prev, _playPause, _next;
    readonly Border _playGlyph = new(), _pauseGlyph = new();
    GlobalSystemMediaTransportControlsSessionManager _manager;
    GlobalSystemMediaTransportControlsSession _session;
    string _artKey;

    public NowPlayingCard(Dispatcher ui)
    {
        _ui = ui;
        Visibility = Visibility.Collapsed;
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        _artFrame = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(6),
            Background = Ui.Brush("#2B2B2B"),
            Child = _art,
            ClipToBounds = true,
        };
        _artFrame.Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 6, 6);

        _playGlyph.Child = Glyphs.Box(18, Glyphs.Fill(Glyphs.Play));
        _pauseGlyph.Child = Glyphs.Box(18, Glyphs.Stroke(Glyphs.Pause, 3));
        var playPauseContent = new Grid();
        playPauseContent.Children.Add(_playGlyph);
        playPauseContent.Children.Add(_pauseGlyph);

        _prev = Button(Glyphs.Box(16, Glyphs.Fill(Glyphs.Previous)), () => _session?.TrySkipPreviousAsync());
        _playPause = Button(playPauseContent, () => _session?.TryTogglePlayPauseAsync());
        _next = Button(Glyphs.Box(16, Glyphs.Fill(Glyphs.Next)), () => _session?.TrySkipNextAsync());

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 4, 0, 0) };
        controls.Children.Add(_prev);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_artist);
        text.Children.Add(controls);

        var row = new DockPanel();
        DockPanel.SetDock(_artFrame, System.Windows.Controls.Dock.Left);
        row.Children.Add(_artFrame);
        row.Children.Add(text);

        Children.Add(row);
        Children.Add(new Border { Height = 1, Background = Ui.Brush("#3C3C3C"), Margin = new Thickness(0, 12, 0, 10) });

        _ = InitAsync();
    }

    static Border Button(UIElement content, Func<object> action)
    {
        var chip = Ui.Chip(content, new Thickness(6));
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { action(); }
            catch (Exception ex) { Log.Error("media control", ex); }
        };
        return chip;
    }

    async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(() => Attach(_manager.GetCurrentSession()));
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception e) { Log.Error("media sessions", e); }
    }

    void Attach(GlobalSystemMediaTransportControlsSession session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
        }
        Refresh();
    }

    // Raised on a background thread.
    void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ui.BeginInvoke(Refresh);

    /// <summary>Re-reads the current track. Also called whenever the panel opens.</summary>
    public async void Refresh()
    {
        var session = _session;
        if (session == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            if (session != _session) return; // switched while we were reading
            if (props == null || string.IsNullOrWhiteSpace(props.Title))
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            _title.Text = props.Title;
            _artist.Text = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle : props.Artist;

            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            _pauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            var c = playback?.Controls;
            _prev.Opacity = c?.IsPreviousEnabled == true ? 1 : 0.35;
            _next.Opacity = c?.IsNextEnabled == true ? 1 : 0.35;
            _playPause.Opacity = c == null || c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled ? 1 : 0.35;

            // Artwork: only re-read when the track changes.
            string key = props.Title + "\n" + props.Artist + "\n" + props.AlbumTitle;
            if (key != _artKey)
            {
                _artKey = key;
                _art.Source = await LoadArt(props);
            }
            Visibility = Visibility.Visible;
        }
        catch (Exception e)
        {
            Log.Error("now playing", e);
            Visibility = Visibility.Collapsed;
        }
    }

    static async Task<ImageSource> LoadArt(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 112;
            bmp.StreamSource = stream.AsStreamForRead();
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e)
        {
            Log.Error("album art", e);
            return null;
        }
    }
}
