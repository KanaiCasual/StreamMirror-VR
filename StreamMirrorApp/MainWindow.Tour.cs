using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace StreamMirror;

/// <summary>
/// The guided tour: one step per thing worth knowing, page by page. Each step opens its page, dims the window around the
/// control it is about and puts a card beside it with what it does. It starts by itself the first time the window opens
/// (AppSettings.TourSeen) and again from About > Take the tour. Esc ends it, the arrow keys and Enter step through it.
/// Placement follows the window's size and the page's layout (events), nothing runs on a timer.
/// </summary>
public partial class MainWindow
{
    /// <summary>A step: its page, its control, its card, and the section it opens while it shows (put back after).</summary>
    private sealed record TourStep(TabItem? Page, Func<FrameworkElement?> Target, string Title, string Text, Expander? Opens = null);

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
    // The step's control while it shows: its size and the scrolling around it place the ring and the card again.
    private FrameworkElement? _tourTarget;
    private ScrollViewer? _tourScroller;
    private bool _tourOpenedSection;

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
            new(null, () => null, "StreamMirror VR in two minutes",
                "This tour shows each page and what its main controls do.\n\nNext steps through it; Skip ends it. You can start it again any time from the About page."),
            new(StatusTab, () => Ancestor<Border>(StatusRows), "The mirror's parts",
                "The mirror layer takes the picture inside OpenXR games, the SteamVR helper inside SteamVR games, and the OBS plugin brings it into OBS.\n\nCyan is ready, grey is not needed on this PC. An amber or red part says what to do about it."),
            new(StatusTab, () => LiveText.Parent as FrameworkElement, "What the mirror is doing",
                "While you play, this line names the game the mirror shows and says whether your eye gaze arrives.\n\nIt answers most \"why is nothing happening\" questions."),
            new(CropTab, () => CropFrameBorder, "Your picture",
                "One eye of your headset view. The blue box on it is what OBS and the mirror window get.\n\nDrag it to move it, drag a corner or roll the mouse wheel to resize it. With the Free shape, drag a side to change the width or the height alone.\n\nThe app keeps this picture until you replace it with one of the buttons above."),
            new(CropTab, () => CropCapture, "Capture from headset",
                "The best picture to frame on is your view as you sit when you play.\n\nPress this, put the headset on, look straight ahead and press the key (Pause), or let a countdown take it (the arrow beside it)."),
            new(CropTab, () => Ancestor<Border>(CropEnabled), "The crop box",
                "Crop the picture switches the box on, and the shape beside it (16:9 for a stream) keeps its proportions.\n\nLock stops a stray click from moving it."),
            new(CropTab, () => ViewEdgesCard, "View edges",
                "The headset draws more than your eye sees through the lens.\n\nMeasure where your view ends once, in the headset. Then Crop to view edges makes the largest box that fits: no black lens corners."),
            new(CropTab, () => Ancestor<Border>(PanelOutput), "What goes out",
                "Which eye, how the picture is taken, the resolution OBS gets and the most pictures a second.\n\nEach Picture size choice names the resolution your crop box gets. Pick the one you record or stream at, 1920 x 1080 for 1080p: more only costs more.\n\nThe game's own flat panels always come along. Show other tools' panels brings kneeboards such as OpenKneeboard too."),
            new(CropTab, () => StabilizeExpander, "A calm picture",
                "Steady the picture takes small head movement and tracking jitter out. Tilt set to Level keeps the horizon level when you lean.\n\nThe green band on the picture is the room the box may move in.\n\nFollow my gaze, just above, moves the box up and down after your eyes.",
                StabilizeExpander),
            new(CropTab, () => Ancestor<WrapPanel>(CropProfile), "Profiles",
                "A profile keeps a framing under a name. New starts one from the default framing, Save as... copies the current one, and the ... button renames or deletes it.\n\nGive it a game's program file under For game (DCS.exe, say) and the mirror switches to it by itself when that game starts."),
            new(CropTab, () => Ancestor<DockPanel>(CropProfileAutoSave), "Saving a profile",
                "Changes show on the stream at once, but go into the profile only when you press Save, so trying something out never spoils it. This line turns orange while something is unsaved, and Revert goes back.\n\nSwitch on Save edits automatically to save every change as you make it; Revert then goes back to how the profile was when you opened it.\n\nOn any page, Ctrl+Z undoes one change and Ctrl+Y redoes it."),
            new(CropTab, () => MirrorToggle, "The mirror window",
                "For Discord and other programs that capture windows: a window with the same picture, behind your other windows. This button opens and closes it.\n\nWith the StreamMirror VR source in OBS you do not need it."),
            new(CropTab, () => MirrorWindowExpander, "Mirror window settings",
                "At the foot of this column: the monitor the window fills (or a set size, for Discord 1920 x 1080 say) and the most pictures a second it draws.\n\nA title bar is there for capture tools that only list windows with one.",
                MirrorWindowExpander),
            new(GazeTab, () => Ancestor<Border>(PanelGaze), "Where the gaze comes from",
                "The ring shows your viewers where you look. The headset's own eye tracking comes first; this sets the fallbacks.\n\nNo eye tracking? Choose Fixed centre, or switch the ring off on the Ring page.\n\nFurther down, a marker in the headset lets you check where the ring lands."),
            new(LookTab, () => LookHeaderBar, "The ring's look",
                "A preset is enough for most people.\n\nThe preview shows every change at once, and your headset shows none of it."),
            new(CoverTab, () => null, "Desktop cover",
                "For headsets that track with cameras: black over the game's window on your monitor, so a moving picture there cannot confuse the tracking.\n\nHeadsets with base stations do not need it."),
        };
        if (_navQuadViews)
        {
            steps.Add(new(QuadViewsTab, () => QuadViewsLayerOn.Parent as FrameworkElement, "Quad views",
                "Foveated rendering: the game draws a small sharp area where you look and the rest of the view at a lower resolution. That frees up graphics card time for a higher frame rate.\n\nIt works in games that offer quad views, such as DCS and VAIL.\n\nThis switch turns it on for every OpenXR game, from the game's next start."));
            steps.Add(new(QuadViewsTab, () => PanelQuadViews, "Why this quad views layer",
                "It is built on mbucchia's Quad-Views-Foveated and adds to it:\n" +
                "  - Direct3D 12 and Vulkan games too (VAIL; DCS, should it move to Vulkan)\n" +
                "  - no visible seam around the sharp area\n" +
                "  - steadier when your eyes jitter or blink\n" +
                "  - sharpening without halos\n" +
                "  - a diagnostics view in the headset\n\n" +
                "The settings under Live show in a running game at once; those under Next start wait for the game to start again.\n\n" +
                "Games it cannot help (OpenGL) run as usual instead of going black. Switched on, it takes over from the official layer if you had that."));
        }
        steps.Add(new(LayersTab, () => Ancestor<Border>(LayerRows), "OpenXR layers",
            "Every OpenXR add-on on this PC, in the order games load them, with a switch for each.\n\nIf the order breaks a rule the app knows, Fix order puts it right."));
        steps.Add(new(AboutTab, () => Ancestor<Border>(TourButton), "That's it",
            "Getting started opens a step-by-step guide to a first setup. Take the tour starts this tour again.\n\nLogs opens the log files to send with a bug report."));
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
        LeaveTourStep();
        _tourIndex = index;
        var step = _tour[index];
        if (step.Page != null) Tabs.SelectedItem = step.Page;
        // A folded section the step is about opens while the step shows.
        if (step.Opens is { IsExpanded: false } section)
        {
            section.IsExpanded = true;
            _tourOpenedSection = true;
        }
        _tourCount.Text = index == 0 ? "Tour" : $"{index} of {_tour.Count - 1}";
        _tourTitle.Text = step.Title;
        _tourText.Text = step.Text;
        _tourBack.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _tourNext.Content = index == 0 ? "Start" : index == _tour.Count - 1 ? "Done" : "Next";
        _tourSkip.Visibility = index == _tour.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        // The page has to be laid out before the control can be found on it.
        step.Target()?.BringIntoView();
        Dispatcher.InvokeAsync(() => FollowTourTarget(step.Target()), System.Windows.Threading.DispatcherPriority.Loaded);
        _tourNext.Focus();
    }

    /// <summary>
    /// Watches the step's control: while a section unfolds it grows, and the column scrolls to show it; both place the
    /// ring and the card again, so they end on the whole section.
    /// </summary>
    private void FollowTourTarget(FrameworkElement? target)
    {
        if (_tourIndex < 0) return;
        _tourTarget = target;
        if (target != null)
        {
            target.SizeChanged += OnTourTargetSizeChanged;
            _tourScroller = null;
            for (DependencyObject? at = VisualTreeHelper.GetParent(target); at != null && _tourScroller == null; at = VisualTreeHelper.GetParent(at))
            {
                _tourScroller = at as ScrollViewer;
            }
            if (_tourScroller != null) _tourScroller.ScrollChanged += OnTourScrolled;
            target.BringIntoView();
        }
        PlaceTour();
    }

    private void OnTourTargetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _tourTarget?.BringIntoView();
        PlaceTour();
    }

    private void OnTourScrolled(object sender, ScrollChangedEventArgs e) => PlaceTour();

    /// <summary>Lets go of the step's control, and folds again the section it opened.</summary>
    private void LeaveTourStep()
    {
        if (_tourTarget != null) _tourTarget.SizeChanged -= OnTourTargetSizeChanged;
        if (_tourScroller != null) _tourScroller.ScrollChanged -= OnTourScrolled;
        _tourTarget = null;
        _tourScroller = null;
        if (_tourOpenedSection && _tourIndex >= 0 && _tourIndex < _tour.Count && _tour[_tourIndex].Opens is { } section) section.IsExpanded = false;
        _tourOpenedSection = false;
    }

    private void EndTour()
    {
        LeaveTourStep();
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
