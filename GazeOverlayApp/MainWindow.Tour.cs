using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace GazeOverlay;

/// <summary>
/// The guided tour: one step per thing worth knowing, page by page. Each step opens its page, dims the window around the
/// control it is about and puts a card beside it with what it does. It starts by itself the first time the window opens
/// (AppSettings.TourSeen) and again from About > Take the tour. Esc ends it, the arrow keys and Enter step through it.
/// Placement follows the window's size and the page's layout (events), nothing runs on a timer.
/// </summary>
public partial class MainWindow
{
    private sealed record TourStep(TabItem? Page, Func<FrameworkElement?> Target, string Title, string Text);

    private List<TourStep> _tour = [];
    private int _tourIndex = -1;
    private readonly ShapePath _tourShade = new() { Fill = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)) };
    private readonly Border _tourRing = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
    private readonly Border _tourCard = new() { Width = 360, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1) };
    private readonly TextBlock _tourCount = new() { FontSize = 11 };
    private readonly TextBlock _tourTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _tourText = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
    private readonly Button _tourBack = new() { Content = "Back", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _tourNext = new() { MinWidth = 76 };
    private readonly Button _tourSkip = new() { Content = "Skip the tour" };

    /// <summary>The nearest T around a control, by the page's own structure (works before the page was ever shown).</summary>
    private static T? Ancestor<T>(DependencyObject? item) where T : DependencyObject
    {
        for (var at = item != null ? LogicalTreeHelper.GetParent(item) : null; at != null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is T found) return found;
        }
        return null;
    }

    private List<TourStep> TourSteps()
    {
        var steps = new List<TourStep>
        {
            new(null, () => null, "VR Gaze Mirror in two minutes",
                "This tour shows each page and what its main controls do. Next steps through it; Skip ends it. You can start it again any time from the About page."),
            new(StatusTab, () => Ancestor<Border>(StatusRows), "The mirror's parts",
                "The mirror layer takes the picture inside OpenXR games, the SteamVR helper inside SteamVR games, and the OBS plugin brings it into OBS. Green is ready, grey is not needed on this PC. A red or orange part says what to do about it."),
            new(StatusTab, () => LiveText.Parent as FrameworkElement, "What the mirror is doing",
                "While you play, this line names the game the mirror shows and says whether your eye gaze arrives. It answers most \"why is nothing happening\" questions."),
            new(CropTab, () => CropFrameBorder, "Your picture",
                "One eye of your headset view. The blue box on it is what OBS and the mirror window get: drag it to move it, drag a corner or roll the mouse wheel to resize it. The app keeps this picture until you replace it with one of the buttons above."),
            new(CropTab, () => CropCapture, "Capture from headset",
                "The best picture to frame on is your view as you sit when you play. Press this, put the headset on, look straight ahead and press the key (Pause), or let a countdown take it (the arrow beside it)."),
            new(CropTab, () => Ancestor<Border>(CropEnabled), "The crop box",
                "Crop the picture switches the box on, and the shape beside it (16:9 for a stream) keeps its proportions. Lock stops a stray click from moving it."),
            new(CropTab, () => ViewEdgesCard, "View edges",
                "The headset draws more than your eye sees through the lens. Measure where your view ends once, in the headset, then Crop to view edges makes the largest box that fits: no black lens corners."),
            new(CropTab, () => Ancestor<Border>(PanelOutput), "What goes out",
                "Which eye, how the picture is taken, the largest size and the most pictures a second. Pick 1920 if OBS records 1080p: it saves work. Show tool panels brings kneeboards such as OpenKneeboard along."),
            new(CropTab, () => StabilizeExpander, "A calm picture",
                "Steady the picture takes small head movement and tracking jitter out; Tilt set to Level keeps the horizon level when you lean. Follow my gaze, just above, moves the box up and down after your eyes."),
            new(CropTab, () => Ancestor<WrapPanel>(CropProfile), "Profiles",
                "Save as... keeps this framing under a name. Give it a game's program file (DCS.exe, say) and the mirror switches to it by itself when that game starts."),
            new(CropTab, () => MirrorToggle, "The mirror window",
                "For Discord and other programs that capture windows: a window with the same picture, behind your other windows. With the Gaze Mirror source in OBS you do not need it."),
            new(GazeTab, () => Ancestor<Border>(PanelGaze), "Where the gaze comes from",
                "The ring shows your viewers where you look. The headset's own eye tracking comes first; this sets the fallbacks. No eye tracking? Choose Fixed centre, or switch the ring off on the Ring page. Further down, a marker in the headset lets you check where the ring lands."),
            new(LookTab, () => LookHeaderBar, "The ring's look",
                "A preset is enough for most people. The preview shows every change at once, and your headset shows none of it."),
            new(CoverTab, () => null, "Desktop cover",
                "For headsets that track with cameras: black over the game's window on your monitor, so a moving picture there cannot confuse the tracking. Headsets with base stations do not need it."),
        };
        if (_navQuadViews)
        {
            steps.Add(new(QuadViewsTab, () => null, "Quad Views",
                "Quad-Views-Foveated's own settings: a game renders sharp where you look and cheaper elsewhere. Apply writes them, and a game takes them at its next start."));
        }
        steps.Add(new(LayersTab, () => Ancestor<Border>(LayerRows), "OpenXR layers",
            "Every OpenXR add-on on this PC, in the order games load them, with a switch for each. If the order breaks a rule the app knows, Fix order puts it right."));
        steps.Add(new(AboutTab, () => Ancestor<Border>(TourButton), "That's it",
            "Getting started opens a step-by-step guide to a first setup, Take the tour starts this tour again, and Logs opens the log files to send with a bug report."));
        return steps;
    }

    private void BuildTourLayer()
    {
        _tourCard.Background = (Brush)FindResource("Panel");
        _tourCard.BorderBrush = (Brush)FindResource("Line");
        _tourRing.BorderBrush = (Brush)FindResource("Accent");
        _tourCount.Foreground = (Brush)FindResource("Muted");
        _tourNext.Style = (Style)FindResource("AccentButtonStyle");
        _tourSkip.Style = (Style)FindResource("FlatButton");
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_tourSkip, Dock.Left);
        DockPanel.SetDock(_tourNext, Dock.Right);
        DockPanel.SetDock(_tourBack, Dock.Right);
        buttons.Children.Add(_tourSkip);
        buttons.Children.Add(_tourNext);
        buttons.Children.Add(_tourBack);
        _tourCard.Child = new StackPanel { Children = { _tourCount, _tourTitle, _tourText, buttons } };
        System.Windows.Automation.AutomationProperties.SetName(_tourCard, "Tour");
        TourLayer.Children.Add(_tourShade);
        TourLayer.Children.Add(_tourRing);
        TourLayer.Children.Add(_tourCard);
        _tourNext.Click += (_, _) => TourNext();
        _tourBack.Click += (_, _) => TourGo(_tourIndex - 1);
        _tourSkip.Click += (_, _) => EndTour();
        TourLayer.SizeChanged += (_, _) => PlaceTour();
        PreviewKeyDown += (_, e) =>
        {
            if (_tourIndex < 0) return;
            if (e.Key == Key.Escape) EndTour();
            else if (e.Key is Key.Right or Key.Enter) TourNext();
            else if (e.Key == Key.Left) TourGo(_tourIndex - 1);
            else return;
            e.Handled = true;
        };
        // The first time the window is shown, the tour starts by itself.
        Loaded += (_, _) =>
        {
            if (!_appSettings.TourSeen && !App.Headless) Dispatcher.InvokeAsync(() => StartTour(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    internal int TourStepCount => _tour.Count;

    private void OnTakeTour(object sender, RoutedEventArgs e) => StartTour();

    internal void StartTour()
    {
        _tour = TourSteps();
        TourLayer.Visibility = Visibility.Visible;
        TourGo(0);
    }

    internal void TourNext()
    {
        if (_tourIndex + 1 >= _tour.Count) EndTour();
        else TourGo(_tourIndex + 1);
    }

    private void TourGo(int index)
    {
        if (index < 0 || index >= _tour.Count) return;
        _tourIndex = index;
        var step = _tour[index];
        if (step.Page != null) Tabs.SelectedItem = step.Page;
        _tourCount.Text = index == 0 ? "Tour" : $"{index} of {_tour.Count - 1}";
        _tourTitle.Text = step.Title;
        _tourText.Text = step.Text;
        _tourBack.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _tourNext.Content = index == 0 ? "Start" : index == _tour.Count - 1 ? "Done" : "Next";
        _tourSkip.Visibility = index == _tour.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        // The page has to be laid out before the control can be found on it.
        step.Target()?.BringIntoView();
        Dispatcher.InvokeAsync(PlaceTour, System.Windows.Threading.DispatcherPriority.Loaded);
        _tourNext.Focus();
    }

    private void EndTour()
    {
        _tourIndex = -1;
        TourLayer.Visibility = Visibility.Collapsed;
        if (_appSettings.TourSeen || App.Headless) return;
        _appSettings.TourSeen = true;
        _appSettings.Save();
    }

    /// <summary>Dims the window except the step's control, rings it, and puts the card beside it where it fits.</summary>
    private void PlaceTour()
    {
        if (_tourIndex < 0 || _tourIndex >= _tour.Count) return;
        double width = TourLayer.ActualWidth, height = TourLayer.ActualHeight;
        if (width < 1 || height < 1) return;
        var target = _tour[_tourIndex].Target();
        Rect? hole = null;
        if (target is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 })
        {
            var bounds = target.TransformToVisual(TourLayer).TransformBounds(new Rect(target.RenderSize));
            bounds.Inflate(6, 6);
            bounds.Intersect(new Rect(0, 0, width, height));
            if (!bounds.IsEmpty && bounds.Width > 4 && bounds.Height > 4) hole = bounds;
        }
        var everything = new RectangleGeometry(new Rect(0, 0, width, height));
        _tourShade.Data = hole is { } cut
            ? new CombinedGeometry(GeometryCombineMode.Exclude, everything, new RectangleGeometry(cut, 8, 8))
            : everything;
        _tourRing.Visibility = hole != null ? Visibility.Visible : Visibility.Collapsed;
        if (hole is { } ring)
        {
            Canvas.SetLeft(_tourRing, ring.X);
            Canvas.SetTop(_tourRing, ring.Y);
            _tourRing.Width = ring.Width;
            _tourRing.Height = ring.Height;
        }

        _tourCard.Width = Math.Min(360, width - 24);
        _tourCard.Measure(new Size(_tourCard.Width, double.PositiveInfinity));
        double cardWidth = _tourCard.Width, cardHeight = _tourCard.DesiredSize.Height, gap = 14, edge = 12;
        double x, y;
        if (hole is not { } spot)
        {
            (x, y) = ((width - cardWidth) / 2, (height - cardHeight) / 2);
        }
        else if (spot.Right + gap + cardWidth <= width - edge)
        {
            (x, y) = (spot.Right + gap, spot.Top);
        }
        else if (spot.Left - gap - cardWidth >= edge)
        {
            (x, y) = (spot.Left - gap - cardWidth, spot.Top);
        }
        else if (spot.Bottom + gap + cardHeight <= height - edge)
        {
            (x, y) = (spot.Left, spot.Bottom + gap);
        }
        else if (spot.Top - gap - cardHeight >= edge)
        {
            (x, y) = (spot.Left, spot.Top - gap - cardHeight);
        }
        else
        {
            // A control as large as the window (the picture in a small window): the card goes over its foot.
            (x, y) = (spot.Left + (spot.Width - cardWidth) / 2, height - cardHeight - edge);
        }
        Canvas.SetLeft(_tourCard, Math.Clamp(x, edge, Math.Max(edge, width - cardWidth - edge)));
        Canvas.SetTop(_tourCard, Math.Clamp(y, edge, Math.Max(edge, height - cardHeight - edge)));
    }
}
