using System.Text.Json.Nodes;

namespace Holtz;

/// <summary>Thresholds. The defaults are the ones three hand-recovered recordings supported
/// (phone audio of two people at a table); every one is a flag. Deliberately conservative: a
/// cut that is not needed costs a little transcription time, a cut that is missed costs the
/// evidence. See reference/design-notes.md for the measurements behind each.</summary>
public sealed record PlanSettings
{
    /// <summary>Speech measured -13 to -25 dBFS and true silence -62 to -73; -40 sits between.</summary>
    public double NoiseDb { get; init; } = -40.0;
    /// <summary>Whisper's window is 30 s and it is silence longer than that which seeds a loop.</summary>
    public double MinSilence { get; init; } = 20.0;
    public double PreRoll { get; init; } = 0.35;
    public double Tail { get; init; } = 0.75;
    /// <summary>A candidate clip whose LOUDEST second stays under this is noise, not speech.</summary>
    public double MinPeakDb { get; init; } = -35.0;
    /// <summary>A sound between two silences whose loudest second stays under this is bridged,
    /// so the two count as one silence. Stricter than <see cref="MinPeakDb"/> on purpose.</summary>
    public double BridgePeakDb { get; init; } = -38.0;
    /// <summary>Optional window of the recording to transcribe (re-doing one stretch). Times stay
    /// on the recording's own timeline.</summary>
    public double? From { get; init; }
    public double? To { get; init; }

    public const double FineSilenceSeconds = 1.0;
    public const double MinSpeechSeconds = 1.5;
    public const double ProfileStepSeconds = 1.0;
    public const double DefaultProfileWindow = 5.0;
}

/// <summary>One stretch of the recording to transcribe on its own.</summary>
public sealed record Clip(int Index, double Start, double Stop, double SpeechStart, double SpeechStop)
{
    /// <summary>Clip ids are namespaced by multiples of this so a mapping can name them unambiguously.</summary>
    public const int SpeakerNamespace = 100;

    public double Duration => Stop - Start;
    public string Name => $"clip_{Index:00}_{Fmt.Stamp(Start)}-{Fmt.Stamp(Stop)}";
    /// <summary>Clip 1 keeps the diarizer's own ids; later clips are moved away from them.</summary>
    public int SpeakerOffset => (Index - 1) * SpeakerNamespace;

    public JsonObject ToJson() => new()
    {
        ["index"] = Index,
        ["name"] = Name,
        ["start"] = Math.Round(Start, 3),
        ["stop"] = Math.Round(Stop, 3),
        ["duration"] = Math.Round(Duration, 3),
        ["speaker_offset"] = SpeakerOffset,
        ["window"] = $"{Fmt.Clock(Start)}-{Fmt.Clock(Stop)}",
    };
}

