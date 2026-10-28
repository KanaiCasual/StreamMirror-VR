using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace GazeOverlay;

/// <summary>What the 2.0 mirror is doing right now, read from the shared block every producer and reader share.</summary>
/// <summary>GazeSupport: 0 not known, 1 the headset gives eye gaze (it has this session), 2 the runtime says it does not, 3 it does, through the helper (from the headset's OpenXR runtime).</summary>
public sealed record MirrorState(bool Producing, bool OpenXR, string Program, string Application, int Width, int Height, bool GazeValid, int Eye, int GazeSupport);

/// <summary>What the helper last received from VRChat (see MirrorLive.ReadExternalGaze).</summary>
public sealed record ExternalGazeState(bool Fresh, long AgeMs, string Writer, float LeftX, float LeftY, float RightX, float RightY);

/// <summary>
/// Read-only look at the 2.0 shared block ("StreamMirror.Frames", v2\protocol\gaze_mirror_protocol.h): which producer is
/// live - the OpenXR layer inside a game, or the SteamVR helper - for which game, at what size, and whether gaze arrives.
/// Plus where the helper and the bundled Quad-Views-Foveated installer are. Nothing here polls: it is read when a tab
/// is shown or a button pressed.
/// </summary>
public static class MirrorLive
{
    private const string FramesName = "StreamMirror.Frames";
    private const uint FramesMagic = 0x46324D47; // 'GM2F'
    // Offsets in Frames (all fields are 4- or 8-byte aligned integers/floats, see the header).
    private const int ProducerKindOffset = 12, ProducerPidOffset = 16, WidthOffset = 24, HeightOffset = 28;
    private const int EyeOffset = 84, GazeValidOffset = 96, GazeSupportOffset = 108, ProgramOffset = 112, ApplicationOffset = 176;
    public const int ProducerOpenXR = 1;
    public const string HelperRunningMutex = "StreamMirror.HelperRunning";
    public const string HelperStopEvent = "StreamMirror.HelperStop";

    public static MirrorState? Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(FramesName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 320, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != FramesMagic) return null;
            var kind = view.ReadInt32(ProducerKindOffset);
            var program = new byte[64];
            var application = new byte[128];
            view.ReadArray(ProgramOffset, program, 0, program.Length);
            view.ReadArray(ApplicationOffset, application, 0, application.Length);
            static string Text(byte[] bytes)
            {
                var end = Array.IndexOf(bytes, (byte)0);
                return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
            }
            return new MirrorState(kind != 0, kind == ProducerOpenXR, Text(program), Text(application),
                (int)view.ReadUInt32(WidthOffset), (int)view.ReadUInt32(HeightOffset), view.ReadInt32(GazeValidOffset) != 0, view.ReadInt32(EyeOffset), view.ReadInt32(GazeSupportOffset));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private const int FrameNumberOffset = 88, ReadersOffset = 304, ReaderSize = 24, ReaderCount = 4;
    /// <summary>A reader is live while its heartbeat is younger than this (ReaderLiveMs in the header).</summary>
    public const long ReaderLiveMs = 2000;

