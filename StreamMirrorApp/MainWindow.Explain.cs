using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StreamMirror;

/// <summary>
/// The explanation at the top of each page: what the page is for, what its parts do and how to use them, in full and
/// on the page itself (a tooltip is found by nobody who does not already know it is there).
///
/// An explanation is open the first time its page is looked at and folded from then on, so that it is read once and
/// does not stand in the way afterwards. A click on its heading opens or folds it, and that choice is kept: one opened
/// by hand stays open. What has been seen, opened and folded is remembered in the app's preferences.
/// </summary>
public partial class MainWindow
{
    /// <summary>One paragraph: a few words that name its subject (may be empty), and the text.</summary>
    private readonly record struct Part(string Lead, string Text);

    private const string HowThisPageWorks = "How this page works";
    private static readonly Part AboutSliders = new("Sliders.", "Click a value to type one. Double-click a slider for its default.");

    private readonly List<(TabItem Page, string Key, Action<bool> Show)> _explanations = [];
    private TabItem? _pageShown;

    private void BuildExplanations()
    {
        AddExplanation(StatusTab, ExplainStatus, "status", HowThisPageWorks,
            new("The line at the top", "names the game the mirror shows right now and says whether eye gaze arrives. A grey dot means no VR game is running; the mirror starts with the game."),
            new("The picture", "is the last one the app saved, cut to your crop box: what OBS gets. It is not a live view.\n\nThe light in its corner: **Reaching OBS** (or the mirror window) while the picture gets there, **Not reaching** when one of them should get it and does not, **Mirroring** while a game runs and nothing takes the picture, and **Waiting for a game** otherwise."),
            new("Beside it", "the game, the picture's size and rate, the eye, the capture, the crop and the steadying.\n\n**Where the picture goes** says for OBS and the mirror window: closed, ready, receiving the picture, or not receiving it."),
            new("Three steps", "take a new install to a picture in OBS. Each step ticks itself off once the app has seen it happen, and the card goes away after the third."),
            new("Parts.", "The mirror layer makes the picture inside OpenXR games. The SteamVR helper does the same for games that use SteamVR directly; it starts and stops with SteamVR. The OBS plugin is the \"StreamMirror VR\" source that shows the picture in OBS.\n\nQuad views are optional and have no part in the mirror.\n\nEach tile's word says how the part is: cyan is fine, grey is not needed on this PC, amber or red says what to do about it."),
            new("OpenXR API layers.", "A game loads its layers in a fixed order, and the mirror layer has to sit below any layer that draws into the picture, so that it sees the finished picture.\n\nThe link above the tiles sums up that order and turns orange on a problem. It opens the Layers page, where you switch a layer on or off, move one, or press Fix order. Games use a change from their next start."));

        AddExplanation(CropTab, ExplainMirror, "mirror", HowThisPageWorks,
            new("Live.", "Every change on this page reaches the stream and a running game at once."),
            new("The picture", "is one eye's whole image as the game drew it. The blue box on it is the part OBS and the mirror window get.\n\nDrag inside the box to move it; drag a corner or roll the mouse wheel over it to resize it. With the Free shape, a side drags on its own: the width or the height alone. With the picture selected, the arrow keys move the box and + and - resize it."),
            new("What OBS gets", "at the top of the settings shows the box's part of the picture as you move it, with the light while a game is mirrored."),
            new("Taking a picture.", "Refresh picture asks the running game for one.\n\nCapture from headset takes it the way you sit in play: arm it, put the headset on, look straight ahead and press the key, or let the countdown run. The buttons sit in the row above the picture."),
            new("View edges.", "The headset draws more than your eye sees through the lens, and the crop should cover the part you see.\n\nMeasure in headset turns the headset black and shows a flashing white block at one side. Push it outwards with the arrow keys until it is gone from your view, press Enter, and the next side comes.\n\nA dashed box on the picture then marks the part your eye can see, and Crop to view edges fits the crop box to it."),
            new("Crop box and Picture.", "Crop the picture switches the box on, and the shape next to it locks the box's proportions.\n\nPicture chooses the eye and caps the size and the rate of the picture that goes out, for OBS and the mirror window alike."),
            new("Follow my gaze.", "A 16:9 box covers about half the height of the eye image, so a look down at your knees leaves the frame. With this on, the box glides up or down to keep your target in it."),
            new("Steady the picture.", "The box moves against small head movement and tracking jitter, and turns against a sideways tilt of your head, which takes them out of the picture. It holds back no frames, so it adds no delay."),
            new("Mirror window.", "A window with the same picture for programs that capture windows, such as Discord. With the StreamMirror VR source in OBS you do not need it."),
            new("Profile.", "A profile keeps a framing under a name. Give it a game's program file and the mirror applies it at that game's start. New starts one from the default framing, Save as copies the current one.\n\nChanges reach the stream at once and go into the profile at Save; Revert goes back. Save edits automatically saves every change as you make it, and Revert then goes back to how the profile was when you opened it.\n\nCtrl+Z undoes the last change on this page, Ctrl+Y redoes it."),
            AboutSliders);

        AddExplanation(GazeTab, ExplainGaze, "gaze", HowThisPageWorks,
            new("", "The ring shows your viewers where you look. This page sets the source of your gaze and the spot in the picture the ring lands on.\n\nIf the mirror is all you want, you can skip the two ring pages."),
            new("Live.", "Every change on this page reaches the stream and a running game at once."),
            new("Gaze from.", "The headset's own eye tracking comes first: exact angles and nothing to set up.\n\nSome eye-tracking software takes the eye cameras for itself (SRanibro) or feeds VRChat and nothing else, and the headset then has no gaze to give. In that case the ring falls back to raw gaze sent to the helper's gaze input port over OSC (SRanibro can send it, so can a driver of your own) or to the eyes of your VRChat avatar.\n\nFixed centre is for headsets without eye tracking: the ring stays where straight ahead lands."),
            new("Calibrate VRChat gaze", "is for the VRChat fallback alone. The avatar's eye values are no angles, so the helper measures once how they relate to where you look.\n\nWith VRChat running, follow the target in the headset with your eyes for about 45 seconds. Thorough takes about two minutes and fits a finer map."),
            new("Where the ring lands.", "Most setups need no change here. If the ring sits beside your target, switch on the marker: a small reticle appears in the headset at the ring's place.\n\nLook at something small, nudge with the two sliders until the reticle sits on it, and switch the marker off again.\n\nThe viewing distance matters because the mirror shows one eye, and your two eyes see close things in different places."));

        AddExplanation(LookTab, ExplainRing, "ring", HowThisPageWorks,
            new("", "The look of the ring on your stream. Your headset shows none of it.\n\nThe preview shows each change at once: on your last picture from the Mirror page, at the ring's own size, or over a cockpit, terrain, sky or black."),
            new("Live.", "Every change on this page reaches the stream and a running game at once."),
            new("Presets and My presets.", "A preset sets the whole look with one click. My presets holds looks of your own by name: Save as... keeps the current look under a name, and the list brings it back. Changes apply at once, but a preset keeps them only when you press Save.\n\nFor most people a preset and the first card are all the ring needs; the three sections under it fold away."),
            new("Ring.", "Style picks the mark. Ghost is a thin ring that stretches into a teardrop as your eyes move; the others are a plain ring, a glow, a dot, a spotlight that darkens the rest, a bubble or blob with a trail, and a heatmap.\n\nColour and size shape it."),
            new("More of the look.", "Line thickness and edge softness, a glow around the line, and the blending: Light adds the ring like a glow and the scene shows through; Paint draws it on top like ink, easier to see and hiding what lies behind.\n\nSolidity, the fill and the dark outline keep the ring readable against a bright sky."),
            new("Tail.", "The tail shows your viewers where your eyes came from: with the Ghost style a teardrop that stretches behind the ring as your eyes move and closes as they rest.\n\nStrength, lag and the longest length shape it, and Tail follows fixes it to the world (the choice for VR) or to the screen.\n\nThe heatmap's warm-up and cool-down set the stare time that turns a spot red and the time the spot takes to fade."),
            new("Motion.", "Raw eye tracking jitters, and eyes close in a blink. Adaptive smoothing holds the ring still while your eye rests and lets it jump as you look elsewhere.\n\nDwell tightens the ring after a held gaze, so viewers can tell reading a gauge from passing over it.\n\nThe last three settings say what the ring does in a blink and when tracking counts as lost."),
            AboutSliders);

        AddExplanation(CoverTab, ExplainCover, "cover", "For headsets with camera-based tracking (inside-out, SLAM)",
            new("", "A headset with camera-based tracking holds its place in the room by anchors: fixed points its cameras pick out around you, such as corners, edges and patterns on walls and furniture.\n\nIt works out how your head moved from the way those points shift between one camera picture and the next. For that it needs points that stand still."),
            new("", "A monitor that shows the game gives it points that move. The picture is bright and full of sharp detail, so the cameras find plenty of points in it, and it turns as you turn your head. If the headset takes those points for anchors, it places your head wrong: the view can drift, shake or jump.\n\nThe effect grows with the share of the cameras' view the monitor fills: close to the screen in a racing or flight rig, with several monitors, or in a dim room where the monitor is the brightest thing in sight."),
            new("", "The cover puts black over the game's window, so the cameras find no moving points there and keep to the room. It covers the game's own window and nothing else.\n\nIt stays while the game is the window in front: bring another window to the front and the cover goes at once."),
            new("Live.", "Every change on this page applies at once."),
            new("", "A headset tracked by base stations (Lighthouse) does not look at the room and has no use for the cover."));

        AddExplanation(QuadViewsTab, ExplainQuadViews, "quadviews", HowThisPageWorks,
            new("", "Quad views make a game render a small sharp region where you look and the rest of the view at a low resolution, which saves graphics card time.\n\nStreamMirror VR's quad views layer (built on mbucchia's Quad-Views-Foveated) does it for Direct3D 11, Direct3D 12 and Vulkan games. The switch at the top turns it on or off; on, it also turns the official Quad-Views-Foveated off, if that is installed."),
            new("Live and Next start.", "Every change is saved as you make it. Settings under Live show in a running game at once. Settings under Next start (resolution, Turbo) wait for the game to start again.\n\nThe lines at the top give the pixels per frame."),
            new("Checking.", "Show diagnostics in the headset tints the zones and marks your gaze while you play.\n\nThe layer stays out of games it cannot help (OpenGL); its log says why."),
            new("Focus", "sets the size of the sharp region as a share of the field of view, moves it up or down, and sets the width of the blend into the surround. Without eye tracking the region stays in the middle of the view."),
            new("Resolution", "sets the pixel count of the sharp region and of the surround; most of the saving comes from the surround. The presets set the sliders, and so does My presets: Save as... keeps the sliders under a name of yours, and the list brings them back. A preset keeps later changes only when you press Save."),
            AboutSliders);

        // A page that has been looked at folds its explanation as the user moves on (or closes the app).
        _pageShown = Tabs.SelectedItem as TabItem;
        Tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
            LeavePage(_pageShown);
            _pageShown = Tabs.SelectedItem as TabItem;
        };
        Closing += (_, _) => LeavePage(_pageShown);
    }

    /// <summary>Open: by the user's own hand, or because the page has not been looked at yet (and was not folded by hand).</summary>
    private bool ExplanationOpen(string key) =>
        _appSettings.OpenExplanations.Contains(key) || !_appSettings.SeenExplanations.Contains(key) && !_appSettings.FoldedExplanations.Contains(key);

    private void LeavePage(TabItem? page)
    {
        if (page == null || App.Headless) return; // A self-test or a screenshot run leaves the preferences alone.
        foreach (var (_, key, show) in _explanations.Where(x => ReferenceEquals(x.Page, page)))
        {
            if (_appSettings.SeenExplanations.Contains(key)) continue;
            _appSettings.SeenExplanations.Add(key);
            _appSettings.Save();
            show(ExplanationOpen(key));
        }
    }

    /// <summary>The card: a heading that opens or folds it, and the paragraphs.</summary>
    private void AddExplanation(TabItem page, Panel host, string key, string title, params Part[] parts)
    {
        var muted = (Brush)FindResource("Muted");
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var part in parts)
        {
            var paragraph = new TextBlock { Style = (Style)FindResource("Hint"), FontSize = 12, Margin = new Thickness(0, 0, 0, body.Children.Count == parts.Length - 1 ? 0 : 6) };
            // What the paragraph is about, a little brighter than the rest, so the eye finds the part it is after.
            if (part.Lead.Length > 0) paragraph.Inlines.Add(new Run(part.Lead + " ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text") });
            paragraph.Inlines.Add(new Run(part.Text));
            body.Children.Add(paragraph);
        }

        // The heading is a button, so the keyboard reaches it and a screen reader says what it does.
        var arrow = new TextBlock { Style = (Style)FindResource("Icon"), FontSize = 9, Foreground = muted, Width = 15 };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(arrow);
        label.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0) });
        var heading = new Button { Style = (Style)FindResource("FlatButton"), Content = label, ToolTip = "Opens this explanation or folds it away" };

        void Show(bool open)
        {
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "" : "";
            System.Windows.Automation.AutomationProperties.SetName(heading, title + (open ? ", open: fold it away" : ", folded: open it"));
        }
        Show(ExplanationOpen(key));
        heading.Click += (_, _) =>
        {
            var open = body.Visibility != Visibility.Visible;
            Show(open);
            _appSettings.OpenExplanations.Remove(key);
            _appSettings.FoldedExplanations.Remove(key);
            (open ? _appSettings.OpenExplanations : _appSettings.FoldedExplanations).Add(key);
            _appSettings.Save();
        };
        _explanations.Add((page, key, Show));

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(body);
        host.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 10, 14, 11), Child = content });
    }
}
