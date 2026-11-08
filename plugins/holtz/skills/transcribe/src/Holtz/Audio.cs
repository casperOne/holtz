using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Holtz;

/// <summary>A (start, stop) stretch of the recording, in seconds.</summary>
public readonly record struct Span(double Start, double Stop)
{
    public double Duration => Stop - Start;
}

/// <summary>A loudness measurement: the window starting at <paramref name="Moment"/> and its RMS
/// level in dBFS (negative infinity for digital silence).</summary>
public readonly record struct Level(double Moment, double Db);

/// <summary>Measuring and cutting the recording with ffmpeg. The measuring is the whole method:
/// silence is found by measurement, per window, and never by averaging across a recording.</summary>
public static partial class Audio
{
    public const int ProfileSampleRate = 16000;

    /// <summary>Find ffmpeg/ffprobe: an explicit folder, then <c>FFMPEG_DIR</c>, then PATH.</summary>
    public static string? Find(string name, string? folder = null)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in new[] { folder, Environment.GetEnvironmentVariable("FFMPEG_DIR") })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir.Trim('"'), exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string Require(string name) =>
        Find(name) ?? throw new TranscribeException(
            $"{name} is not on PATH. This needs ffmpeg and ffprobe to measure and cut the audio "
            + "(Windows: `winget install Gyan.FFmpeg`, then open a new terminal; or set FFMPEG_DIR "
            + "to the folder holding ffmpeg.exe).");

    /// <summary>Run a tool to completion and capture both streams. Arguments are passed as a list,
    /// never a joined string, so a path with spaces needs no quoting on any OS.</summary>
    public static (int Code, string Stdout, string Stderr) RunTool(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new TranscribeException($"Could not start {exe}.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>The command line as it would be typed, for the provenance record.</summary>
    public static string Describe(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    private static string RunFfmpeg(IReadOnlyList<string> args)
    {
        var exe = Require("ffmpeg");
        var (code, _, stderr) = RunTool(exe, args);
        if (code != 0)
        {
            var tail = string.Join('\n', stderr.Trim().Split('\n').TakeLast(5));
            throw new TranscribeException($"ffmpeg failed ({code}):\n{tail}");
        }
        return stderr;
    }

    /// <summary>Total seconds of the recording, from ffprobe.</summary>
    public static double ProbeDuration(string audio)
    {
        var (code, stdout, _) = RunTool(Require("ffprobe"),
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", audio]);
        if (code != 0 || !double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Float, Fmt.Inv, out var seconds))
            throw new TranscribeException($"ffprobe could not read a duration from {audio}.");
        return seconds;
    }

    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end:\s*(-?[\d.]+)")]
    private static partial Regex SilenceEnd();

    /// <summary>ffmpeg <c>silencedetect</c> output to silence runs. A silence running to the end
    /// of the file has a start and no end, so it is closed at the duration; a start is clamped
    /// at zero because ffmpeg occasionally reports a tiny negative one.</summary>
    public static List<Span> ParseSilencedetect(string stderr, double duration)
    {
        var runs = new List<Span>();
        double? pending = null;
        foreach (var line in stderr.Split('\n'))
        {
            var start = SilenceStart().Match(line);
            if (start.Success)
            {
                pending = Math.Max(0, double.Parse(start.Groups[1].Value, Fmt.Inv));
                continue;
            }
            var end = SilenceEnd().Match(line);
            if (end.Success && pending is { } begin)
            {
                var stop = Math.Min(duration, double.Parse(end.Groups[1].Value, Fmt.Inv));
                if (stop > begin) runs.Add(new Span(begin, stop));
                pending = null;
            }
        }
        if (pending is { } open && duration > open) runs.Add(new Span(open, duration));
        return runs;
    }

    /// <summary>Silence runs of at least <paramref name="minSilence"/> seconds, and the command.</summary>
    public static (List<Span> Runs, string Command) DetectSilences(string audio, double duration, double noiseDb, double minSilence)
    {
        string[] args = ["-nostdin", "-hide_banner", "-nostats", "-i", audio,
            "-af", $"silencedetect=noise={Fmt.G(noiseDb)}dB:d={Fmt.G(minSilence)}", "-f", "null", "-"];
        return (ParseSilencedetect(RunFfmpeg(args), duration), Describe("ffmpeg", args));
    }

    [GeneratedRegex(@"pts_time:\s*([\d.]+)")]
    private static partial Regex AstatsTime();

    [GeneratedRegex(@"lavfi\.astats\.Overall\.RMS_level\s*=\s*(-?[\d.]+|-?inf|nan)")]
    private static partial Regex AstatsRms();

    /// <summary><c>astats</c>/<c>ametadata</c> output to per-window levels. Digital silence is
    /// carried through as negative infinity, so "nothing at all" stays distinguishable from
    /// "very quiet".</summary>
    public static List<Level> ParseAstats(string output)
    {
        var windows = new List<Level>();
        double? current = null;
        foreach (var line in output.Split('\n'))
        {
            var moment = AstatsTime().Match(line);
            if (moment.Success)
            {
                current = double.Parse(moment.Groups[1].Value, Fmt.Inv);
                continue;
            }
            var level = AstatsRms().Match(line);
            if (level.Success && current is { } at)
            {
                var token = level.Groups[1].Value;
                if (token != "nan")
                    windows.Add(new Level(at, token.EndsWith("inf") ? double.NegativeInfinity : double.Parse(token, Fmt.Inv)));
                current = null;
            }
        }
        return windows;
    }

    /// <summary>Per-window RMS level across the whole recording. <c>astats</c>'s own reset counts
    /// decoder frames, not seconds (1024 samples, 64 ms, for typical input), so asking it for a
    /// 5-second window that way silently yields 64 ms windows. Resampling to a known rate and
    /// re-framing with <c>asetnsamples</c> makes one frame exactly one window.</summary>
    public static List<Level> LoudnessProfile(string audio, double window)
    {
        var samples = Math.Max(1, (int)Math.Round(ProfileSampleRate * window));
        string[] args = ["-nostdin", "-hide_banner", "-nostats", "-i", audio, "-af",
            $"aresample={ProfileSampleRate},asetnsamples=n={samples}:p=0,astats=metadata=1:reset=1,"
            + "ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-",
            "-f", "null", "-"];
        var (code, stdout, stderr) = RunTool(Require("ffmpeg"), args);
        if (code != 0)
            throw new TranscribeException($"ffmpeg astats failed ({code}):\n" + string.Join('\n', stderr.Trim().Split('\n').TakeLast(5)));
        // ametadata's file=- writes to stdout; older builds fold it into stderr.
        var parsed = ParseAstats(stdout);
        return parsed.Count > 0 ? parsed : ParseAstats(stderr);
    }

    /// <summary>Extract one window as 16 kHz mono WAV, which is what whisper wants anyway. Input
    /// seeking with an explicit duration and a re-encode, so the clip starts exactly where the
    /// plan says: a frame of drift would make the assembled timestamps disagree with the source.</summary>
    public static string CutClip(string audio, double start, double stop, string outPath)
    {
        string[] args = ["-nostdin", "-hide_banner", "-nostats", "-y",
            "-ss", Fmt.F(start, 3), "-t", Fmt.F(stop - start, 3), "-i", audio,
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outPath];
        RunFfmpeg(args);
        return Describe("ffmpeg", args);
    }
}