    /// <summary>
    /// The readers that hold a place - the OBS source while it shows in a scene, the mirror window while it is open - with
    /// the age of their heartbeat, and how many frames the producer has published so far.
    /// </summary>
    public static (long FrameNumber, List<(int ProcessId, long HeartbeatAgeMs)> Readers)? ReadReaders()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(FramesName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, ReadersOffset + ReaderCount * ReaderSize, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != FramesMagic) return null;
            var now = Environment.TickCount64; // GetTickCount64, the clock the readers write
            var readers = new List<(int, long)>();
            for (var i = 0; i < ReaderCount; i++)
            {
                var at = ReadersOffset + i * ReaderSize;
                var pid = view.ReadInt32(at);
                if (pid != 0) readers.Add((pid, now - view.ReadInt64(at + 8)));
            }
            return (view.ReadInt64(FrameNumberOffset), readers);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Who publishes right now: kind (0 nobody, 1 the layer inside an OpenXR game, 2 the SteamVR helper), its process, and the game's program file.</summary>
    public static (int Kind, int ProcessId, string Program)? ReadProducer()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(FramesName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 320, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != FramesMagic) return null;
            var program = new byte[64];
            view.ReadArray(ProgramOffset, program, 0, program.Length);
            var end = Array.IndexOf(program, (byte)0);
            return (view.ReadInt32(ProducerKindOffset), view.ReadInt32(ProducerPidOffset), Encoding.UTF8.GetString(program, 0, end < 0 ? program.Length : end));
        }
        catch
        {
            return null; // No block: nothing of ours has run since Windows started.
        }
    }

    // ------------------------------------------------------------------ the SteamVR helper

    /// <summary>Next to this app when installed; in the repository's 2.0 build folder when run from there.</summary>
    public static string? FindHelper() => FindBeside("StreamMirrorHelper.exe", Path.Combine("v2", "bin", "helper"));

    /// <summary>
    /// Whether SteamVR is installed: OpenVR's own record of its runtime (%LOCALAPPDATA%\openvr\openvrpaths.vrpath, what
    /// SteamVR writes on its first start and VR_IsRuntimeInstalled reads) names a folder that exists.
    /// </summary>
    public static bool SteamVrInstalled()
    {
        try
        {
            var record = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            if (!File.Exists(record)) return false;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(record));
            return json.RootElement.TryGetProperty("runtime", out var runtimes) && runtimes.ValueKind == System.Text.Json.JsonValueKind.Array &&
                   runtimes.EnumerateArray().Any(r => r.ValueKind == System.Text.Json.JsonValueKind.String && Directory.Exists(r.GetString()));
        }
        catch
        {
            return true; // Unreadable: assume it is there, and let the helper's own registration say what is wrong.
        }
    }

    public static bool IsHelperRunning()
    {
        if (!Mutex.TryOpenExisting(HelperRunningMutex, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Starts the helper (it leaves by itself when SteamVR is not running).</summary>
    public static bool StartHelper() => RunHelper("", wait: false) != null;

    /// <summary>Tells a running helper to leave.</summary>
    public static void StopHelper()
    {
        try
        {
            using var stop = EventWaitHandle.OpenExisting(HelperStopEvent);
            stop.Set();
        }
        catch
        {
            // Not running.
        }
    }

    /// <summary>Registers the helper with SteamVR to start with it (or takes that back). Null = the helper was not found; otherwise its exit code (0 = done).</summary>
    public static async Task<int?> SetHelperAutostartAsync(bool on)
    {
        var process = RunHelper(on ? "--register" : "--unregister", wait: true);
        if (process == null) return null;
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    private static Process? RunHelper(string arguments, bool wait)
    {
        var program = FindHelper();
        if (program == null) return null;
        try
        {
            return Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(program)! });
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Quad-Views-Foveated's official installer

    // ------------------------------------------------------------------ eye gaze from VRChat (received by the helper)

    private const string ExternalGazeName = "StreamMirror.ExternalGaze";
    private const uint ExternalGazeMagic = 0x58324D47; // 'GM2X'
    private const int ExternalGazeSize = 400, ExternalGazeFreshMs = 250;

    /// <summary>
    /// The block the helper fills with VRChat's eye parameters (protocol: ExternalGaze), or null when no helper has run
    /// since Windows started. AgeMs is -1 until the first sample; Fresh = a sample within the last quarter second.
    /// </summary>
    public static ExternalGazeState? ReadExternalGaze() => ReadGazeBlock(ExternalGazeName);

    /// <summary>The block the helper fills with raw gaze from the gaze input port (same layout, "StreamMirror.RawGaze"), or null.</summary>
    public static ExternalGazeState? ReadRawGaze() => ReadGazeBlock("StreamMirror.RawGaze");

    private static ExternalGazeState? ReadGazeBlock(string name)
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, ExternalGazeSize, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != ExternalGazeMagic) return null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var before = view.ReadInt64(16);
                if ((before & 1) != 0) continue;
                var writtenMs = view.ReadInt64(24);
                var pid = view.ReadInt32(32);
                var valid = view.ReadInt32(40) != 0 || view.ReadInt32(44) != 0; // Either eye (the raw block is per eye).
                var state = new ExternalGazeState(
                    Fresh: false, AgeMs: writtenMs > 0 ? Math.Max(0, Environment.TickCount64 - writtenMs) : -1,
                    Writer: ReadText(view, 80, 32), LeftX: view.ReadSingle(48), LeftY: view.ReadSingle(52), RightX: view.ReadSingle(56), RightY: view.ReadSingle(60));
                if (view.ReadInt64(16) != before) continue;
                return state with { Fresh = pid != 0 && valid && state.AgeMs >= 0 && state.AgeMs <= ExternalGazeFreshMs };
            }
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(MemoryMappedViewAccessor view, int offset, int length)
    {
        var bytes = new byte[length];
        view.ReadArray(offset, bytes, 0, length);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? length : end);
    }

    private static string? FindBeside(string fileName, string repositoryFolder, string? subfolder = null)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, subfolder ?? "", fileName);
        if (File.Exists(beside)) return beside;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var built = Path.Combine(folder.FullName, repositoryFolder, fileName);
            if (File.Exists(built)) return built;
        }
        return null;
    }
}
