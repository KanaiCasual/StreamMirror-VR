using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GazeOverlay;

// The Status page's top half: the picture that goes out (the last one the app saw, framed by the crop box), the mirror line
// and its reflection, and beside it what the mirror sends. Read on demand: when the page is refreshed and while the live
// line ticks (see UpdateLive); the picture itself is only cut again when it or the box changed.
public partial class MainWindow
{
    private bool _layerActive;
    private (int Width, int Height)? _obsGets; // What OBS gets with the box as drawn on the Mirror page (see DrawCrop).

    private void UpdateStatusHero()
    {
        var live = MirrorLive.Read();
        var producing = live is { Producing: true };
        var reading = producing && live!.Width > 0; // a reader asked for the picture: the OBS source or the mirror window

        UpdateStatusPicture();
        var hasPicture = StatusPicture.Source != null;

        var onAir = (Brush)FindResource("OnAir");
        SetTally(StatusTally, StatusTallyDot, StatusTallyText, producing, reading, "Mirroring, nothing reads it yet");

        StatusWaitShade.Visibility = StatusWaitNote.Visibility = producing ? Visibility.Collapsed : Visibility.Visible;
        StatusWaitText.Text = "The mirror picks it up by itself, and OBS shows it." +
            (hasPicture ? $"\nThis is the last picture, from {File.GetLastWriteTime(LastPicturePath):d MMMM}." : "\nThe Mirror page takes a picture to frame on.");
        var eye = ChoiceLabel("mirror_eye");
        var cropOn = _values.GetValueOrDefault("crop_enabled") == "1";
        StatusCaption.Text = producing ? "The last picture, framed as OBS gets it" : "";
        StatusCaptionRight.Text = producing ? eye + (cropOn ? ", " + ChoiceLabel("crop_aspect") : ", whole image") : "";
        StatusLineDot.Fill = producing ? onAir : (Brush)FindResource("Muted");
        StatusAmbient.Opacity = producing ? 0.32 : 0.14;
        StatusReflection.Opacity = producing ? 1 : 0.5;

        // What goes out, and how.
        StatusGame.Text = producing ? (live!.Application.Length > 0 ? live.Application : live.Program) : "No game yet";
        StatusGameSub.Text = !producing ? "OpenXR or SteamVR, any headset"
            : live!.OpenXR ? $"OpenXR game, {live.Program}" : $"SteamVR game, {live.Program}";
        var size = reading ? $"{live!.Width} × {live.Height}" : _obsGets is { } gets ? $"{gets.Width} × {gets.Height}" : ChoiceLabel("output_max_side");
        var steady = new List<string>();
        if (_values.GetValueOrDefault("crop_follow", "off") != "off") steady.Add("follow");
        if (_values.GetValueOrDefault("stabilize") == "1")
        {
            steady.Add(_values.GetValueOrDefault("stabilize_tilt") switch { "level" => "level tilt", "head" => "steady", _ => "smoothed tilt" });
        }
        FillFacts(
        [
            ("Picture to OBS", size),
            ("Picture rate", ChoiceLabel("output_fps")),
            ("Eye", eye),
            ("Capture", ChoiceLabel("picture_source").Split(" (")[0]),
            ("Crop", cropOn ? ChoiceLabel("crop_aspect") : "Off, the whole image"),
            ("Steadying", steady.Count == 0 ? "Off" : char.ToUpper(steady[0][0]) + string.Join(", ", steady)[1..]),
        ]);
        FillPath([("Headset", producing), ("Mirror", producing || _layerActive), ("OBS or window", reading)]);
        LayOutStatusHero();
    }

