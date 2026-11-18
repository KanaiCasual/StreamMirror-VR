using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StreamMirror;

/// <summary>
/// The Desktop ring page: the ring alone, lined up with the game's own desktop window. One countdown takes a picture of
/// each eye and one of the game's window; the window's picture stays put, the user moves and sizes the eye's picture
/// over it until they match. What the window then shows of the eye image is the box (DesktopRing.BoxFromOverlay), saved
/// for the game or for every game in desktop-ring.ini; the mirror takes it while the ring is alone.
/// </summary>
public partial class MainWindow
{
    /// <summary>Where the headset's picture lies over the window's: the box's centre and height, fractions of the eye image.</summary>
    private readonly record struct RingPlacement(double CenterX, double CenterY, double Height);

    private BitmapSource? _ringWindowPicture;
    private readonly BitmapSource?[] _ringEyePictures = new BitmapSource?[2];
    // The box being lined up: its centre and height as fractions of the eye image (its shape is the window's).
    private double _ringCenterX = 0.5, _ringCenterY = 0.5, _ringHeight = 1;
    private bool _ringHaveBox;
    private Rect _ringWindowRect; // where the window's picture lies on the stage
    private DateTime? _ringDeadline;
    private int _ringCountdownSent;
    private readonly DispatcherTimer _ringCountdownTimer = new() { Interval = TimeSpan.FromMilliseconds(200) }; // runs only during a countdown
    private Point? _ringDragFrom;
    private Rect _ringDragRect;
    // The difference view works on small copies of the pictures, at most this wide or high.
    private const int RingDiffSide = 480;
    private (byte[] Pixels, int Width, int Height)? _ringWindowPixels;
    private readonly (byte[] Pixels, int Width, int Height)?[] _ringEyePixels = new (byte[], int, int)?[2];
    private WriteableBitmap? _ringDiff;

    private static string RingWindowPath => Path.Combine(AppSettings.Folder, "desktop-ring-window.png");
    private static string RingEyePath(int eye) => Path.Combine(AppSettings.Folder, eye == 0 ? "desktop-ring-eye-left.png" : "desktop-ring-eye-right.png");

    private void BuildDesktopRingUi()
    {
        PanelDesktopRingSwitch.Children.Add(BuildRow(Settings.All.First(d => d.Key == "output_ring_only")));

        foreach (var seconds in new[] { 5, 10, 15, 20 }) DesktopRingTimer.Items.Add(new ComboBoxItem { Content = $"Countdown {seconds} s", Tag = seconds });
        DesktopRingTimer.SelectedItem = DesktopRingTimer.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == _appSettings.DesktopRingSeconds) ?? DesktopRingTimer.Items[1];
        DesktopRingTimer.SelectionChanged += (_, _) =>
        {
            if (DesktopRingTimer.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
            _appSettings.DesktopRingSeconds = seconds;
            _appSettings.Save();
        };

        DesktopRingOpacity.Value = Math.Clamp(_appSettings.DesktopRingOpacity, 0, 100);
        DesktopRingOpacity.ValueChanged += (_, _) =>
        {
            _appSettings.DesktopRingOpacity = (int)Math.Round(DesktopRingOpacity.Value);
            _appSettings.Save();
            DrawDesktopRing();
        };
        DesktopRingDifference.Click += (_, _) => DrawDesktopRing();
        void EyeChosen(int eye)
        {
            if (_loading || _appSettings.DesktopRingEye == eye) return;
            _appSettings.DesktopRingEye = eye;
            _appSettings.Save();
            DrawDesktopRing();
        }
        DesktopRingEyeLeft.Checked += (_, _) => EyeChosen(0);
        DesktopRingEyeRight.Checked += (_, _) => EyeChosen(1);

        DesktopRingStageFrame.SizeChanged += (_, e) => { if (e.WidthChanged) LayOutDesktopRing(); };
        DesktopRingCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        DesktopRingCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        DesktopRingCards.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            var narrow = DesktopRingCards.ActualWidth < 640; // the two cards side by side need about that
            DesktopRingCards.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(DesktopRingObsCard, narrow ? 0 : 1);
            Grid.SetRow(DesktopRingObsCard, narrow ? 1 : 0);
        };

