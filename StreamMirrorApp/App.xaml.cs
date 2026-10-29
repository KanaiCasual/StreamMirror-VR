using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace StreamMirror;

/// <summary>
/// The settings app. It never installs anything, never asks for administrator rights and never touches Program Files or
/// the system registry - the MSI does all of that. All it writes is two settings files in the user's profile: the ring's,
/// and (only when Apply is clicked on the Quad Views tab) Quad-Views-Foveated's.
/// One user-driven exception: ticking a layer on or off on the Status tab. The app itself still never runs elevated -
/// it asks Windows (UAC) to let the system's own reg.exe change that single value (see LayerStatus.SetEnabledAsync).
/// </summary>
public partial class App : Application
{
    /// <summary>True for --selftest and --screenshots: nothing about the window is remembered from those runs.</summary>
    public static bool Headless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length >= 1 && args[0] == "--order-layers")
        {
            // The installer's last step, elevated, with no window: no single-instance check, as the app may be open.
            AppLog.Start(false);
            LayerOrder.ApplyAfterInstall();
            Shutdown(0);
            return;
        }
        if (args.Length >= 1 && args[0] == "--fix-startup")
        {
            // The installer's step as the signed-in user: the data of before 3.0 comes over, and Start with Windows names
            // this exe. No window and no single-instance check.
            AppSettings.TakeOverOldName();
            AppLog.Start(false);
            StreamMirror.MainWindow.FixStartupAfterUpdate();
            Shutdown(0);
            return;
        }
        Headless = args.Length >= 1 && args[0] is "--selftest" or "--screenshots" or "--cover-dummy";
        // Before the log starts: a second copy must not touch the running one's log.
        if (!Headless && !TakeSingleInstance())
        {
            Shutdown(0);
            return;
        }
        if (!Headless) AppSettings.TakeOverOldName();
        AppLog.Start(Headless);
        AppLog.Write($"StreamMirror VR {UpdateChecker.CurrentVersionText} starting" + (args.Length > 0 ? " with " + string.Join(" ", args) : "") +
                     $"; app folder {AppContext.BaseDirectory}; settings folder {AppSettings.Folder}");
        // Anything that would otherwise close the app silently is written down first; Windows' own crash handling still follows.
        DispatcherUnhandledException += (_, error) => AppLog.Write("Unhandled error on the UI thread", error.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => AppLog.Write("Unhandled error", error.ExceptionObject as Exception ?? new Exception(error.ExceptionObject?.ToString() ?? "unknown"));
        TaskScheduler.UnobservedTaskException += (_, error) => AppLog.Write("Unobserved task error", error.Exception);

        if (args.Length >= 1 && args[0] == "--cover-dummy")
        {
            // Started by the self-test: a stand-in for a game's window, for the desktop cover to cover.
            ShowCoverDummy();
            return;
        }

        if (args.Length >= 2 && args[0] == "--selftest")
        {
            Shutdown(SelfTest(args[1]));
            return;
        }

        if (args.Length >= 2 && args[0] == "--screenshots")
        {
            // Development aid: renders every tab of the real window to PNG files, off-screen.
            var outputDir = args[1];
            Directory.CreateDirectory(outputDir);
            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false };
            // Optional window size (--screenshots <dir> <width> <height>), to check the layout when it is docked small.
            if (args.Length >= 4 && double.TryParse(args[2], out var width) && double.TryParse(args[3], out var height))
            {
                window.Width = width;
                window.Height = height;
            }
            window.Show();
            window.ShowCropTestPicture();
            window.Dispatcher.InvokeAsync(() =>
            {
                void Snapshot(string name)
                {
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var root = (FrameworkElement)window.Content;
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                        context.DrawRectangle((System.Windows.Media.Brush)window.FindResource("WindowBack"), null, bounds);
                        // Unscaled, from the top left: a brush that fills would stretch the page when nothing is drawn in its right margin.
                        context.DrawRectangle(new System.Windows.Media.VisualBrush(root) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, bounds);
                    }
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outputDir, $"{name}.png"));
                    encoder.Save(file);
                }
                for (var i = 0; i < window.Tabs.Items.Count; i++)
                {
                    window.Tabs.SelectedIndex = i;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    // Sections that open do so with a short animation: with them open (STREAMMIRROR_SCREENSHOT_EXPANDED=1), wait for it to end.
                    if (Environment.GetEnvironmentVariable("STREAMMIRROR_SCREENSHOT_EXPANDED") == "1")
                    {
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        wait.Tick += (_, _) => { wait.Stop(); frame.Continue = false; };
                        wait.Start();
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                    }
                    var root = (FrameworkElement)window.Content;
                    File.AppendAllText(Path.Combine(outputDir, "layout.txt"),
                        $"tab{i}: window {window.ActualWidth:0}x{window.ActualHeight:0}, root {root.ActualWidth:0}, tabs {window.Tabs.ActualWidth:0}, preview {window.RingPanel.Visibility} {window.RingPanel.ActualWidth:0}, column {window.PreviewColumn.ActualWidth:0} ({window.PreviewColumn.Width}), inner grid {((FrameworkElement)window.Tabs.Parent).ActualWidth:0}, tabs margin {window.Tabs.Margin}\n");
                    Snapshot($"tab{i}");
                }
                // STREAMMIRROR_SCREENSHOT_TOUR=1: the guided tour, one picture per step.
                if (Environment.GetEnvironmentVariable("STREAMMIRROR_SCREENSHOT_TOUR") == "1")
                {
                    window.StartTour();
                    for (var step = 0; step < window.TourStepCount; step++)
                    {
                        // A section the step opens unfolds with a short animation: wait for it to end.
                        var settle = new System.Windows.Threading.DispatcherFrame();
                        var settleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        settleTimer.Tick += (_, _) => { settleTimer.Stop(); settle.Continue = false; };
                        settleTimer.Start();
                        System.Windows.Threading.Dispatcher.PushFrame(settle);
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Snapshot($"tour{step:00}");
                        window.TourNext();
                    }
                }
                // STREAMMIRROR_SCREENSHOT_PROFILES=1 (with STREAMMIRROR_PROFILES_FILE pointing at a scratch file): exercise the crop profiles on the Mirror tab.
                if (Environment.GetEnvironmentVariable("STREAMMIRROR_SCREENSHOT_PROFILES") == "1")
                {
                    window.Tabs.SelectedItem = window.CropTab;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    File.WriteAllText(Path.Combine(outputDir, "profiles.txt"), window.ExerciseProfiles(Snapshot));
                }
                // STREAMMIRROR_SCREENSHOT_PRESETS=1 (with STREAMMIRROR_APP_FILE pointing at a scratch file): the Ring and Quad Views presets.
                if (Environment.GetEnvironmentVariable("STREAMMIRROR_SCREENSHOT_PRESETS") == "1")
                {
                    foreach (var (page, tab) in new[] { ("ring", (System.Windows.Controls.TabItem)window.LookTab), ("quadviews", window.QuadViewsTab) })
                    {
                        window.Tabs.SelectedItem = tab;
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        File.WriteAllText(Path.Combine(outputDir, $"presets-{page}.txt"), window.ExercisePresets(page, Snapshot));
                    }
                    File.WriteAllText(Path.Combine(outputDir, "quick-switches.txt"), window.ExerciseQuickSwitches());
                }
                Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        var main = new MainWindow();
        main.ShowOrStartInTray();
        // A later start of the app asks this one to come forward; the thread sleeps until it does.
        new Thread(() =>
        {
            while (_showRequest!.WaitOne()) main.Dispatcher.BeginInvoke(main.ShowFromTray);
        }) { IsBackground = true, Name = "Show request" }.Start();
    }

    private const string SingleInstanceName = "StreamMirror.App.Single", ShowRequestName = "StreamMirror.App.Show";
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showRequest;

    /// <summary>One copy of the app per sign-in. A second start hands over to the running copy, which comes forward (out
    /// of the tray too), and leaves.</summary>
    private static bool TakeSingleInstance()
    {
        _singleInstance = new Mutex(true, SingleInstanceName, out var first);
        if (first)
        {
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
            return true;
        }
        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: the running copy may bring its window to the front.
            using var show = EventWaitHandle.OpenExisting(ShowRequestName);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The running copy is still starting; its window appears by itself.
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>The crop box arithmetic, and the picture request answered by a stand-in for the mirror layer.</summary>
    private static bool SelfTestCrop(StreamWriter report)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        var box = new CropBox { Aspect = 16.0 / 9.0, ImageAspect = 1, Height = 0.5 };
        var (w1, h1) = box.Size();
        box.Height = 1;                                   // a full-height 16:9 box does not fit a square image
        var (w2, h2) = box.Size();
        box.Height = 0.5; box.MoveTo(0.95, 0.02);         // pushed into the top right corner
        var corner = box.Rect();
        var vertical = new CropBox { Aspect = 9.0 / 16.0, ImageAspect = 1, Height = 1 };
        var resized = new CropBox { Aspect = 1, ImageAspect = 1 };
        resized.ResizeFromCorner(0.2, 0.2, 0.9, 0.5);     // pointer went further across than down: the square follows it
        var free = new CropBox { Aspect = 0, ImageAspect = 1 };
        free.ResizeFromCorner(0.1, 0.1, 0.6, 0.3);
        // Room kept free for the picture steadying: the box cannot enter it, whatever is done to it.
        var roomy = new CropBox { Aspect = 16.0 / 9.0, ImageAspect = 1, Height = 0.4, Margin = 0.02 };
        roomy.MoveTo(0, 0);                               // pushed against the left wall and the top
        var pushed = roomy.Rect();
        roomy.Height = 1; roomy.Normalize();              // "Largest"
        var largest = roomy.Rect();
        var roomyResize = new CropBox { Aspect = 1, ImageAspect = 1, Margin = 0.05 };
        roomyResize.ResizeFromCorner(0.5, 0.5, 1.5, 1.5); // dragged far out of the picture
        // Free shape, its sides one at a time: a box from 0.2 to 0.6 across and 0.3 to 0.6 down.
        var sides = new CropBox { Aspect = 0, ImageAspect = 1, FreeWidth = 0.4, Height = 0.3, CenterX = 0.4, CenterY = 0.45 };
        sides.ResizeFromSide(CropBox.Side.Right, 0.8);    // right side out to 0.8
        var widened = sides.Rect();
        sides.ResizeFromSide(CropBox.Side.Top, 0.1);      // top side up to 0.1
        var heightened = sides.Rect();
        sides.ResizeFromSide(CropBox.Side.Left, 0.95);    // left side dragged past the right one
        var squeezed = sides.Rect();
        var marginSides = new CropBox { Aspect = 0, ImageAspect = 1, FreeWidth = 0.4, Height = 0.4, Margin = 0.05 };
        marginSides.ResizeFromSide(CropBox.Side.Bottom, 1.5); // dragged far out of the picture
        var fixedShape = new CropBox { Aspect = 16.0 / 9.0, ImageAspect = 1, Height = 0.3 };
        var fixedBefore = fixedShape.Rect();
        fixedShape.ResizeFromSide(CropBox.Side.Right, 0.9);

        // Picture request: play the layer (blocks + answering the request counter).
        string pictureResult;
        try
        {
            using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("StreamMirror.SettingsSignal");
            pictureResult = "skipped (a game is running)";
        }
        catch (FileNotFoundException)
        {
            using var signal = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("StreamMirror.SettingsSignal", 16);
            using var signalView = signal.CreateViewAccessor(0, 16);
            signalView.Write(0, 0x53534F47u);
            signalView.Write(4, 2u);
            const int width = 4, height = 2;
            using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(MirrorSnapshot.BlockName, MirrorSnapshot.BlockSize);
            using var blockView = block.CreateViewAccessor();
            var layer = Task.Run(async () =>
            {
                for (var i = 0; i < 100 && signalView.ReadInt32(12) == 0; i++) await Task.Delay(10);
                blockView.Write(0, MirrorSnapshot.BlockMagic);
                blockView.Write(12, (uint)width); blockView.Write(16, (uint)height);
                blockView.Write(20, 8192u); blockView.Write(24, 4096u);
                blockView.Write(28, 100u); blockView.Write(32, 200u); blockView.Write(36, 1920u); blockView.Write(40, 1080u);
                for (var i = 0; i < width * height * 4; i++) blockView.Write(MirrorSnapshot.PixelsOffset + i, (byte)(i * 7));
                // Version 2: the right eye first, the left eye as the second picture.
                blockView.Write(4, 2u);
                blockView.Write(44, 1u); blockView.Write(48, 0u);
                for (var i = 0; i < width * height * 4; i++) blockView.Write(MirrorSnapshot.SecondPixelsOffset + i, (byte)(i * 3));
                blockView.Write(8, blockView.ReadInt32(8) + 1);
            });
            var (picture, message) = Task.Run(MirrorSnapshot.RequestAsync).GetAwaiter().GetResult();
            layer.Wait();
            var pixels = new byte[width * height * 4];
            picture?.Image.CopyPixels(pixels, width * 4, 0);
            var otherPixels = new byte[width * height * 4];
            picture?.OtherImage?.CopyPixels(otherPixels, width * 4, 0);
            pictureResult = picture is { FullWidth: 8192, FullHeight: 4096, CropX: 100, CropWidth: 1920, Eye: 1, OtherEye: 0 } && picture.Image.PixelWidth == width && pixels[5] == 35
                            && otherPixels[5] == 15
                ? "ok" : "wrong: " + message;
        }

        var checks = new (string Name, bool Ok)[]
        {
            ("a 16:9 box half the height of a square image is 0.889 of its width", Near(w1, 0.5 * 16 / 9) && Near(h1, 0.5)),
            ("a box too big for the image is shrunk, keeping its shape", Near(w2, 1) && Near(h2, 9.0 / 16)),
            ("a box pushed into a corner stays inside the image", Near(corner.Left + corner.Width, 1) && Near(corner.Top, 0)),
            ("a 9:16 box can take the full height", Near(vertical.Size().Height, 1) && Near(vertical.Size().Width, 9.0 / 16)),
            ("dragging a corner keeps the opposite corner and the shape", Near(resized.Rect().Left, 0.2) && Near(resized.Rect().Top, 0.2) && Near(resized.Rect().Width, 0.7) && Near(resized.Rect().Height, 0.7)),
            ("with room kept free, a box pushed into a corner stops short of the edges", Near(pushed.Left, 0.02) && Near(pushed.Top, 0.02)),
            ("with room kept free, the largest box leaves that room on both sides", Near(largest.Left, 0.02) && Near(largest.Width, 0.96) && Near(largest.Height, 0.96 * 9 / 16)),
            ("with room kept free, dragging a corner stops at the room", Near(roomyResize.Rect().Left, 0.5) && Near(roomyResize.Rect().Width, 0.45) && Near(roomyResize.Rect().Height, 0.45)),
            ("the free shape follows the pointer both ways", Near(free.Rect().Width, 0.5) && Near(free.Rect().Height, 0.2)),
            ("free shape, a side dragged: only its own size changes, the other sides stay",
                Near(widened.Left, 0.2) && Near(widened.Width, 0.6) && Near(widened.Top, 0.3) && Near(widened.Height, 0.3)
                && Near(heightened.Left, 0.2) && Near(heightened.Width, 0.6) && Near(heightened.Top, 0.1) && Near(heightened.Height, 0.5)),
            ("free shape, a side dragged past the opposite one stops at the smallest size", Near(squeezed.Left + squeezed.Width, 0.8) && Near(squeezed.Width, CropBox.MinSize)),
            ("free shape, a side dragged out of the picture stops at the room kept free", Near(marginSides.Rect().Top + marginSides.Rect().Height, 0.95)),
            ("a fixed shape has no sides to drag", fixedShape.Rect() == fixedBefore),
            ("\"16:9\", \"0\" and plain numbers are understood as shapes", Near(CropBox.ParseAspect("16:9"), 16.0 / 9) && CropBox.ParseAspect("0") == 0 && Near(CropBox.ParseAspect("1.5"), 1.5)),
            ("a picture request is answered and read back, both eyes (" + pictureResult + ")", pictureResult == "ok" || pictureResult.StartsWith("skipped")),
        };
        foreach (var (name, ok) in checks) report.WriteLine($"crop - {(ok ? "ok  " : "FAIL")} {name}");
        return checks.All(c => c.Ok);
    }

    /// <summary>What a settings row does with a typed number, and the settings whose slider runs the other way.</summary>
    private static bool SelfTestSettingRows(StreamWriter report)
    {
        static SettingDef Def(string key) => Settings.All.First(d => d.Key == key);
        static bool Near(double? a, double b) => a is { } value && Math.Abs(value - b) < 1e-9;
        var size = Def("radius");            // a share of the image, shown in percent
        var glide = Def("crop_follow_glide_ms"); // milliseconds
        var steady = Def("stabilize_min_cutoff");
        var named = Settings.All.Select(d => d.Key).ToHashSet();
        var portKept = StreamMirror.MainWindow.KeepsOldGazePort(hadSettingsFile: true, twoThreeRanBefore: false, port: null);
        var portNew = !StreamMirror.MainWindow.KeepsOldGazePort(hadSettingsFile: false, twoThreeRanBefore: false, port: null);
        var portOwn = !StreamMirror.MainWindow.KeepsOldGazePort(hadSettingsFile: true, twoThreeRanBefore: false, port: "9010")
                      && !StreamMirror.MainWindow.KeepsOldGazePort(hadSettingsFile: true, twoThreeRanBefore: true, port: null);
        var ring = new[] { Settings.GroupLook, Settings.GroupTail, Settings.GroupMotion };
        var checks = new (string Name, bool Ok)[]
        {
            ("an update from before 2.3.0 with no port of its own keeps the gaze input port at 9005", portKept),
            ("a 1866 x 1050 box in a 2000 x 2100 image may turn about 8.5 degrees; a box that fills its room, none",
                Math.Abs(StreamMirror.MainWindow.MaxTurn(933, 525, 1000, 1050) * 180 / Math.PI - 8.5) < 0.6 && StreamMirror.MainWindow.MaxTurn(933, 525, 933, 1050) < 0.01),
            ("a new install gets 9006; a port of its own, or a file a 2.3 build already used, is left as it is", portNew && portOwn),
            ("a typed \"3.5\" for a share of the image is 3.5 %, with or without its unit", Near(size.Parse("3.5"), 0.035) && Near(size.Parse(" 3.5 % of image"), 0.035) && Near(size.Parse("3,5"), 0.035)),
            ("a typed number is brought onto the slider's steps and into its range", Near(size.Parse("999"), size.Max) && Near(size.Parse("0"), size.Min) && Near(glide.Parse("503"), Math.Round(503 / glide.Step) * glide.Step)),
            ("what is not a number changes nothing", size.Parse("") == null && size.Parse("Instant") == null && size.Parse("abc") == null),
            ("the two steadiness settings run the other way, and no other", Settings.All.Where(d => d.Reversed).Select(d => d.Key).Order().SequenceEqual(new[] { "filter_min_cutoff", "stabilize_min_cutoff" })
                && steady.Reversed && Near(steady.Parse("0.7"), 0.7)),
            ("every setting of the ring has its place on the Ring page or falls into a section of its group",
                Settings.All.Where(d => ring.Contains(d.Group)).All(d => named.Contains(d.Key)) && Settings.All.Any(d => d.Group == Settings.GroupTail) && Settings.All.Any(d => d.Group == Settings.GroupMotion)),
        };
        foreach (var (name, ok) in checks) report.WriteLine($"settings - {(ok ? "ok  " : "FAIL")} {name}");
        return checks.All(c => c.Ok);
    }

    /// <summary>
    /// The layers tool: the order rules on made-up lists, and the reordering mechanism itself - a .reg file imported by
    /// reg.exe - on a scratch key of this app's own under HKCU (no elevation, nothing of OpenXR's is touched).
    /// </summary>
    private static bool SelfTestLayerOrder(string outputDir, StreamWriter report)
    {
        LayerEntry Layer(string name, params string[] extensions) => new($@"C:\Layers\{name}.json", true, name, null) { Extensions = extensions };
        var mirror = Layer(LayerStatus.StreamMirrorLayer);
        var toolkit = Layer("XR_APILAYER_MBUCCHIA_toolkit");
        var quadViews = Layer(LayerStatus.QuadViewsLayer, "XR_VARJO_quad_views", "XR_VARJO_foveated_rendering");
        var eyeTracking = Layer("XR_APILAYER_EXAMPLE_eye_tracker", "XR_EXT_eye_gaze_interaction");
        var unknown = Layer("XR_APILAYER_EXAMPLE_unknown");
        var gameLayers = Layer(LayerStatus.GameLayersLayer);
        var gameLayersLast = LayerOrder.Suggest([quadViews, toolkit, mirror, gameLayers]);

        var bad = new List<LayerEntry> { mirror, unknown, eyeTracking, toolkit, quadViews };
        var problems = LayerOrder.Problems(bad);
        var suggested = LayerOrder.Suggest(bad);
        var good = new List<LayerEntry> { quadViews, toolkit, mirror };
        var mirrorOff = new List<LayerEntry> { mirror with { Enabled = false }, quadViews };

        // The mechanism: values are re-created in the wanted order by one import.
        const string scratch = @"Software\StreamMirrorVR\SelfTestLayerOrder";
        var names = new[] { @"C:\Program Files\A\a.json", @"C:\B ""quoted""\b.json", @"C:\C\c.json" };
        var wanted = new[] { names[2], names[0], names[1] };
        List<string> after = [];
        string mechanism;
        var observable = true;
        try
        {
            // Control first: does deleting values and creating them again reorder them in THIS registry view? In the real
            // registry it does (a new value goes to the end of the list). A process started from inside a packaged (MSIX)
            // app sees a virtualised view in which a re-created value returns to its old place, so nothing can be observed.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch))
            {
                foreach (var name in names) key.SetValue(name, 0, Microsoft.Win32.RegistryValueKind.DWord);
                foreach (var name in names) key.DeleteValue(name);
                foreach (var name in wanted) key.SetValue(name, 0, Microsoft.Win32.RegistryValueKind.DWord);
                observable = key.GetValueNames().SequenceEqual(wanted);
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch))
            {
                for (var i = 0; i < names.Length; i++) key.SetValue(names[i], i % 2, Microsoft.Win32.RegistryValueKind.DWord);
            }
            var regPath = Path.Combine(outputDir, "layer-order-test.reg");
            File.WriteAllText(regPath, LayerOrder.RegFile(perUser: true, [.. wanted.Select(n => (n, Array.IndexOf(names, n) % 2))], scratch), System.Text.Encoding.Unicode);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "reg.exe"), $"import \"{regPath}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            process.WaitForExit();
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(scratch)!)
            {
                after = [.. key.GetValueNames()];
                mechanism = after.SequenceEqual(wanted) && after.All(n => (int)key.GetValue(n)! == Array.IndexOf(names, n) % 2) ? "ok" : "order or data wrong";
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        }
        catch (Exception e)
        {
            mechanism = e.Message;
        }

        var checks = new (string Name, bool Ok)[]
        {
            ("a bad order is reported (mirror under quad views and under the overlay layer, toolkit under quad views, quad views and toolkit above the eye tracker)",
                problems.Count == 5 && problems.Any(p => p.StartsWith("MBUCCHIA_quad_views_foveated must be above KANAICASUAL_StreamMirror")) &&
                problems.Any(p => p.StartsWith("MBUCCHIA_quad_views_foveated must be above EXAMPLE_eye_tracker"))),
            ("the suggested order satisfies every rule and leaves unrelated layers where they were",
                suggested != null && LayerOrder.Problems(suggested).Count == 0 && suggested.IndexOf(unknown) < suggested.IndexOf(eyeTracking)),
            ("a good order has no problems", LayerOrder.Problems(good).Count == 0),
            ("a layer that is switched off is not complained about", LayerOrder.Problems(mirrorOff).Count == 0),
            ("the game-layers watcher at the bottom is reported, and the suggestion puts it first, the rest as they were",
                LayerOrder.Problems([quadViews, toolkit, mirror, gameLayers]).Count == 3 &&
                gameLayersLast is [var top, var second, var third, var last] && top == gameLayers && second == quadViews && third == toolkit && last == mirror),
            (observable ? "reg.exe import re-creates the values in the wanted order, names with spaces and quotes included (" + mechanism + ")"
                        : "reordering by reg.exe import: NOT TESTABLE HERE - this registry view is virtualised and always sorted; run the selftest from a normal terminal",
                !observable || mechanism == "ok"),
        };
        foreach (var (name, ok) in checks) report.WriteLine($"layer order - {(ok ? "ok  " : "FAIL")} {name}");
        if (problems.Count != 5) foreach (var p in problems) report.WriteLine("    reported: " + p);
        if (observable && mechanism != "ok") report.WriteLine("    after import: " + string.Join(" | ", after));
        return checks.All(c => c.Ok);
    }

    /// <summary>
    /// Quad Views tab logic against a canned copy of a real case: a Pimax driven through SteamVR, with a settings file
    /// written by the QuadViews Companion (focus size only under headset sections that SteamVR does not match).
    /// </summary>
    private static bool SelfTestQuadViews(string outputDir, StreamWriter report)
    {
        const string cannedLog = """
            2026-09-18 22:19:00 +0100: XR_APILAYER_MBUCCHIA_quad_views_foveated layer (v1.1.4) is active
            2026-09-18 22:19:01 +0100: Application: DCS (DCS.exe)
            2026-09-18 22:19:01 +0100: Using OpenXR runtime: SteamVR/OpenXR 2.17.10
            2026-09-18 22:19:01 +0100: Using OpenXR system: SteamVR/OpenXR : lighthouse
            2026-09-18 22:19:01 +0100: Trying to locate configuration file at 'Z:\nowhere\settings.cfg'...
            2026-09-18 22:19:01 +0100: Eye tracking is supported
            2026-09-18 22:19:01 +0100: Recommended peripheral resolution: 2270x2372 (0.320x density)
            2026-09-18 22:19:01 +0100: Recommended focus resolution: 3042x3178 (1.225x density)
            2026-09-18 22:19:01 +0100:   Stereo pixel count was: 105,191,104 (7096x7412)
            2026-09-18 22:19:01 +0100:   Quad views pixel count is: 30,103,832
            2026-09-18 22:19:01 +0100:   Savings: -71.4%
            """;
        const string cannedFile = """
            # QuadViews configuration file created using TallyMouse's QuadViews Companion App.

            # Common settings for all headsets (unless overriden below).
            smoothen_focus_view_edges=0.2
            sharpen_focus_view=0
            turbo_mode=1
            peripheral_multiplier=0.32
            focus_multiplier=1.225

            [Pimax]
            # Dynamic Foveated Rendering settings
            horizontal_focus_section=0.2
            vertical_focus_section=0.2
            peripheral_multiplier=0.32

            [SteamVR]
            # Turbo Mode causes unexplained errors with SteamVR.
            turbo_mode=0

            [app:SomeGame]
            horizontal_focus_section=0.6

            # Un-advertise in incompatible apps.
            [exe:Contractors]
            unadvertise=1
            """;

        var logPath = Path.Combine(outputDir, "quadviews-canned.log");
        File.WriteAllText(logPath, cannedLog);
        var session = QuadViewsSession.Read(logPath);
        var file = QuadViewsFile.Parse(cannedFile.Replace("\r\n", "\n"));

        long Pixels(QuadViewsFile f)
        {
            var e = QuadViewsFile.Evaluate([("test", f)], session);
            return QuadViewsPixels.Compute(session!.StereoWidth, session.StereoHeight, e["peripheral_multiplier"], e["focus_multiplier"], e["horizontal_focus_section"], e["vertical_focus_section"]).Total;
        }

        QuadViewsFile OwnPlan()
        {
            var own = QuadViewsSetting.All.ToDictionary(s => s.Id, s => s.ToSlider(QuadViewsFile.BuiltIn[s.Keys[0]]));
            own["gaze_deadzone"] = 0.7;
            return QuadViewsFile.Plan(QuadViewsFile.Parse(""), own, new HashSet<string> { "gaze_deadzone" }, ownLayer: true);
        }
        bool OwnOrderOk()
        {
            var mirror = new LayerEntry("mirror.json", true, LayerStatus.StreamMirrorLayer, null);
            var quad = new LayerEntry("quad.json", true, LayerStatus.OwnQuadViewsLayer, null) { Extensions = ["XR_VARJO_quad_views", "XR_VARJO_foveated_rendering"] };
            return LayerOrder.Problems([mirror, quad]).Count > 0 && LayerOrder.Suggest([mirror, quad]) is [var first, _] && ReferenceEquals(first, quad)
                && LayerOrder.Problems([quad, mirror]).Count == 0;
        }
        // The layer reads its file without share-delete: a plain move over it is refused while it reads, SwapIn waits it out.
        bool SwapsInWhileRead()
        {
            var folder = Path.Combine(Path.GetTempPath(), "streammirror-swap-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "settings.cfg");
            File.WriteAllText(path, "old");
            File.WriteAllText(path + ".a", "plain");
            File.WriteAllText(path + ".b", "new");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bool refused;
            try { File.Move(path + ".a", path, overwrite: true); refused = false; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { refused = true; }
            _ = Task.Delay(40).ContinueWith(_ => reader.Dispose());
            ConfigFile.SwapIn(path + ".b", path);
            var result = refused && File.ReadAllText(path) == "new";
            try { Directory.Delete(folder, recursive: true); } catch { /* scratch */ }
            return result;
        }
        // A profile name with a '#', brackets and '=' comes back whole from both files.
        bool OddNameSurvives()
        {
            const string name = "[DCS] #2 = night";
            var folder = Path.Combine(Path.GetTempPath(), "streammirror-names-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            var previous = Environment.GetEnvironmentVariable("STREAMMIRROR_PROFILES_FILE");
            try
            {
                var settings = Path.Combine(folder, "settings.cfg");
                ConfigFile.Save(settings, new Dictionary<string, string>(Settings.Defaults()) { ["open_crop_profile"] = name });
                Environment.SetEnvironmentVariable("STREAMMIRROR_PROFILES_FILE", Path.Combine(folder, "crop-profiles.ini"));
                var profile = new CropProfile { Name = name, Game = "DCS.exe" };
                profile.Values["crop_height"] = "0.25";
                CropProfiles.Save([profile]);
                return ConfigFile.Load(settings)["open_crop_profile"] == name && CropProfiles.Load() is [{ Name: name, Game: "DCS.exe" }];
            }
            finally
            {
                Environment.SetEnvironmentVariable("STREAMMIRROR_PROFILES_FILE", previous);
                try { Directory.Delete(folder, recursive: true); } catch { /* scratch */ }
            }
        }
        QuadViewsPixels.Result Logged() => QuadViewsPixels.Compute(session!.StereoWidth, session.StereoHeight, 0.32, 1.225, 0.35, 0.35);
        var before = QuadViewsFile.Evaluate([("test", file)], session);
        var sliders = QuadViewsSetting.All.ToDictionary(s => s.Id, _ => 0.0);
        var untouched = QuadViewsFile.Plan(file, sliders, new HashSet<string>());
        var after = QuadViewsFile.Evaluate([("test", untouched)], session);
        var again = QuadViewsFile.Plan(untouched, sliders, new HashSet<string>());

        sliders["focus_h"] = 30; sliders["peripheral_res"] = 16; sliders["turbo"] = 1;
        var edited = QuadViewsFile.Plan(untouched, sliders, new HashSet<string> { "focus_h", "peripheral_res", "turbo" });
        var editedValues = QuadViewsFile.Evaluate([("test", edited)], session);
        File.WriteAllText(Path.Combine(outputDir, "quadviews-planned.cfg"), edited.Text);
        // Turbo off everywhere: the [SteamVR] guard repeats the common value, and stays all the same.
        sliders["turbo"] = 0;
        var turboOff = QuadViewsFile.Plan(edited, sliders, new HashSet<string> { "turbo" });
        // A real override in a headset section survives a change of the same slider in the common part.
        var withOverride = QuadViewsFile.Parse(edited.Text.Replace("[SteamVR]", "[Oculus]\nhorizontal_focus_section=0.5\n\n[SteamVR]"));
        sliders["focus_h"] = 40;
        var overridden = QuadViewsFile.Plan(withOverride, sliders, new HashSet<string> { "focus_h" });

        var checks = new (string Name, bool Ok)[]
        {
            ("log: runtime, game and resolution are read", session is { RuntimeName: "SteamVR/OpenXR 2.17.10", AppName: "DCS", ExeName: "DCS.exe", StereoWidth: 7096, StereoHeight: 7412, QuadPixels: 30103832, EyeTracking: true }),
            ("a [Pimax] focus size does not reach a SteamVR runtime (the layer's 35 % applies)", before["horizontal_focus_section"] == 0.35),
            ("pixel count equals what the layer logged (30,103,832)", Pixels(file) == 30103832),
            ("Apply with nothing changed copies the intended 20 % into the common part", after["horizontal_focus_section"] == 0.2 && after["vertical_focus_section"] == 0.2),
            ("...which gives 17,081,296 pixels", Pixels(untouched) == 17081296),
            ("...and leaves values that already reach the game as they were written", untouched.InCommon("peripheral_multiplier") == "0.32"),
            ("a second Apply changes nothing", again.Text == untouched.Text),
            ("Apply writes the whole file fresh: the app's header on top, no [Pimax] section", untouched.Lines[0] == QuadViewsFile.OwnFileHeader && !untouched.Lines.Contains("[Pimax]")),
            ("a changed slider is written once, in the common part", editedValues["horizontal_focus_section"] == 0.3 && edited.Entries().Count(e => e.Key == "horizontal_focus_section" && e.Section == null) == 1
                && edited.InCommon("horizontal_focus_section") == "0.3"),
            ("the [SteamVR] Turbo guard stays when it repeats the common value", turboOff.InCommon("turbo_mode") == "0" && turboOff.Entries().Any(e => e.Section == "[SteamVR]" && e.Key == "turbo_mode")),
            ("a headset section's own focus size goes: the page's value counts everywhere", overridden.InCommon("horizontal_focus_section") == "0.4"
                && !overridden.Lines.Contains("[Oculus]")),
            ("16 % of the pixels is a 0.4 multiplier", edited.InCommon("peripheral_multiplier") == "0.4" && editedValues["peripheral_multiplier"] == 0.4),
            ("a per-game focus size goes, with the section it leaves empty: the page's value counts in every game", !untouched.Lines.Contains("[app:SomeGame]")),
            ("per-game lines the page has no setting for stay", untouched.Entries().Any(e => e.Section == "[exe:Contractors]" && e.Key == "unadvertise")),
            ("the [SteamVR] Turbo guard is left alone", editedValues["turbo_mode"] == 0 && edited.InCommon("turbo_mode") == "1"),
            ("the lines under the sliders give the sizes the layer logged (sharp 3042 x 3178, surround 2270 x 2372) and its total (29 %)",
                QuadViewsReadout.Focus(1.225, Logged()).Contains("3042 x 3178") && QuadViewsReadout.Peripheral(0.32, Logged(), 7096, 7412).Contains("2270 x 2372")
                && QuadViewsReadout.Total(0.32, 1.225, 0.35, 0.35, Logged(), session!.StereoPixels).StartsWith("In all: 29 %")),
            ("StreamMirror VR's layer: its own settings are written (gaze smoothing, deadzone 0.7 as typed, diagnostics), the official debug switch is not",
                OwnPlan().InCommon("gaze_smoothing_ms") == "8" && OwnPlan().InCommon("gaze_deadzone_degrees") == "0.7" && OwnPlan().InCommon("show_diagnostics") == "0"
                && OwnPlan().InCommon("debug_eye_gaze") == null),
            ("...and the official layer's file gets none of them", untouched.InCommon("gaze_smoothing_ms") == null && untouched.InCommon("show_diagnostics") == null
                && untouched.InCommon("debug_eye_gaze") != null),
            ("StreamMirror VR's quad views layer must sit above the mirror, and Fix order puts it there", OwnOrderOk()),
            ("a settings file the layer is reading is replaced once the read ends", SwapsInWhileRead()),
            ("a profile name with #, brackets and = survives both files", OddNameSurvives()),
            ("...and say the sharpness against the headset's", QuadViewsReadout.Sharpness(1.0) == "As sharp as the headset." && QuadViewsReadout.Sharpness(1.414).StartsWith("Draws 200 %")
                && QuadViewsReadout.Sharpness(0.4) == "Draws 16 % of the headset's pixels: faster, but softer."),
            ("a resolution shows the file's multiplier first", QuadViewsSetting.All.First(s => s.Id == "peripheral_res").Format(16) == "0.4x · 16 %"
                && QuadViewsSetting.All.First(s => s.Id == "focus_h").Format(20) == "20 % of FOV"),
        };
        foreach (var (name, ok) in checks) report.WriteLine($"quad views - {(ok ? "ok  " : "FAIL")} {name}");

        // A PC with no settings file yet: TM's Favorite chosen, then Apply - the file as the app writes it, for a look.
        var favourite = QuadViewsSetting.Presets.First(p => p.Name == "TM's Favorite").Values;
        var fresh = QuadViewsSetting.All.ToDictionary(s => s.Id, s => favourite.TryGetValue(s.Id, out var v) ? v : s.Id == "turbo" ? 1.0 : 0.0);
        var newFile = QuadViewsFile.Plan(QuadViewsFile.Load(Path.Combine(outputDir, "no-such-settings.cfg")), fresh, favourite.Keys.ToHashSet());
        File.WriteAllText(Path.Combine(outputDir, "quadviews-new-file.cfg"), newFile.Text);
        var newFileOk = newFile.Lines[0] == QuadViewsFile.OwnFileHeader && newFile.Lines.Count(l => l.StartsWith("# Settings in this first part", StringComparison.Ordinal)) == 1
            && newFile.InCommon("horizontal_focus_section") == "0.33";
        report.WriteLine($"quad views - {(newFileOk ? "ok  " : "FAIL")} a new file says once that its top part applies everywhere: no second heading");
        int At(string key) => newFile.Lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        var grouped = At("horizontal_focus_section") < At("vertical_focus_section") && At("vertical_focus_section") < At("vertical_focus_offset")
            && At("vertical_focus_offset") < At("horizontal_fixed_section") && At("horizontal_fixed_section") < At("vertical_fixed_offset")
            && At("vertical_fixed_offset") < At("focus_multiplier");
        var settled = QuadViewsFile.Plan(newFile, fresh, new HashSet<string>()).Text == newFile.Text;
        report.WriteLine($"quad views - {(grouped && settled ? "ok  " : "FAIL")} a new file lists the eye-tracked values together, then the fixed ones, then the rest; a second Apply leaves it as it is");

        // This PC's real files, for information only (they change whenever the user changes a setting).
        var real = QuadViewsSession.Read(QuadViewsSession.LogPath);
        if (real is { HasResolution: true })
        {
            var files = new List<(string, QuadViewsFile)>();
            var shipped = real.ConfigPaths.FirstOrDefault(p => !string.Equals(p, QuadViewsFile.DefaultPath, StringComparison.OrdinalIgnoreCase));
            if (shipped != null && File.Exists(shipped)) files.Add(("shipped", QuadViewsFile.Load(shipped)));
            files.Add(("user", QuadViewsFile.Load(QuadViewsFile.DefaultPath)));
            var e = QuadViewsFile.Evaluate(files, real);
            var sections = real.EyeTracking == false ? ("horizontal_fixed_section", "vertical_fixed_section") : ("horizontal_focus_section", "vertical_focus_section");
            var computed = QuadViewsPixels.Compute(real.StereoWidth, real.StereoHeight, e["peripheral_multiplier"], e["focus_multiplier"], e[sections.Item1], e[sections.Item2]);
            report.WriteLine($"quad views (this PC): runtime '{real.RuntimeName}', {real.StereoWidth}x{real.StereoHeight}; layer logged {real.QuadPixels}, computed from the files {computed.Total} " +
                             $"(focus {computed.FocusWidth}x{computed.FocusHeight} vs logged {real.FocusResolution}); focus size in effect {e[sections.Item1]}x{e[sections.Item2]}");
            // An Apply with nothing changed only tidies: for this PC's runtime and game, every value stays (read only; nothing is written to the real file).
            var userFile = QuadViewsFile.Load(QuadViewsFile.DefaultPath);
            var tidied = QuadViewsFile.Plan(userFile, QuadViewsSetting.All.ToDictionary(s => s.Id, _ => 0.0), new HashSet<string>());
            var tidiedFiles = files.Take(files.Count - 1).Append(("user", tidied)).ToList();
            var tidiedValues = QuadViewsFile.Evaluate(tidiedFiles, real);
            // A per-game value for this game goes on purpose; everything else stays as it was.
            var perGame = e.DecidedBy.Where(d => d.Value.StartsWith("[app:", StringComparison.Ordinal) || d.Value.StartsWith("[exe:", StringComparison.Ordinal)).Select(d => d.Key).ToHashSet();
            var same = QuadViewsFile.BuiltIn.Keys.Where(k => !perGame.Contains(k) || k == "turbo_mode").All(k => e[k] == tidiedValues[k]);
            foreach (var key in perGame) report.WriteLine($"quad views (this PC): {key} was {e[key]} for {real.AppName} by its own section; after the tidy {tidiedValues[key]}");
            File.WriteAllText(Path.Combine(outputDir, "quadviews-this-pc-tidied.cfg"), tidied.Text);
            report.WriteLine($"quad views (this PC) - {(same ? "ok  " : "FAIL")} an Apply that changes nothing writes the file fresh ({tidied.Lines.Count} lines, was {userFile.Lines.Count}) and keeps every value for '{real.RuntimeName}' / {real.AppName}");
        }
        else
        {
            report.WriteLine("quad views (this PC): no usable Quad-Views-Foveated log");
        }
        return checks.All(c => c.Ok);
    }

    /// <summary>Exercises everything without showing a window, writing the results into a folder.</summary>
    /// <summary>The SHA-256 of an empty file, through the same code the updater checks downloads with.</summary>
    private static string ChecksumOfEmptyFile(string outputDir)
    {
        var path = Path.Combine(outputDir, "empty.bin");
        File.WriteAllBytes(path, []);
        return Task.Run(() => UpdateChecker.Sha256Async(path)).GetAwaiter().GetResult();
    }

    private static int SelfTest(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        using var report = new StreamWriter(Path.Combine(outputDir, "selftest.txt"));
        try
        {
            var status = LayerStatus.Get();
            report.WriteLine($"quad views: {status.QuadViews.Health} - {status.QuadViews.Detail}");
            report.WriteLine($"gaze mirror: {status.StreamMirror.Health} - {status.StreamMirror.Detail}");
            report.WriteLine($"obs: {status.ObsPath ?? "(not found)"} plugin present={status.ObsPluginPresent}");
            report.WriteLine($"order correct: {status.OrderCorrect?.ToString() ?? "n/a"}");
            foreach (var layer in status.Layers) report.WriteLine($"  [{(layer.Enabled ? "on " : "off")}] {layer.LayerName ?? "?"}  {layer.JsonPath}");

            var (liveState, liveText) = GazeLive.Read();
            report.WriteLine($"live: {liveState} - {liveText}");

            // Config round trip.
            var configPath = Path.Combine(outputDir, "roundtrip.cfg");
            var values = ConfigFile.Load(ConfigFile.DefaultPath);
            ConfigFile.Save(configPath, values);
            var reloaded = ConfigFile.Load(configPath);
            var mismatches = values.Where(kv => reloaded.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
            report.WriteLine($"config round trip: {values.Count} keys, mismatches: {(mismatches.Count == 0 ? "none" : string.Join(", ", mismatches))}");

            // Previews.
            var index = 0;
            foreach (var (background, moving) in new[] { (PreviewBackground.Cockpit, false), (PreviewBackground.Terrain, false), (PreviewBackground.Sky, false), (PreviewBackground.Cockpit, true) })
            {
                var bitmap = RingPreview.Render(values, background, moving, 340);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(outputDir, $"preview{index++}.png"));
                encoder.Save(file);
            }
            // The styles that are drawn from recorded places, with the eyes moving: trail behind the blob, cooling heat spot.
            foreach (var trailStyle in new[] { "bubble", "solid", "heatmap" })
            {
                var styled = new Dictionary<string, string>(values) { ["style"] = trailStyle };
                var bitmap = RingPreview.Render(styled, PreviewBackground.Cockpit, moving: true, 260);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(outputDir, $"preview-{trailStyle}-moving.png"));
                encoder.Save(file);
            }
            report.WriteLine("previews: ok");

            // Live link: play the layer's part (it creates the signal block) and check that the app's notify bumps it.
            var liveOk = true;
            try
            {
                using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("StreamMirror.SettingsSignal");
                report.WriteLine("live link: a game is running, so its real signal block exists - simulated test skipped (notify returned " + LiveLink.NotifySettingsChanged() + ")");
            }
            catch (FileNotFoundException)
            {
                var withoutGame = LiveLink.NotifySettingsChanged();
                using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("StreamMirror.SettingsSignal", 12);
                using var view = block.CreateViewAccessor(0, 12);
                view.Write(0, 0x53534F47u);
                view.Write(4, 1u);
                var first = LiveLink.NotifySettingsChanged();
                var second = LiveLink.NotifySettingsChanged();
                var generation = view.ReadInt32(8);
                liveOk = !withoutGame && first && second && generation == 2;
                report.WriteLine($"live link - {(liveOk ? "ok  " : "FAIL")} no game -> {withoutGame}; with a (simulated) game -> {first}, {second}; counter = {generation} (expected 2)");
            }

            // The VRChat gaze block: written here the way the helper writes it (by offset), read back through the app's reader.
            try
            {
                using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen("StreamMirror.ExternalGaze", 400);
                using var view = block.CreateViewAccessor(0, 400);
                view.Write(4, 1u); view.Write(8, 400u); view.Write(0, 0x58324D47u);
                view.Write(32, Environment.ProcessId); view.Write(36, 2);
                var name = System.Text.Encoding.UTF8.GetBytes("selftest writer"); var padded = new byte[32]; Array.Copy(name, padded, name.Length);
                view.WriteArray(80, padded, 0, 32);
                view.Write(16, 2L); view.Write(40, 1); view.Write(44, 1);
                view.Write(48, -0.25f); view.Write(52, 0.5f); view.Write(56, -0.2f); view.Write(60, 0.45f);
                view.Write(24, Environment.TickCount64);
                var fresh = MirrorLive.ReadExternalGaze();
                view.Write(24, Environment.TickCount64 - 5000);
                var stale = MirrorLive.ReadExternalGaze();
                view.Write(16, 3L); // A write in progress: the reader must not take it.
                var torn = MirrorLive.ReadExternalGaze();
                view.Write(16, 4L); view.Write(32, 0); // Gone: the block stays (Windows keeps it while anyone holds it), the writer does not.
                var externalOk = fresh is { Fresh: true, Writer: "selftest writer", LeftX: -0.25f, RightY: 0.45f } && stale is { Fresh: false, AgeMs: >= 4900 } && torn == null;
                liveOk &= externalOk;
                report.WriteLine($"vrchat block - {(externalOk ? "ok  " : "FAIL")} fresh sample read back ({fresh?.Writer}, left {fresh?.LeftX} {fresh?.LeftY}); a 5 s old one is stale; a torn read is refused");
            }
            catch (Exception e)
            {
                liveOk = false;
                report.WriteLine("vrchat block - FAIL " + e.Message);
            }

            // The SRanibro raw-gaze block: the same layout under its own name, per-eye validity, z fields at 112/116.
            try
            {
                using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen("StreamMirror.RawGaze", 400);
                using var view = block.CreateViewAccessor(0, 400);
                view.Write(4, 1u); view.Write(8, 400u); view.Write(0, 0x58324D47u);
                view.Write(32, Environment.ProcessId); view.Write(36, 3);
                var name = System.Text.Encoding.UTF8.GetBytes("selftest raw"); var padded = new byte[32]; Array.Copy(name, padded, name.Length);
                view.WriteArray(80, padded, 0, 32);
                view.Write(16, 2L); view.Write(40, 1); view.Write(44, 0); // Left eye only.
                view.Write(48, 0.3f); view.Write(52, 0.1f); view.Write(112, -0.95f);
                view.Write(24, Environment.TickCount64);
                var oneEye = MirrorLive.ReadRawGaze();
                view.Write(16, 4L); view.Write(32, 0);
                var rawOk = oneEye is { Fresh: true, Writer: "selftest raw", LeftX: 0.3f };
                liveOk &= rawOk;
                report.WriteLine($"raw block - {(rawOk ? "ok  " : "FAIL")} a one-eye sample counts as fresh ({oneEye?.Writer}, left {oneEye?.LeftX} {oneEye?.LeftY})");
            }
            catch (Exception e)
            {
                liveOk = false;
                report.WriteLine("raw block - FAIL " + e.Message);
            }

            var quadViewsOk = SelfTestQuadViews(outputDir, report);
            quadViewsOk &= SelfTestLayerOrder(outputDir, report);
            quadViewsOk &= SelfTestCrop(report);
            quadViewsOk &= SelfTestSettingRows(report);
            quadViewsOk &= SelfTestCover(outputDir, report);

            // Mirror window: what this PC offers, and the command line the settings turn into.
            var mirrorArguments = MirrorWindowControl.Arguments(new AppSettings { MirrorMonitor = 2, MirrorTitled = true, MirrorOutputWidth = 1920, MirrorOutputHeight = 1080, MirrorFps = 30 });
            var mirrorOk = mirrorArguments == "--monitor 2 --titled 1 --size 1920x1080 --fps 30" &&
                           MirrorWindowControl.Arguments(new AppSettings { MirrorMonitor = 0 }) == "--windowed --size fill --fps 60";
            report.WriteLine($"mirror window - {(mirrorOk ? "ok  " : "FAIL")} settings become the expected command line");
            report.WriteLine($"mirror window: program {MirrorWindowControl.FindProgram() ?? "(not found)"}, running={MirrorWindowControl.IsRunning()}, monitors: " +
                             string.Join(", ", MirrorWindowControl.Monitors().Select(m => $"{m.Number}={m.Width}x{m.Height}")));
            quadViewsOk &= mirrorOk;

            // The user's own presets survive a trip through app.json's format (exact numbers, empty ring slots stay empty), and
            // the Quad Views slots of older builds come over as presets named after their slot.
            var oldFile = """{"RingSlots":[{"SavedAt":"2026-09-19T07:00:00","Values":{"radius":"0.05"}},null,null],"QuadViewsSlots":[null,{"SavedAt":"2026-09-19T07:00:00","Values":{"peripheral_res":"4.84"}},null]}""";
            var carried = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(oldFile)!;
            carried.CarrySlots();
            var slotsBack = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(carried));
            var slotsOk = slotsBack is { RingSlots: null, QuadViewsSlots: null, RingPresets: [{ Name: "Slot 1" } ring], QuadViewsPresets: [{ Name: "Slot 2" } only] } &&
                          only.Values["peripheral_res"] == "4.84" && ring.Values["radius"] == "0.05";
            report.WriteLine($"preset slots - {(slotsOk ? "ok  " : "FAIL")} saved values round-trip, old ring and Quad Views slots become named presets");
            quadViewsOk &= slotsOk;

            // Update check: version parsing and picking, against a canned release list (no network).
            const string canned = """
                [
                  {"tag_name":"v0.9.0-beta.1","name":"0.9 beta","prerelease":true,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/v0.9.0-beta.1",
                   "assets":[{"name":"StreamMirror-VR-0.9.0.msi","size":1234,"browser_download_url":"https://github.com/someone/project/releases/download/v0.9.0-beta.1/StreamMirror-VR-0.9.0.msi"},
                             {"name":"StreamMirror-VR-0.9.0.msi.sha256","size":100,"browser_download_url":"https://github.com/someone/project/releases/download/v0.9.0-beta.1/StreamMirror-VR-0.9.0.msi.sha256"}]},
                  {"tag_name":"v0.5.2","name":"0.5.2","prerelease":false,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/v0.5.2",
                   "assets":[{"name":"StreamMirror-VR-0.5.2.msi","size":1234,"browser_download_url":"https://evil.example/StreamMirror-VR-0.5.2.msi"},
                             {"name":"StreamMirror-VR-0.5.2.msi.sha256","size":100,"browser_download_url":"https://github.com/someone/project/releases/download/v0.5.2/StreamMirror-VR-0.5.2.msi.sha256"}]},
                  {"tag_name":"v0.6.0","name":"unfinished","prerelease":false,"draft":true,"html_url":"https://github.com/someone/project/releases/tag/v0.6.0"},
                  {"tag_name":"v0.5.10","name":"0.5.10","prerelease":false,"draft":false,"html_url":"https://evil.example/not-github",
                   "assets":[{"name":"StreamMirror-VR-0.5.10.msi","size":1234,"browser_download_url":"https://github.com/someone/project/releases/download/v0.5.10/StreamMirror-VR-0.5.10.msi"},
                             {"name":"setup.exe","size":9999,"browser_download_url":"https://github.com/someone/project/releases/download/v0.5.10/setup.exe"}]},
                  {"tag_name":"nightly","name":"no version in tag","prerelease":false,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/nightly"}
                ]
                """;
            var releases = UpdateChecker.ParseReleases(canned, "someone/project");
            // The rename: a build that knows the repository by its old name gets the release from the renamed one (GitHub
            // answers the old name with the new), and the installer under its old name is taken too (the 2.x updaters' name).
            const string renamed = """
                [
                  {"tag_name":"v3.0.0","name":"3.0.0","prerelease":false,"draft":false,"html_url":"https://github.com/someone/StreamMirror-VR/releases/tag/v3.0.0",
                   "assets":[{"name":"VR-Gaze-Mirror-3.0.0.msi","size":1234,"browser_download_url":"https://github.com/someone/StreamMirror-VR/releases/download/v3.0.0/VR-Gaze-Mirror-3.0.0.msi"},
                             {"name":"VR-Gaze-Mirror-3.0.0.msi.sha256","size":100,"browser_download_url":"https://github.com/someone/StreamMirror-VR/releases/download/v3.0.0/VR-Gaze-Mirror-3.0.0.msi.sha256"}]}
                ]
                """;
            var afterRename = UpdateChecker.ParseReleases(renamed, "someone/VR-Gaze-Mirror").FirstOrDefault();
            var stable = UpdateChecker.PickUpdate(releases, new Version(0, 5, 2), includeBetas: false);
            var beta = UpdateChecker.PickUpdate(releases, new Version(0, 5, 2), includeBetas: true);
            var none = UpdateChecker.PickUpdate(releases, new Version(1, 0, 0), includeBetas: true);
            var sameNumber = UpdateChecker.PickUpdate(releases, new Version(0, 5, 10), includeBetas: false);
            var fromTestBuild = UpdateChecker.PickUpdate(releases, new Version(0, 5, 10), includeBetas: false, currentIsTestBuild: true);
            var testBuildOfBeta = UpdateChecker.PickUpdate(releases, new Version(0, 9, 0), includeBetas: true, currentIsTestBuild: true);
            var checks = new (string Name, bool Ok)[]
            {
                ("drafts and unversioned tags are ignored", releases.Count == 3),
                ("0.5.10 is newer than 0.5.2 (numeric, not text)", stable?.Version == new Version(0, 5, 10)),
                ("betas only when asked for", beta?.Version == new Version(0, 9, 0) && beta.IsBeta),
                ("nothing offered when already newer", none == null),
                ("the release of the same number is not offered to itself", sameNumber == null),
                ("a test build is offered the release of its own number (0.5.10-dev.rev3 -> 0.5.10)", fromTestBuild?.Version == new Version(0, 5, 10)),
                ("but not a beta of its own number", testBuildOfBeta == null),
                ("foreign links are replaced by the project's releases page", stable?.Url == "https://github.com/someone/project/releases"),
                ("an installer with its checksum from the project's own address can be installed", beta is { CanInstall: true, Installer.Name: "StreamMirror-VR-0.9.0.msi", Checksum.Size: 100 }),
                ("the renamed repository's release and its old-named installer are offered to a build with the old name",
                    afterRename is { CanInstall: true, Url: "https://github.com/someone/StreamMirror-VR/releases/tag/v3.0.0" }),
                ("an installer from a foreign address is not offered", releases.First(r => r.Tag == "v0.5.2") is { Installer: null, CanInstall: false }),
                ("an installer without a checksum, and an .exe, are not offered", stable is { Installer: not null, Checksum: null, CanInstall: false }),
                ("a sha256sum-style checksum file is read for the right name",
                    UpdateChecker.ParseChecksum("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *Other.msi\nABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789  StreamMirror-VR-0.9.0.msi\n", "StreamMirror-VR-0.9.0.msi")
                        == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
                    && UpdateChecker.ParseChecksum("not a hash", "x.msi") == null),
                ("a file's SHA-256 is computed as sha256sum would", ChecksumOfEmptyFile(outputDir) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
            };
            foreach (var (name, ok) in checks) report.WriteLine($"update check - {(ok ? "ok  " : "FAIL")} {name}");
            // The Mirror page's automatic refresh: it only fills an empty page, and only with a game's picture.
            var pictureChecks = new (string Name, bool Ok)[]
            {
                ("with no picture yet, a running game's picture is taken", StreamMirror.MainWindow.AutomaticRefreshAllowed(havePicture: false, armed: false, producerProgram: "DCS.exe")),
                ("a saved picture stays: only a button replaces it", !StreamMirror.MainWindow.AutomaticRefreshAllowed(havePicture: true, armed: false, producerProgram: "DCS.exe")),
                ("nothing is taken while a capture is armed", !StreamMirror.MainWindow.AutomaticRefreshAllowed(havePicture: false, armed: true, producerProgram: "DCS.exe")),
                ("SteamVR with no game in it (the helper mirrors its empty home) gives no picture", !StreamMirror.MainWindow.AutomaticRefreshAllowed(havePicture: false, armed: false, producerProgram: "")),
                ("no mirror at all gives no picture", !StreamMirror.MainWindow.AutomaticRefreshAllowed(havePicture: false, armed: false, producerProgram: null)),
            };
            foreach (var (name, ok) in pictureChecks) report.WriteLine($"mirror picture - {(ok ? "ok  " : "FAIL")} {name}");
            // Picture size: the frame turns with the box, nothing is scaled up, 0 is no limit (the core's pipeline does the same sum).
            var sizeChecks = new (string Name, bool Ok)[]
            {
                ("16:9 box at 4K: 3840 x 2160", Settings.OutputSize(6000, 3375, 3840) == (3840, 2160)),
                ("4:3 box at 4K: 2880 x 2160", Settings.OutputSize(6000, 4500, 3840) == (2880, 2160)),
                ("square box at 4K: 2160 x 2160", Settings.OutputSize(7000, 7000, 3840) == (2160, 2160)),
                ("9:16 box at 4K: 2160 x 3840", Settings.OutputSize(3375, 6000, 3840) == (2160, 3840)),
                ("16:9 box at 1080p: 1920 x 1080", Settings.OutputSize(6000, 3375, 1920) == (1920, 1080)),
                ("a box a hair off 16:9 (6814 x 3832) still gets 3840 x 2160", Settings.OutputSize(6814, 3832, 3840) == (3840, 2160)),
                ("a 16:9 box of 6226.5 x 3501.98 px (crop height 52.9 % of a 6620 px eye) gets 3840 x 2160, not 3840 x 2158", Settings.OutputSize(6226.5, 3501.98, 3840) == (3840, 2160)),
                ("a smaller box is sent as it is", Settings.OutputSize(1600, 900, 3840) == (1600, 900)),
                ("as large as the box: no limit", Settings.OutputSize(6814, 3832, 0) == (6814, 3832)),
            };
            // Every shape preset gives its exact numbers, whatever the box's size: the box is made the way the mirror makes it
            // (its height in eye pixels, its width = height x the shape, in single precision), at three sizes from real eyes.
            var shapes = new (string Shape, float Width, float Height, (int, int) K4, (int, int) P1440, (int, int) P1080)[]
            {
                ("16:9", 16, 9, (3840, 2160), (2560, 1440), (1920, 1080)),
                ("9:16", 9, 16, (2160, 3840), (1440, 2560), (1080, 1920)),
                ("1:1", 1, 1, (2160, 2160), (1440, 1440), (1080, 1080)),
                ("4:3", 4, 3, (2880, 2160), (1920, 1440), (1440, 1080)),
                ("4:5", 4, 5, (2160, 2700), (1440, 1800), (1080, 1350)),
                ("21:9", 21, 9, (3840, 1646), (2560, 1098), (1920, 822)),
            };
            var shapeChecks = shapes.Select(shape =>
            {
                var aspect = shape.Width / shape.Height;
                var exact = new[] { 4501.37f, 5844.06f, 6620f * 0.947f }.All(height =>
                {
                    var width = height * aspect;
                    return Settings.OutputSize(width, height, 3840) == shape.K4 && Settings.OutputSize(width, height, 2560) == shape.P1440
                        && Settings.OutputSize(width, height, 1920) == shape.P1080;
                });
                return ($"the {shape.Shape} shape gives {shape.K4.Item1} x {shape.K4.Item2}, {shape.P1440.Item1} x {shape.P1440.Item2} and {shape.P1080.Item1} x {shape.P1080.Item2} at any box size", exact);
            }).ToArray();
            foreach (var (name, ok) in sizeChecks.Concat(shapeChecks)) report.WriteLine($"picture size - {(ok ? "ok  " : "FAIL")} {name}");
            report.WriteLine($"this build: version {UpdateChecker.CurrentVersionText}, repository '{UpdateChecker.Repository}' (configured: {UpdateChecker.IsConfigured})");

            // Optional live read of a public repository's release list, to prove the network path (--selftest <dir> <owner/name>).
            var liveRepo = Environment.GetCommandLineArgs().Skip(3).FirstOrDefault();
            if (liveRepo != null)
            {
                try
                {
                    // Task.Run: blocking the UI thread on an await that wants to resume on it would deadlock.
                    var found = Task.Run(() => UpdateChecker.CheckAsync(liveRepo, includeBetas: true)).GetAwaiter().GetResult();
                    report.WriteLine($"live check of {liveRepo}: " + (found == null ? "reachable, nothing newer than this build" : $"reachable, newest is {found.Version} ({found.Tag}) -> {found.Url}"));
                }
                catch (Exception liveError)
                {
                    report.WriteLine($"live check of {liveRepo} failed: {liveError.Message}");
                }
            }
            return checks.All(c => c.Ok) && pictureChecks.All(c => c.Ok) && sizeChecks.All(c => c.Ok) && shapeChecks.All(c => c.Item2) && liveOk && quadViewsOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.WriteLine("SELFTEST FAILED: " + ex);
            return 1;
        }
    }
}