    private string ChoiceLabel(string key)
    {
        var def = Settings.All.First(d => d.Key == key);
        var value = _values.GetValueOrDefault(key, def.Default);
        return string.Join(' ', (def.Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void FillFacts((string Label, string Value)[] facts)
    {
        if (StatusFacts.Children.Count != facts.Length * 3)
        {
            StatusFacts.Children.Clear();
            StatusFacts.RowDefinitions.Clear();
            StatusFacts.ColumnDefinitions.Clear();
            StatusFacts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StatusFacts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < facts.Length; i++)
            {
                StatusFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var line = new Border { Height = 1, Background = (Brush)FindResource("Line"), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(line, i);
                Grid.SetColumnSpan(line, 2);
                var label = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 9, 16, 9) };
                Grid.SetRow(label, i);
                var value = new TextBlock { FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 9, 0, 9), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                StatusFacts.Children.Add(line);
                StatusFacts.Children.Add(label);
                StatusFacts.Children.Add(value);
            }
        }
        for (var i = 0; i < facts.Length; i++)
        {
            ((TextBlock)StatusFacts.Children[i * 3 + 1]).Text = facts[i].Label;
            ((TextBlock)StatusFacts.Children[i * 3 + 2]).Text = facts[i].Value;
        }
    }

    /// <summary>Headset, mirror, reader: a tick in a cyan ring for each part the picture has reached, an empty ring before.</summary>
    private void FillPath((string Name, bool Reached)[] steps)
    {
        StatusPath.Children.Clear();
        StatusPath.ColumnDefinitions.Clear();
        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0)
            {
                StatusPath.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(bar, StatusPath.ColumnDefinitions.Count - 1);
                StatusPath.Children.Add(bar);
            }
            StatusPath.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var reached = steps[i].Reached;
            var mark = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 7, 0),
                Background = reached ? new SolidColorBrush(Color.FromArgb(0x2E, 0x2E, 0x9B, 0xFF)) : Brushes.Transparent,
                BorderBrush = reached ? null : (Brush)FindResource("Muted"), BorderThickness = new Thickness(reached ? 0 : 1.5),
                Child = reached ? new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 11, Foreground = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Center } : null,
            };
            var step = new StackPanel { Orientation = Orientation.Horizontal };
            step.Children.Add(mark);
            step.Children.Add(new TextBlock { Text = steps[i].Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource(reached ? "Text" : "Muted") });
            Grid.SetColumn(step, StatusPath.ColumnDefinitions.Count - 1);
            StatusPath.Children.Add(step);
        }
    }

    private string _framedKey = "";
    private BitmapSource? _framed;

    /// <summary>The picture as OBS gets it: the saved one, cut to the crop box when the box is on. Cut again only on a change.</summary>
    private BitmapSource? FramedPicture()
    {
        var source = _cropImage.Source as BitmapSource ?? _pictureFirst;
        var cropOn = _values.GetValueOrDefault("crop_enabled") == "1";
        var rect = cropOn && source != null ? _crop.Rect() : (0, 0, 1, 1);
        var key = $"{source?.GetHashCode()} {rect}";
        if (key == _framedKey) return _framed;
        _framedKey = key;
        _framed = source;
        if (source != null && cropOn)
        {
            var pixels = new Int32Rect((int)(rect.Item1 * source.PixelWidth), (int)(rect.Item2 * source.PixelHeight),
                                       Math.Max(1, (int)(rect.Item3 * source.PixelWidth)), Math.Max(1, (int)(rect.Item4 * source.PixelHeight)));
            pixels.Width = Math.Min(pixels.Width, source.PixelWidth - pixels.X);
            pixels.Height = Math.Min(pixels.Height, source.PixelHeight - pixels.Y);
            try { _framed = new CroppedBitmap(source, pixels); } catch (ArgumentException) { _framed = source; }
        }
        return _framed;
    }

    /// <summary>The reflection under the mirror line: the picture's lower edge against the line, upside down.</summary>
    private static Brush? Reflection(BitmapSource? picture) => picture == null ? null
        : new ImageBrush(picture) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Bottom, RelativeTransform = new ScaleTransform(1, -1, 0.5, 0.5) };

    private void UpdateStatusPicture()
    {
        var shown = FramedPicture();
        if (ReferenceEquals(shown, _statusShown) && shown != null) return;
        _statusShown = shown;
        StatusPicture.Source = StatusAmbient.Source = shown;
        StatusReflection.Fill = Reflection(shown);
        SizeStatusMonitor();
    }

    private BitmapSource? _statusShown;

    /// <summary>
    /// A picture's light: Live (red) while a game goes out to a reader - the OBS source or the mirror window -, cyan while
    /// it is mirrored and nothing reads it, an outlined "Waiting for a game" otherwise.
    /// </summary>
    private void SetTally(Border tally, Ellipse dot, TextBlock text, bool producing, bool reading, string mirroringText)
    {
        if (reading)
        {
            tally.Background = (Brush)FindResource("OnAir");
            dot.Fill = Brushes.White;
            dot.Stroke = null;
            text.Foreground = Brushes.White;
            text.Text = "Live";
        }
        else if (producing)
        {
            tally.Background = (Brush)FindResource("Accent");
            dot.Fill = (Brush)FindResource("OnAccent");
            dot.Stroke = null;
            text.Foreground = (Brush)FindResource("OnAccent");
            text.Text = mirroringText;
        }
        else
        {
            tally.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x09, 0x0E, 0x20));
            dot.Fill = null;
            dot.Stroke = (Brush)FindResource("Ice");
            dot.StrokeThickness = 2;
            text.Foreground = (Brush)FindResource("Ice");
            text.Text = "Waiting for a game";
        }
    }

    private DateTime _mirrorLiveRead;
    private MirrorState? _mirrorLiveLast;

    /// <summary>
    /// The Mirror page's "What OBS gets": the box's picture as it is drawn now. Follows every change of the box; the game's
    /// state is read at most once a second (a drag redraws many times a second).
    /// </summary>
    private void UpdateMirrorObsCard()
    {
        var shown = FramedPicture();
        if (!ReferenceEquals(shown, MirrorObsPicture.Source))
        {
            MirrorObsPicture.Source = shown;
            MirrorObsReflection.Fill = Reflection(shown);
            SizeMirrorObsFrame();
        }
        MirrorObsEmpty.Visibility = shown == null ? Visibility.Visible : Visibility.Collapsed;

        if ((DateTime.UtcNow - _mirrorLiveRead).TotalSeconds >= 1)
        {
            _mirrorLiveRead = DateTime.UtcNow;
            _mirrorLiveLast = MirrorLive.Read();
        }
        var producing = _mirrorLiveLast is { Producing: true };
        var reading = producing && _mirrorLiveLast!.Width > 0;
        MirrorObsTally.Visibility = producing ? Visibility.Visible : Visibility.Collapsed;
        if (producing) SetTally(MirrorObsTally, MirrorObsTallyDot, MirrorObsTallyText, producing, reading, "Mirroring");
        MirrorObsLineDot.Fill = (Brush)FindResource(producing ? "OnAir" : "Muted");
        MirrorObsFacts.Text = ChoiceLabel("mirror_eye") + (_obsGets is { } gets ? $", {gets.Width} × {gets.Height}" : "");
    }

    private void OnMirrorObsSize(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) SizeMirrorObsFrame();
    }

    private void SizeMirrorObsFrame()
    {
        var width = MirrorObsFrame.ActualWidth;
        if (width <= 0) return;
        var aspect = MirrorObsPicture.Source is BitmapSource { PixelHeight: > 0 } shown ? (double)shown.PixelWidth / shown.PixelHeight : 16.0 / 9.0;
        MirrorObsFrame.Height = Math.Min(width / Math.Clamp(aspect, 0.6, 3.5), 300);
    }

    private void OnStatusHeroSize(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayOutStatusHero();
    }

    private void OnStatusMonitorSize(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) SizeStatusMonitor();
    }

    /// <summary>The monitor takes the picture's shape (16:9 without one), never taller than the window has room for.</summary>
    private void SizeStatusMonitor()
    {
        var width = StatusMonitor.ActualWidth;
        if (width <= 0) return;
        var aspect = StatusPicture.Source is BitmapSource { PixelHeight: > 0 } shown ? (double)shown.PixelWidth / shown.PixelHeight : 16.0 / 9.0;
        StatusMonitor.Height = Math.Min(width / Math.Clamp(aspect, 0.6, 3.5), 460);
    }

    /// <summary>The readout beside the picture while there is room for both; under it in a narrow window.</summary>
    private void LayOutStatusHero()
    {
        var beside = StatusHero.ActualWidth is 0 or >= 820;
        StatusReadoutColumn.Width = beside ? new GridLength(330) : new GridLength(0);
        Grid.SetColumn(StatusReadout, beside ? 1 : 0);
        Grid.SetRow(StatusReadout, beside ? 0 : 1);
        StatusReadout.Margin = beside ? new Thickness(26, 0, 0, 0) : new Thickness(0, 18, 0, 0);
    }

    private void OnStatusOpenMirror(object sender, RoutedEventArgs e) => Tabs.SelectedItem = CropTab;

    /// <summary>Three tiles to a row on a wide page, two or one on a narrower one.</summary>
    private void OnStatusTilesSize(object sender, SizeChangedEventArgs e)
    {
        var width = StatusRows.ActualWidth;
        if (width <= 0) return;
        var columns = width >= 900 ? 3 : width >= 560 ? 2 : 1;
        var tile = Math.Floor(width / columns);
        if (double.IsNaN(StatusRows.ItemWidth) || Math.Abs(StatusRows.ItemWidth - tile) > 0.5) StatusRows.ItemWidth = tile;
    }

    /// <summary>One part: an icon, its name and what is known about it, a word for its state, and a button when there is something to do.</summary>
    private Border StatusTile(string icon, string title, string detail, Color color, string pill, (string Label, RoutedEventHandler Click)? button, string? tip)
    {
        // The status colours of the theme: cyan ready, grey not needed, amber a problem, red missing.
        var (fore, back) = color == Colors.SeaGreen ? ("#7CC0FF", "#242E9BFF")
                         : color == Colors.Gray ? ("#9DA9C8", "#1A9DA9C8")
                         : color == Colors.Firebrick ? ("#FF7A86", "#26FF3B4D")
                         : ("#F5B342", "#26F5B342");
        var converter = new BrushConverter();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = (Brush)converter.ConvertFrom("#1A2550")!, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), FontSize = 16, Foreground = (Brush)FindResource("Ice"), HorizontalAlignment = HorizontalAlignment.Center },
        });
        var words = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Hint"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) });
        if (button is { } action)
        {
            var control = new Button { Content = action.Label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            control.Click += action.Click;
            words.Children.Add(control);
        }
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        var state = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Top, Background = (Brush)converter.ConvertFrom(back)!,
            Child = new TextBlock { Text = pill, FontSize = 12, Foreground = (Brush)converter.ConvertFrom(fore)! },
        };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);
        var tile = new Border
        {
            Background = (Brush)converter.ConvertFrom("#C8131B36")!, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 12, 12), Margin = new Thickness(0, 0, 10, 10), Child = grid,
        };
        if (tip != null) tile.ToolTip = WrappedToolTip(tip);
        return tile;
    }
}