/// <summary>What will be transcribed, what will be skipped, and the numbers behind both.</summary>
public sealed record Plan(
    double Duration,
    List<Clip> Clips,
    List<Span> Skipped,
    PlanSettings Settings,
    List<(Span Span, double PeakDb)> TooQuiet,
    List<(Span Span, double PeakDb)> Bridged)
{
    public double Covered => Clips.Sum(c => c.Duration);

    public JsonObject ToJson()
    {
        static JsonObject Window(Span s) => new()
        {
            ["start"] = Math.Round(s.Start, 3),
            ["stop"] = Math.Round(s.Stop, 3),
            ["window"] = $"{Fmt.Clock(s.Start)}-{Fmt.Clock(s.Stop)}",
        };
        return new JsonObject
        {
            ["duration"] = Math.Round(Duration, 3),
            ["covered_seconds"] = Math.Round(Covered, 3),
            ["settings"] = new JsonObject
            {
                ["noise_db"] = Settings.NoiseDb,
                ["min_silence"] = Settings.MinSilence,
                ["pre_roll"] = Settings.PreRoll,
                ["tail"] = Settings.Tail,
                ["min_peak_db"] = Settings.MinPeakDb,
                ["bridge_peak_db"] = Settings.BridgePeakDb,
                ["from"] = Settings.From,
                ["to"] = Settings.To,
            },
            ["clips"] = new JsonArray(Clips.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["dropped_too_quiet"] = new JsonArray(TooQuiet.Select(q =>
            {
                var o = Window(q.Span);
                o["peak_db"] = Math.Round(q.PeakDb, 1);
                return (JsonNode)o;
            }).ToArray()),
            ["bridged_inside_silences"] = new JsonArray(Bridged.Select(b =>
            {
                var o = Window(b.Span);
                o["peak_db"] = Math.Round(b.PeakDb, 1);
                return (JsonNode)o;
            }).ToArray()),
            ["skipped_silences"] = new JsonArray(Skipped.Select(s =>
            {
                var o = Window(s);
                o["duration"] = Math.Round(s.Duration, 3);
                return (JsonNode)o;
            }).ToArray()),
        };
    }
}

/// <summary>Turning measurements into clips that each begin on speech. The whole mechanism: whisper
/// conditions each window on the text it just emitted, so a window that opens on silence makes
/// it emit filler and then feed on its own filler. Never hand it one.</summary>
public static class Planner
{
    /// <summary>Advance <paramref name="start"/> past any silence it lands inside. silencedetect does
    /// not guarantee a clip opens on speech: one blip inside a long quiet stretch splits it into
    /// runs too short to cut on, and the clip opens in dead air (measured: a boundary at 14:32 with
    /// silence until 15:02, and one sentence hallucinated 99 times). A gap under
    /// <paramref name="minSpeech"/> between two silences is a blip, not speech resuming.</summary>
    public static double FirstSpeechAfter(double start, IEnumerable<Span> fineSilences, double minSpeech = PlanSettings.MinSpeechSeconds)
    {
        var runs = fineSilences.OrderBy(r => r.Start).ThenBy(r => r.Stop).ToList();
        var moved = start;
        var progressed = true;
        while (progressed) // `moved` strictly increases, so this terminates
        {
            progressed = false;
            foreach (var (begin, end) in runs)
            {
                if (begin <= moved && moved < end) { moved = end; progressed = true; }
                else if (moved < begin && begin < moved + minSpeech) { moved = end; progressed = true; }
            }
        }
        return moved;
    }

    /// <summary>Loudest level measured inside a window, or null. Peak, never mean: a stretch that is
    /// silent apart from one real sentence must read as speech.</summary>
    public static double? PeakDb(IReadOnlyList<Level> windows, double start, double stop)
    {
        double? peak = null;
        foreach (var (moment, db) in windows)
            if (moment < stop && moment + PlanSettings.ProfileStepSeconds > start && (peak is null || db > peak))
                peak = db;
        return peak;
    }

    /// <summary>Join fine silence runs across sounds too quiet to be speech. Without this a pencil
    /// every few seconds hides a five-minute silence from the coarse pass entirely (measured: a
    /// 39-minute single clip with a 09:10-14:05 silence inside it). Bridged on measured loudness
    /// only, never on length: a short "No." inside a long silence is exactly the line that must
    /// not be lost.</summary>
    public static (List<Span> Joined, List<(Span Span, double PeakDb)> Bridged) BridgeNoiseBlips(
        IEnumerable<Span> fineSilences, IReadOnlyList<Level>? loudness, double bridgePeakDb)
    {
        var merged = new List<Span>();
        var bridged = new List<(Span, double)>();
        foreach (var run in fineSilences.OrderBy(r => r.Start).ThenBy(r => r.Stop))
        {
            if (merged.Count > 0 && loudness is { Count: > 0 })
            {
                var prev = merged[^1];
                if (run.Start <= prev.Stop) { merged[^1] = prev with { Stop = Math.Max(prev.Stop, run.Stop) }; continue; }
                if (PeakDb(loudness, prev.Stop, run.Start) is { } gapPeak && gapPeak < bridgePeakDb)
                {
                    merged[^1] = prev with { Stop = Math.Max(prev.Stop, run.Stop) };
                    bridged.Add((new Span(prev.Stop, run.Start), gapPeak));
                    continue;
                }
            }
            merged.Add(run);
        }
        return (merged, bridged);
    }

    public static List<Span> MergeRuns(IEnumerable<Span> runs)
    {
        var output = new List<Span>();
        foreach (var run in runs.OrderBy(r => r.Start).ThenBy(r => r.Stop))
        {
            if (output.Count > 0 && run.Start <= output[^1].Stop)
                output[^1] = output[^1] with { Stop = Math.Max(output[^1].Stop, run.Stop) };
            else output.Add(run);
        }
        return output;
    }

    /// <summary>Complement the silences into clips that each begin on speech: a pre-roll before the
    /// first word, a tail after the last, and the long silences between them never transcribed.
    /// With no qualifying silence the answer is one clip, the whole file. Given loudness, a
    /// candidate whose loudest second never reaches the speech floor is dropped before it is
    /// numbered: crossing the silence threshold once is not the same as containing speech.</summary>
    public static Plan Build(
        double duration,
        IEnumerable<Span> silences,
        PlanSettings settings,
        IReadOnlyList<Span>? fineSilences = null,
        IReadOnlyList<Level>? loudness = null)
    {
        var silenceList = silences.ToList();
        var bridgedBlips = new List<(Span Span, double PeakDb)>();
        if (fineSilences is { Count: > 0 } && loudness is { Count: > 0 })
        {
            var (joined, blips) = BridgeNoiseBlips(fineSilences, loudness, settings.BridgePeakDb);
            var longRuns = joined.Where(r => r.Duration >= settings.MinSilence).ToList();
            bridgedBlips = blips.Where(b => longRuns.Any(r => r.Start <= b.Span.Start && b.Span.Stop <= r.Stop)).ToList();
            silenceList = MergeRuns(silenceList.Concat(longRuns));
        }
        var sorted = silenceList.OrderBy(s => s.Start).ThenBy(s => s.Stop).ToList();

        var speech = new List<Span>();
        var cursor = 0.0;
        foreach (var (start, stop) in sorted)
        {
            if (start > cursor) speech.Add(new Span(cursor, start));
            cursor = Math.Max(cursor, stop);
        }
        if (cursor < duration) speech.Add(new Span(cursor, duration));

        // A requested window keeps only the speech inside it, and is honoured exactly: no
        // pre-roll reaches back before `from`, no tail runs past `to`.
        var lower = settings.From ?? 0.0;
        var upper = Math.Min(duration, settings.To ?? duration);

        var clips = new List<Clip>();
        var tooQuiet = new List<(Span, double)>();
        foreach (var region in speech)
        {
            var speechStart = Math.Max(region.Start, lower);
            var speechStop = Math.Min(region.Stop, upper);
            if (speechStart >= speechStop) continue;
            // Move to where speech truly resumes before the pre-roll, so the pre-roll buys a beat
            // of lead-in rather than a beat more silence. A forced `from` is not moved.
            if (settings.From is null || speechStart > settings.From)
                speechStart = FirstSpeechAfter(speechStart, fineSilences ?? []);
            if (speechStart >= speechStop) continue;
            var clipStart = Math.Max(lower, speechStart - settings.PreRoll);
            var clipStop = Math.Min(upper, speechStop + settings.Tail);
            if (clipStop <= clipStart) continue;
            if (loudness is { Count: > 0 } && PeakDb(loudness, clipStart, clipStop) is { } peak && peak < settings.MinPeakDb)
            {
                tooQuiet.Add((new Span(clipStart, clipStop), peak));
                continue;
            }
            clips.Add(new Clip(clips.Count + 1, clipStart, clipStop, speechStart, speechStop));
        }

        var skipped = sorted
            .Where(s => s.Stop > lower && s.Start < upper)
            .ToList();
        return new Plan(duration, clips, skipped, settings, tooQuiet, bridgedBlips);
    }

    /// <summary>One skipped silence's bridged sounds: how many, and the loudest (the one to listen
    /// to first, since a real line that quiet would be lost).</summary>
    public static string BridgedSummary(Plan plan, Span silence)
    {
        var inside = plan.Bridged.Where(b => silence.Start <= b.Span.Start && b.Span.Stop <= silence.Stop).ToList();
        if (inside.Count == 0) return "";
        var loudest = inside.MaxBy(b => b.PeakDb);
        return $"  {inside.Count} sound(s) bridged, loudest {Fmt.F(loudest.PeakDb, 1)} dBFS at {Fmt.Clock(loudest.Span.Start)}";
    }

    public static string Render(Plan plan)
    {
        var s = plan.Settings;
        var lines = new List<string>
        {
            $"Recording: {Fmt.Clock(plan.Duration)} ({Fmt.F(plan.Duration, 1)} s)  |  silence threshold {Fmt.G(s.NoiseDb)} dBFS, cut at runs >= {Fmt.G(s.MinSilence)} s"
            + (s.From is not null || s.To is not null ? $"  |  window {Fmt.Clock(s.From ?? 0)}-{Fmt.Clock(s.To ?? plan.Duration)}" : ""),
            "",
            $"{plan.Clips.Count} clip(s) to transcribe, {Fmt.Clock(plan.Covered)} of audio ({Fmt.Percent(plan.Covered / plan.Duration)} of the recording):",
        };
        foreach (var clip in plan.Clips)
            lines.Add($"  {clip.Index,2}. {Fmt.Clock(clip.Start)}-{Fmt.Clock(clip.Stop)}  ({clip.Duration,6:F1} s)  speaker ids +{clip.SpeakerOffset}  {clip.Name}");
        if (plan.TooQuiet.Count > 0)
        {
            lines.Add("");
            lines.Add($"{plan.TooQuiet.Count} stretch(es) dropped as noise — loud enough to cross the silence threshold,");
            lines.Add($"never loud enough to be speech (peak under {Fmt.G(s.MinPeakDb)} dBFS):");
            foreach (var (span, peak) in plan.TooQuiet)
                lines.Add($"      {Fmt.Clock(span.Start)}-{Fmt.Clock(span.Stop)}  ({span.Duration,6:F1} s)  peak {Fmt.F(peak, 1)} dBFS");
        }
        if (plan.Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"{plan.Skipped.Count} silence run(s) skipped (not transcribed):");
            foreach (var span in plan.Skipped)
                lines.Add($"      {Fmt.Clock(span.Start)}-{Fmt.Clock(span.Stop)}  ({span.Duration,6:F1} s)" + BridgedSummary(plan, span));
            if (plan.Bridged.Count > 0)
            {
                lines.Add($"      (bridged: a sound whose loudest second stays under {Fmt.G(s.BridgePeakDb)} dBFS, counted as");
                lines.Add("       part of the silence around it; each is listed in transcript-assembly.json)");
            }
        }
        else
        {
            lines.Add("");
            lines.Add("No qualifying silence: the recording is transcribed in one pass.");
        }
        if (plan.Clips.Count > 1)
        {
            lines.Add("");
            lines.Add("Each clip is diarized independently, so its speaker ids mean nothing to the others;");
            lines.Add("they are namespaced by clip and every one must be mapped.");
        }
        return string.Join('\n', lines);
    }

    /// <summary>The loudness map, one line per run of similar level. Read the runs, never the
    /// average: averaging a long silence with the talk around it reports a broken recording as fine.</summary>
    public static string RenderProfile(IReadOnlyList<Level> windows, double window, double noiseDb)
    {
        if (windows.Count == 0) return "No loudness windows measured.";
        var runs = new List<(double Start, List<double> Levels)>();
        foreach (var (moment, db) in windows)
        {
            var loud = db > noiseDb;
            if (runs.Count > 0 && runs[^1].Levels[0] > noiseDb == loud) runs[^1].Levels.Add(db);
            else runs.Add((moment, [db]));
        }
        var lines = new List<string> { $"Loudness profile ({Fmt.G(window)} s windows, speech threshold {Fmt.G(noiseDb)} dBFS)", "" };
        for (var i = 0; i < runs.Count; i++)
        {
            var (start, levels) = runs[i];
            var stop = i + 1 < runs.Count ? runs[i + 1].Start : windows[^1].Moment + window;
            var finite = levels.Where(l => !double.IsNegativeInfinity(l)).ToList();
            var span = finite.Count > 0 ? $"{Fmt.F(finite.Min(), 0)} to {Fmt.F(finite.Max(), 0)} dBFS" : "digital silence";
            var label = levels[0] > noiseDb ? "speech " : "SILENT ";
            lines.Add($"{Fmt.Clock(start)} - {Fmt.Clock(stop)}   {label}  ({span})");
        }
        lines.Add("");
        lines.Add("Read the runs, never the average: averaging a long silence with the talk around it");
        lines.Add("reports a broken recording as fine.");
        return string.Join('\n', lines);
    }
}