        DesktopRingStage.MouseLeftButtonDown += OnDesktopRingDragStart;
        DesktopRingStage.MouseMove += OnDesktopRingDrag;
        DesktopRingStage.MouseLeftButtonUp += (_, _) => { _ringDragFrom = null; DesktopRingStage.ReleaseMouseCapture(); };
        DesktopRingStage.MouseWheel += OnDesktopRingWheel;
        DesktopRingStage.KeyDown += OnDesktopRingKey;

        _ringCountdownTimer.Tick += OnDesktopRingCountdownTick;
        Closed += (_, _) => StopDesktopRingCountdown();
        // What the page says about the running game is looked up when somebody looks at it, not on a timer.
        Tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, Tabs) && ReferenceEquals(Tabs.SelectedItem, DesktopRingTab)) UpdateDesktopRingInUse(); };
        Activated += (_, _) => { if (ReferenceEquals(Tabs.SelectedItem, DesktopRingTab)) UpdateDesktopRingInUse(); };

        LoadDesktopRingPictures();
        FillDesktopRingSaved();
        UpdateDesktopRingInUse();
    }

    // ------------------------------------------------------------------ the pictures

    private void LoadDesktopRingPictures()
    {
        static BitmapSource? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // the next capture replaces the file
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
        _ringWindowPicture = Load(RingWindowPath);
        for (var eye = 0; eye < 2; eye++) _ringEyePictures[eye] = _ringWindowPicture != null ? Load(RingEyePath(eye)) : null;
        PicturesTaken(_appSettings.DesktopRingGame);
    }

    /// <summary>New pictures (or the kept ones): the small copies for the difference view, and the box to start from.</summary>
    private void PicturesTaken(string game)
    {
        _ringWindowPixels = _ringWindowPicture != null ? SmallPixels(_ringWindowPicture) : null;
        for (var eye = 0; eye < 2; eye++) _ringEyePixels[eye] = _ringEyePictures[eye] is { } picture ? SmallPixels(picture) : null;
        if (_ringEyePictures[_appSettings.DesktopRingEye == 0 ? 0 : 1] == null && _ringEyePictures.FirstOrDefault(p => p != null) is { } only)
            _appSettings.DesktopRingEye = Array.IndexOf(_ringEyePictures, only);
        // Start from what is saved for this game (or for every game), else from the window's width.
        if (DesktopRing.For(DesktopRing.Load(), game) is { } saved)
        {
            (_ringCenterX, _ringCenterY, _ringHeight) = (saved.CenterX, saved.CenterY, saved.Height);
            if (_ringEyePictures[saved.Eye] != null) _appSettings.DesktopRingEye = saved.Eye;
            _ringHaveBox = true;
        }
        else if (!_ringHaveBox) FitDesktopRing();
        LayOutDesktopRing();
    }

    private static (byte[] Pixels, int Width, int Height) SmallPixels(BitmapSource picture)
    {
        var scale = Math.Min(1.0, (double)RingDiffSide / Math.Max(picture.PixelWidth, picture.PixelHeight));
        BitmapSource source = scale < 1 ? new TransformedBitmap(picture, new ScaleTransform(scale, scale)) : picture;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    private BitmapSource? RingEyePicture => _ringEyePictures[_appSettings.DesktopRingEye == 0 ? 0 : 1];
    private double RingWindowAspect => _ringWindowPicture is { } w ? (double)w.PixelWidth / w.PixelHeight : 16.0 / 9;
    private double RingEyeAspect => RingEyePicture is { } e ? (double)e.PixelWidth / e.PixelHeight : 1;

    /// <summary>The eye's picture as wide as the window, centred.</summary>
    private void FitDesktopRing()
    {
        (_ringCenterX, _ringCenterY, _ringHeight) = FitPlacement();
        _ringHaveBox = RingEyePicture != null && _ringWindowPicture != null;
    }

    private RingPlacement FitPlacement() => new(0.5, 0.5, Math.Clamp(RingEyeAspect / RingWindowAspect, 0.02, 4)); // eye width = window width

    private void OnDesktopRingFit(object sender, RoutedEventArgs e)
    {
        PlaceRing(FitPlacement());
        DesktopRingStage.Focus();
    }

    private RingPlacement CurrentRingPlacement => new(_ringCenterX, _ringCenterY, _ringHeight);

    /// <summary>The user moved or sized the headset's picture: one undoable change (Ctrl+Z).</summary>
    private void PlaceRing(RingPlacement placement)
    {
        RecordPlacementUndo(CurrentRingPlacement, placement);
        ApplyRingPlacement(placement);
    }

    /// <summary>Puts the headset's picture there, as undo and redo do: not recorded.</summary>
    private void ApplyRingPlacement(RingPlacement placement)
    {
        (_ringCenterX, _ringCenterY, _ringHeight) = placement;
        DrawDesktopRing();
    }

    // ------------------------------------------------------------------ the stage

    /// <summary>The stage's height follows its width; the window's picture sits in its middle with room around it, where
    /// the rest of the eye's picture shows.</summary>
    private void LayOutDesktopRing()
    {
        var width = DesktopRingStageFrame.ActualWidth;
        if (width < 10) return;
        var height = Math.Clamp(width * 0.5, 200, 440); // the buttons above and the tools below stay in sight with it
        DesktopRingStageFrame.Height = height;
        var room = Math.Min(width * 0.74 / RingWindowAspect, height * 0.74);
        var size = new Size(room * RingWindowAspect, room);
        _ringWindowRect = new Rect((width - size.Width) / 2, (height - size.Height) / 2, size.Width, size.Height);
        // The keys' steps in the window's own pixels: the picture on the stage is the window made smaller.
        var perStep = _ringWindowPicture is { } window ? Math.Max(1, (int)Math.Round(window.PixelWidth / size.Width)) : 0;
        DesktopRingKeysHint.Text = "Drag to move the headset's picture, mouse wheel to size it (Shift for fine steps). " +
            (perStep > 0 ? $"Arrow keys move it about {perStep} pixels of the game's window, Shift+arrow about {perStep * 10}, Ctrl+arrow exactly 1. "
                         : "Arrow keys move it, Shift+arrow in big jumps, Ctrl+arrow one pixel of the game's window. ") +
            "+ and − size it. Ctrl+Z undoes, Ctrl+Y redoes.";
        DrawDesktopRing();
    }

    /// <summary>Where the eye's picture lies on the stage for the box.</summary>
    private Rect RingEyeRect()
    {
        var r = DesktopRing.OverlayFromBox(_ringWindowRect.Width, _ringWindowRect.Height, RingEyeAspect, _ringCenterX, _ringCenterY, _ringHeight);
        r.Offset(_ringWindowRect.X, _ringWindowRect.Y);
        return r;
    }

    private void SetRingEyeRect(Rect eye)
    {
        if (eye.Width < 1 || eye.Height < 1) return;
        eye.Offset(-_ringWindowRect.X, -_ringWindowRect.Y);
        var (centerX, centerY, height, _) = DesktopRing.BoxFromOverlay(_ringWindowRect.Width, _ringWindowRect.Height, eye);
        if (height is < 0.02 or > 4) return; // as small or as big as the mirror takes
        PlaceRing(new RingPlacement(centerX, centerY, height));
    }

    private void DrawDesktopRing()
    {
        var eyePicture = RingEyePicture;
        var have = _ringWindowPicture != null && eyePicture != null && _ringWindowRect.Width > 1;
        DesktopRingEmpty.Visibility = _ringWindowPicture != null && eyePicture != null ? Visibility.Collapsed : Visibility.Visible;
        DesktopRingStage.Visibility = have ? Visibility.Visible : Visibility.Collapsed;
        DesktopRingTools.IsEnabled = have;
        _loading = true;
        DesktopRingEyeLeft.IsEnabled = _ringEyePictures[0] != null;
        DesktopRingEyeRight.IsEnabled = _ringEyePictures[1] != null;
        DesktopRingEyeLeft.IsChecked = _appSettings.DesktopRingEye == 0;
        DesktopRingEyeRight.IsChecked = _appSettings.DesktopRingEye != 0;
        _loading = false;
        UpdateDesktopRingSaveButtons();
        if (!have) return;

        static void Place(FrameworkElement element, Rect rect)
        {
            Canvas.SetLeft(element, rect.X);
            Canvas.SetTop(element, rect.Y);
            element.Width = rect.Width;
            element.Height = rect.Height;
        }
        var difference = DesktopRingDifference.IsChecked == true;
        DesktopRingWindowImage.Source = _ringWindowPicture;
        Place(DesktopRingWindowImage, _ringWindowRect);
        Place(DesktopRingWindowEdge, _ringWindowRect);
        DesktopRingEyeImage.Source = eyePicture;
        DesktopRingEyeImage.Opacity = DesktopRingOpacity.Value / 100;
        Place(DesktopRingEyeImage, RingEyeRect());
        // The difference covers the window; around it the eye's picture still shows where the rest of the view lies.
        DesktopRingDiffImage.Visibility = difference ? Visibility.Visible : Visibility.Collapsed;
        if (difference && DrawRingDifference() is { } diff)
        {
            DesktopRingDiffImage.Source = diff;
            Place(DesktopRingDiffImage, _ringWindowRect);
        }
    }

    /// <summary>|window - eye| per channel, doubled, over the window: lined up, it goes dark.</summary>
    private WriteableBitmap? DrawRingDifference()
    {
        if (_ringWindowPixels is not { } window || _ringEyePixels[_appSettings.DesktopRingEye == 0 ? 0 : 1] is not { } eye) return null;
        if (_ringDiff == null || _ringDiff.PixelWidth != window.Width || _ringDiff.PixelHeight != window.Height)
            _ringDiff = new WriteableBitmap(window.Width, window.Height, 96, 96, PixelFormats.Bgr32, null);
        var result = new byte[window.Pixels.Length];
        var boxWidth = _ringHeight * RingWindowAspect / ((double)eye.Width / eye.Height); // of the eye image's width
        for (var y = 0; y < window.Height; y++)
        {
            var v = _ringCenterY + ((y + 0.5) / window.Height - 0.5) * _ringHeight;
            var eyeY = (int)Math.Floor(v * eye.Height);
            for (var x = 0; x < window.Width; x++)
            {
                var at = (y * window.Width + x) * 4;
                var u = _ringCenterX + ((x + 0.5) / window.Width - 0.5) * boxWidth;
                var eyeX = (int)Math.Floor(u * eye.Width);
                if (eyeX < 0 || eyeY < 0 || eyeX >= eye.Width || eyeY >= eye.Height) continue; // the headset shows nothing there: black
                var from = (eyeY * eye.Width + eyeX) * 4;
                for (var c = 0; c < 3; c++) result[at + c] = (byte)Math.Min(255, 2 * Math.Abs(window.Pixels[at + c] - eye.Pixels[from + c]));
            }
        }
        _ringDiff.WritePixels(new Int32Rect(0, 0, window.Width, window.Height), result, window.Width * 4, 0);
        return _ringDiff;
    }

    private void OnDesktopRingDragStart(object sender, MouseButtonEventArgs e)
    {
        DesktopRingStage.Focus();
        if (!_ringHaveBox) return;
        _ringDragFrom = e.GetPosition(DesktopRingStage);
        _ringDragRect = RingEyeRect();
        DesktopRingStage.CaptureMouse();
        e.Handled = true;
    }

    private void OnDesktopRingDrag(object sender, MouseEventArgs e)
    {
        if (_ringDragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var rect = _ringDragRect;
        rect.Offset(e.GetPosition(DesktopRingStage) - from);
        SetRingEyeRect(rect);
    }

    private void OnDesktopRingWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_ringHaveBox) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.004 : 1.04;
        ScaleRingEye(Math.Pow(step, e.Delta / 120.0), e.GetPosition(DesktopRingStage));
        e.Handled = true; // the page does not scroll under the picture
    }

    /// <summary>Sizes the eye's picture around a point of the stage, which stays where it is.</summary>
    private void ScaleRingEye(double factor, Point around)
    {
        var rect = RingEyeRect();
        SetRingEyeRect(new Rect(around.X + (rect.X - around.X) * factor, around.Y + (rect.Y - around.Y) * factor, rect.Width * factor, rect.Height * factor));
    }

    private void OnDesktopRingKey(object sender, KeyEventArgs e)
    {
        if (!_ringHaveBox) return;
        var fine = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Ctrl: one pixel of the game's window (a fraction of a pixel on the stage); else one stage pixel, or ten with Shift.
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _ringWindowPicture is { } window ? _ringWindowRect.Width / window.PixelWidth : fine ? 1 : 10;
        var centre = new Point(_ringWindowRect.X + _ringWindowRect.Width / 2, _ringWindowRect.Y + _ringWindowRect.Height / 2);
        var rect = RingEyeRect();
        switch (e.Key)
        {
            case Key.Left: rect.X -= step; SetRingEyeRect(rect); break;
            case Key.Right: rect.X += step; SetRingEyeRect(rect); break;
            case Key.Up: rect.Y -= step; SetRingEyeRect(rect); break;
            case Key.Down: rect.Y += step; SetRingEyeRect(rect); break;
            case Key.OemPlus or Key.Add: ScaleRingEye(fine ? 1.002 : 1.02, centre); break;
            case Key.OemMinus or Key.Subtract: ScaleRingEye(fine ? 1 / 1.002 : 1 / 1.02, centre); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ the capture

    private void OnDesktopRingCapture(object sender, RoutedEventArgs e)
    {
        if (MirrorLive.ReadProducer() is not { Kind: not 0 })
        {
            DesktopRingStatus.Text = "No VR game is running. Start the game, then press Capture.";
            return;
        }
        if (DesktopRing.FindGameWindow() == null)
        {
            DesktopRingStatus.Text = "The game has no window on the monitor to capture.";
            return;
        }
        _ringDeadline = DateTime.UtcNow.AddSeconds(_appSettings.DesktopRingSeconds);
        _ringCountdownTimer.Start();
        DesktopRingCapture.IsEnabled = false;
        DesktopRingTimer.IsEnabled = false;
        DesktopRingCancel.Visibility = Visibility.Visible;
        OnDesktopRingCountdownTick(null, EventArgs.Empty);
        AppLog.Write($"Desktop ring: capture countdown {_appSettings.DesktopRingSeconds} s.");
    }

    private void OnDesktopRingCountdownTick(object? sender, EventArgs e)
    {
        if (_ringDeadline is not { } deadline) return;
        var left = Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
        if (left > 0)
        {
            if (left != _ringCountdownSent) { MirrorSnapshot.SetCountdown(left); _ringCountdownSent = left; } // the headset shows it too
            DesktopRingStatus.Text = $"Pictures in {left} s. Put the headset on, look ahead and hold still.";
            return;
        }
        TakeDesktopRingPictures();
    }

    private void OnDesktopRingCancel(object sender, RoutedEventArgs e)
    {
        StopDesktopRingCountdown();
        DesktopRingStatus.Text = "Capture cancelled.";
    }

    private void StopDesktopRingCountdown()
    {
        _ringCountdownTimer.Stop();
        _ringDeadline = null;
        if (_ringCountdownSent != 0) { MirrorSnapshot.SetCountdown(0); _ringCountdownSent = 0; }
        DesktopRingCapture.IsEnabled = true;
        DesktopRingTimer.IsEnabled = true;
        DesktopRingCancel.Visibility = Visibility.Collapsed;
    }

    private async void TakeDesktopRingPictures()
    {
        StopDesktopRingCountdown(); // the number leaves the headset before its picture is taken
        DesktopRingCapture.IsEnabled = false;
        DesktopRingStatus.Text = "Taking the pictures...";
        var (picture, message) = await MirrorSnapshot.RequestAsync();
        // The window right after the eyes: the user is still holding still.
        var found = DesktopRing.FindGameWindow();
        var problem = "The game has no window on the monitor to capture.";
        var window = found is { } f ? DesktopRing.CaptureWindow(f.Window, out problem) : null;
        DesktopRingCapture.IsEnabled = true;
        if (picture == null || window == null)
        {
            System.Media.SystemSounds.Hand.Play();
            DesktopRingStatus.Text = picture == null ? "No picture came from the headset: " + message : problem;
            AppLog.Write("Desktop ring: capture failed (" + DesktopRingStatus.Text + ").");
            return;
        }
        System.Media.SystemSounds.Asterisk.Play();

        Array.Clear(_ringEyePictures);
        _ringEyePictures[picture.Eye == 0 ? 0 : 1] = picture.Image; // an older layer that does not say: taken as the right eye
        if (picture.OtherImage != null && picture.OtherEye is 0 or 1) _ringEyePictures[picture.OtherEye] = picture.OtherImage;
        _ringWindowPicture = window;
        var game = found!.Value.Game;
        _appSettings.DesktopRingGame = game;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            static void Save(BitmapSource image, string path)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(path);
                encoder.Save(file);
            }
            Save(window, RingWindowPath);
            for (var eye = 0; eye < 2; eye++)
            {
                if (_ringEyePictures[eye] is { } image) Save(image, RingEyePath(eye));
                else File.Delete(RingEyePath(eye));
            }
        }
        catch
        {
            // Not keeping the pictures for next time is not worth bothering anyone about.
        }
        PicturesTaken(game);
        _appSettings.Save();
        var both = _ringEyePictures[0] != null && _ringEyePictures[1] != null;
        DesktopRingStatus.Text = $"Captured at {DateTime.Now:T}: {(both ? "both eyes" : "one eye")} and {(game.Length > 0 ? game + "'s" : "the game's")} window ({window.PixelWidth} x {window.PixelHeight}). Line them up, then save.";
        AppLog.Write($"Desktop ring: captured {(both ? "both eyes" : "one eye")} and the window of {game} ({window.PixelWidth} x {window.PixelHeight}).");
        DesktopRingStage.Focus();
    }

    // ------------------------------------------------------------------ saving

    private void UpdateDesktopRingSaveButtons()
    {
        var game = _appSettings.DesktopRingGame;
        DesktopRingSaveGame.Content = game.Length > 0 ? $"Save for {game}" : "Save for this game";
        DesktopRingSaveGame.ToolTip = (game.Length > 0 ? $"Only {game} takes it, ahead of the one for every game." : "The pictures do not say which game they are of.") +
            "\n\n**Live:** a running game takes it at once.";
        DesktopRingSaveGame.IsEnabled = _ringHaveBox && game.Length > 0 && _ringWindowPicture != null;
        DesktopRingSaveDefault.IsEnabled = _ringHaveBox && _ringWindowPicture != null;
    }

    private void OnDesktopRingSaveGame(object sender, RoutedEventArgs e) => SaveDesktopRing(_appSettings.DesktopRingGame);

    private void OnDesktopRingSaveDefault(object sender, RoutedEventArgs e) => SaveDesktopRing(DesktopRing.DefaultSection);

    private void SaveDesktopRing(string section)
    {
        if (!_ringHaveBox || section.Length == 0) return;
        var label = DesktopRing.IsDefault(section) ? "every game" : section;
        try
        {
            var all = DesktopRing.Load();
            // A new alignment for the game keeps its "ring only whenever it runs".
            var always = all.FirstOrDefault(a => string.Equals(a.Section, section, StringComparison.OrdinalIgnoreCase))?.RingOnly ?? false;
            all.RemoveAll(a => string.Equals(a.Section, section, StringComparison.OrdinalIgnoreCase));
            all.Add(new DesktopAlignment(section, _appSettings.DesktopRingEye == 0 ? 0 : 1, _ringCenterX, _ringCenterY, _ringHeight, RingWindowAspect, always));
            DesktopRing.Save(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopRingStatus.Text = $"Could not save: {ex.Message}";
            return;
        }
        var live = LiveLink.NotifySettingsChanged();
        DesktopRingStatus.Text = $"Saved for {label}." + (live ? " The running game takes it at once." : " A game takes it when it starts.");
        AppLog.Write($"Desktop ring: saved for {label}.");
        FillDesktopRingSaved();
        UpdateDesktopRingInUse();
    }

    private void DeleteDesktopRing(string section)
    {
        try
        {
            var all = DesktopRing.Load();
            all.RemoveAll(a => string.Equals(a.Section, section, StringComparison.OrdinalIgnoreCase));
            DesktopRing.Save(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopRingStatus.Text = $"Could not delete: {ex.Message}";
            return;
        }
        LiveLink.NotifySettingsChanged();
        DesktopRingStatus.Text = $"Deleted the one for {(DesktopRing.IsDefault(section) ? "every game" : section)}.";
        FillDesktopRingSaved();
        UpdateDesktopRingInUse();
    }

    /// <summary>A game's "ring only whenever it runs", switched on its saved row.</summary>
    private void SetDesktopRingAlways(string section, bool on)
    {
        try
        {
            var all = DesktopRing.Load();
            var index = all.FindIndex(a => string.Equals(a.Section, section, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            all[index] = all[index] with { RingOnly = on };
            DesktopRing.Save(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopRingStatus.Text = $"Could not save: {ex.Message}";
            FillDesktopRingSaved();
            return;
        }
        var live = LiveLink.NotifySettingsChanged();
        DesktopRingStatus.Text = on ? $"{section} always gets the ring alone." + (live ? " A running game changes at once." : "")
                                    : $"{section} follows the Ring only switch again.";
        AppLog.Write($"Desktop ring: {section} ring only whenever it runs: {(on ? "on" : "off")}.");
        UpdateDesktopRingInUse();
        RefreshStatus();
    }

    /// <summary>16:9 and the other usual shapes by name, anything else as a number.</summary>
    private static string AspectName(double aspect)
    {
        foreach (var (w, h) in new[] { (16, 9), (16, 10), (21, 9), (32, 9), (4, 3), (5, 4), (1, 1) })
        {
            if (Math.Abs(aspect - (double)w / h) < 0.02 * aspect) return $"{w}:{h}";
        }
        return aspect.ToString("0.##", CultureInfo.CurrentCulture) + ":1";
    }

    private void FillDesktopRingSaved()
    {
        DesktopRingSaved.Children.Clear();
        var all = DesktopRing.Load().OrderBy(a => DesktopRing.IsDefault(a.Section) ? 0 : 1).ThenBy(a => a.Section, StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0)
        {
            DesktopRingSaved.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Text = "Nothing saved yet. Until then the ring sits where it would on the Mirror page's crop box.",
            });
            return;
        }
        foreach (var alignment in all)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var delete = new Button
            {
                Style = (Style)FindResource("FlatButton"), Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = "\uE74D", FontSize = 13 },
                ToolTip = "Delete this one.\n\n**Live:** a running game drops it at once.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(delete, "Delete");
            var section = alignment.Section;
            delete.Click += (_, _) => DeleteDesktopRing(section);
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = DesktopRing.IsDefault(section) ? "Every game" : section, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Text = $"{(alignment.Eye == 0 ? "Left" : "Right")} eye, {AspectName(alignment.Aspect)} window, {alignment.Height * 100:0} % of the eye's height",
            });
            row.Children.Add(text);
            if (!DesktopRing.IsDefault(section))
            {
                var always = new CheckBox
                {
                    Content = "Ring only whenever it runs", MinWidth = 0, Margin = new Thickness(0, 4, 0, 0), IsChecked = alignment.RingOnly,
                    ToolTip = $"OBS gets the ring alone whenever {section} runs, even with the Ring only switch off. Other games follow the switch.\n\n**Live:** a running game changes at once.",
                };
                always.Click += (_, _) => SetDesktopRingAlways(section, always.IsChecked == true);
                text.Children.Add(always);
            }
            DesktopRingSaved.Children.Add(row);
        }
    }

    /// <summary>What OBS gets now, in a line under the switch.</summary>
    private void UpdateDesktopRingInUse()
    {
        if (DesktopRingInUse == null) return;
        var game = MirrorLive.ReadProducer() is { Kind: not 0 } producer ? producer.Program : null;
        var all = DesktopRing.Load();
        var forced = DesktopRing.AlwaysRingOnly(all, game);
        string text;
        if (_values.GetValueOrDefault("output_ring_only") != "1" && !forced)
        {
            var always = all.Where(a => a.RingOnly && !DesktopRing.IsDefault(a.Section)).Select(a => a.Section).ToList();
            text = "Off: OBS gets the game picture, framed on the Mirror page." + (always.Count > 0 ? $" Except {string.Join(", ", always)}: ring only whenever it runs." : "");
        }
        else if (game == null) text = "On. No VR game is running.";
        else
        {
            var name = game.Length > 0 ? game : "the game";
            text = forced ? $"On for {name}: its alignment says ring only whenever it runs. The ring lines up with its window."
                : DesktopRing.For(all, game) switch
                {
                    null => $"On. {name} has no alignment yet, so the ring sits where it would on the Mirror page's crop box.",
                    { } a when DesktopRing.IsDefault(a.Section) => $"On. The ring lines up with {name}'s window, by the alignment for every game.",
                    _ => $"On. The ring lines up with {name}'s window, by its own alignment.",
                };
        }
        DesktopRingInUse.Text = text;
    }
}
