namespace Holtz;

/// <summary>One quality-gate hit. <paramref name="RecutFrom"/> is the point of the structure: most
/// findings name the second (on the cues' own timeline) where the model lost its place, which
/// is what lets a clip be re-cut from there automatically instead of by hand. Null means the
/// finding is about the whole stretch and names no window.</summary>
public sealed record Finding(string Kind, string Message, double? RecutFrom = null);

/// <summary>Checks for the mechanical signature of a whisper transcription failure. A transcript
/// can pass all of them and still be wrong; these catch only the failure that is visible in the
/// text itself: the model was handed audio it could not hear and filled it with silence tokens,
/// or it started feeding on its own output and looped.</summary>
public static class QualityGate
{
    /// <summary>Whisper emits these verbatim over silence. One is normal; a run of them, or a clip
    /// made of nothing else, means the model was fed something it could not hear.</summary>
    public static readonly HashSet<string> HallucinationStrings =
        ["thank you.", "thanks for watching!", "thank you for watching.", "you", "."];

    // Real conversation runs 100-140 words/min; a dead stretch measured 6.1. Only judged over
    // a long enough span: a three-minute quiet stretch is legitimately sparse.
    public const double MinWordsPerMinute = 30.0;
    public const double MinRateableSeconds = 300.0;
    // Whisper's 30 s fallback window: one segment per window when it finds no speech timestamps.
    public const double FallbackWindowSeconds = 25.0;
    public const double UniformSegmentFraction = 0.5;
    // A run of identical silence tokens this long is a loop, not a coincidence.
    public const int MinHallucinationRun = 5;
    // A real phrase looping without adjacency: healthy recordings top out at ~3 repeats and ~1%;
    // a looping pass measured 361 repeats and 38.5%. Short backchannel legitimately repeats.
    public const int LoopMinWords = 4;
    public const int LoopMinRepeats = 5;
    public const double LoopMinShare = 0.05;
    // Short lines cycling ("Okay." / "So that's 1,200." alternating): too short for the phrase
    // check, not adjacent for the run check. Real loops ran 17-223 lines; healthy speech <= 8.
    public const int CycleMaxPeriod = 4;
    public const int MinCycleLines = 12;
    // Share of lines repeating an earlier line: healthy 0.19-0.26, a looping pass 0.86.
    public const double MaxDuplicateFraction = 0.5;
    // A short loop inside an otherwise good clip. Over 30 real transcripts (8,704 lines) these
    // settings hit 10 places, including every short loop a second engine exposed in one recording.
    public const int LocalLoopMinWords = 3;
    public const int LocalLoopMaxGap = 4;
    public const int LocalLoopMinHits = 3;

    private static string Key(Cue cue) => Fmt.Squash(cue.Text).ToLowerInvariant();

    public static List<string> Assess(IReadOnlyList<Cue> cues) => Findings(cues).Select(f => f.Message).ToList();

    /// <summary>The longest stretch where every line repeats the line <c>period</c> lines earlier:
    /// (repeating lines, index of the cycle's first line, period), or null. The start is walked
    /// back over directly preceding copies of the cycle's lines, so a loop that slipped its phase
    /// once is re-cut from where it really began.</summary>
    public static (int Lines, int Start, int Period)? LongestCycle(
        IReadOnlyList<Cue> cues, int maxPeriod = CycleMaxPeriod, int minLines = MinCycleLines)
    {
        var texts = cues.Select(Key).ToList();
        (int Lines, int Start, int Period)? best = null;
        for (var period = 1; period <= maxPeriod; period++)
        {
            var run = 0;
            for (var j = period; j < texts.Count; j++)
            {
                if (texts[j].Length > 0 && texts[j] == texts[j - period])
                {
                    run++;
                    if (run >= minLines && (best is null || run > best.Value.Lines))
                        best = (run, j - run + 1 - period, period);
                }
                else run = 0;
            }
        }
        if (best is not { } found) return null;
        var (lines, start, p) = found;
        var members = texts.Skip(start).Take(p).ToHashSet();
        while (start > 0 && members.Contains(texts[start - 1])) { start--; lines++; }
        return (lines, start, p);
    }

    /// <summary>The checks that still mean something on one stretch of a clip. Share-of-clip checks are
    /// diluted by a long clip (5 copies of a line in 275 is under 2%), so a stretch kept across a
    /// re-cut is judged alone. Empty unless the stretch is nothing but silence tokens, or holds a
    /// loop AND is mostly repeats: a small repeat beside good lines must not cost the good lines
    /// (<see cref="LocalLoops"/> flags it instead).</summary>
    public static List<Finding> StretchFindings(IReadOnlyList<Cue> stretch)
    {
        var findings = Findings(stretch);
        var loop = findings.Any(f => f.Kind is "silence_hallucination" or "phrase_loop" or "cycle_loop");
        var mostlyRepeats = findings.Any(f => f.Kind == "duplicate_share");
        return findings.Any(f => f.Kind == "all_hallucination") || (loop && mostlyRepeats) ? findings : [];
    }

    /// <summary>Lines of at least <see cref="LocalLoopMinWords"/> words repeated at least
    /// <see cref="LocalLoopMinHits"/> times, each within <see cref="LocalLoopMaxGap"/> lines of the
    /// last: (first index, last index, repeats), in order. For flagging, never for rejecting: too
    /// short for every gate check, and people do repeat themselves. Compared on letters and digits
    /// only, so punctuation variants of one line count together.</summary>
    public static List<(int First, int Last, int Hits)> LocalLoops(IReadOnlyList<Cue> cues)
    {
        var keys = cues.Select(c => new string(Fmt.Squash(c.Text).ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray())).ToList();
        var used = new HashSet<int>();
        var loops = new List<(int, int, int)>();
        for (var i = 0; i < keys.Count; i++)
        {
            if (used.Contains(i) || keys[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < LocalLoopMinWords) continue;
            var hits = new List<int> { i };
            for (var j = i + 1; j < keys.Count && j - hits[^1] <= LocalLoopMaxGap; j++)
                if (keys[j] == keys[i]) hits.Add(j);
            if (hits.Count < LocalLoopMinHits) continue;
            used.UnionWith(hits);
            loops.Add((i, hits[^1], hits.Count));
        }
        return loops;
    }

    /// <summary>Every check, keeping the window each one names. Most actionable first.</summary>
    public static List<Finding> Findings(IReadOnlyList<Cue> cues)
    {
        var findings = new List<Finding>();
        if (cues.Count == 0) return findings;

        // 1. A run of one silence token. Names its window, so it goes first.
        (int Count, string Key, string Display, double Start, double Stop) longest = (0, "", "", 0, 0);
        (int Count, string Key, string Display, double Start, double Stop)? run = null;
        foreach (var cue in cues)
        {
            var key = Key(cue);
            if (run is { } r && key == r.Key) run = r with { Count = r.Count + 1, Stop = cue.Stop };
            else run = (1, key, Fmt.Squash(cue.Text), cue.Start, cue.Stop);
            if (run.Value.Count > longest.Count && HallucinationStrings.Contains(run.Value.Key)) longest = run.Value;
        }
        if (longest.Count >= MinHallucinationRun)
            findings.Add(new Finding("silence_hallucination",
                $"transcription failure: \"{longest.Display}\" repeats {longest.Count} times between "
                + $"{Fmt.Time(longest.Start)} and {Fmt.Time(longest.Stop)}. That is a silence "
                + $"hallucination, not the recording. Re-cut from {Fmt.F(longest.Start, 0)} and re-transcribe.",
                longest.Start));

        // 1b. Nothing BUT silence tokens. The run check needs five in a row, so a short clip that
        // is pure fabrication sails past it. Length is irrelevant: if every line is a silence
        // token, none of it is the recording, and re-cutting silence finds more silence.
        var spoken = cues.Select(c => Fmt.Squash(c.Text)).Where(t => t.Length > 0).ToList();
        if (spoken.Count > 0 && spoken.All(t => HallucinationStrings.Contains(t.ToLowerInvariant())))
        {
            var shown = string.Join(", ", spoken.Select(t => $"\"{t}\"").Distinct());
            findings.Add(new Finding("all_hallucination",
                $"transcription failure: every line between {Fmt.Time(cues[0].Start)} and "
                + $"{Fmt.Time(cues[^1].Stop)} is a known silence token ({shown}). Whisper was given "
                + "audio with no speech in it, so there is nothing here to recover by re-cutting."));
        }

        var texts = spoken;
        if (texts.Count > 0)
        {
            // 2. A real phrase looping without adjacency.
            var phrase = texts.Where(t => t.Split(' ').Length >= LoopMinWords)
                .GroupBy(t => t.ToLowerInvariant())
                .Select(g => (Phrase: g.Key, Hits: g.Count()))
                .DefaultIfEmpty()
                .MaxBy(g => g.Hits);
            if (phrase.Phrase is not null)
            {
                var share = (double)phrase.Hits / texts.Count;
                if (phrase.Hits >= LoopMinRepeats && share >= LoopMinShare)
                {
                    var where = cues.Where(c => Key(c) == phrase.Phrase).ToList();
                    var first = where[0].Start;
                    var shownPhrase = phrase.Phrase.Length > 60 ? phrase.Phrase[..60] : phrase.Phrase;
                    findings.Add(new Finding("phrase_loop",
                        $"transcription failure: \"{shownPhrase}\" repeats {phrase.Hits} times "
                        + $"({Fmt.Percent(share)} of the transcript) between {Fmt.Time(first)} and "
                        + $"{Fmt.Time(where[^1].Stop)}. Whisper looped on a real phrase and stopped "
                        + $"transcribing. Re-cut from {Fmt.F(first, 0)} and re-transcribe.",
                        first));
                }
            }

            // 2b. A short-line cycle: the window the duplicate share below cannot name.
            if (LongestCycle(cues) is { } cycle)
            {
                var (lines, firstIndex, period) = cycle;
                var first = cues[firstIndex].Start;
                var last = cues[Math.Min(cues.Count - 1, firstIndex + period + lines - 1)].Stop;
                var shown = string.Join(" / ", cues.Skip(firstIndex).Take(period).Select(c =>
                {
                    var t = Fmt.Squash(c.Text);
                    return $"\"{(t.Length > 40 ? t[..40] : t)}\"";
                }));
                findings.Add(new Finding("cycle_loop",
                    $"transcription failure: {shown} cycles for {lines + period} lines between "
                    + $"{Fmt.Time(first)} and {Fmt.Time(last)}. Whisper looped on short lines and "
                    + $"stopped transcribing. Re-cut from {Fmt.F(first, 0)} and re-transcribe.",
                    first));
            }

            // Duplicate share: the clip-wide symptom, which names no window.
            var duplicated = texts.GroupBy(t => t.ToLowerInvariant()).Sum(g => g.Count() - 1);
            var fraction = (double)duplicated / texts.Count;
            if (fraction >= MaxDuplicateFraction)
                findings.Add(new Finding("duplicate_share",
                    $"{Fmt.Percent(fraction)} of segments repeat an earlier segment (healthy recordings "
                    + "run 19-26%). Most of this transcript is the model repeating itself."));
        }

        // 3. Word rate: the most robust single signal that a transcript is not the recording.
        var covered = cues.Max(c => c.Stop) - cues.Min(c => c.Start);
        var words = cues.Sum(c => c.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (covered >= MinRateableSeconds)
        {
            var rate = words / (covered / 60.0);
            if (rate < MinWordsPerMinute)
                findings.Add(new Finding("word_rate",
                    $"word rate is {Fmt.F(rate, 0)}/min over {Fmt.Time(covered)} (real conversation "
                    + "runs 100-140). Most of this stretch did not transcribe."));
        }

        // 4. Whisper's fixed fallback window: the segmentation is synthetic, so the timings are too.
        var modal = cues.Select(c => Math.Round(c.Stop - c.Start, 1))
            .GroupBy(d => d).Select(g => (Duration: g.Key, Hits: g.Count())).MaxBy(g => g.Hits);
        if (modal.Duration >= FallbackWindowSeconds && (double)modal.Hits / cues.Count >= UniformSegmentFraction)
            findings.Add(new Finding("fallback_window",
                $"{modal.Hits} of {cues.Count} segments are exactly {Fmt.G(modal.Duration)}s long. That is "
                + "whisper's fallback window, emitted when it finds no speech timestamps - the "
                + "segmentation is synthetic, so the timings are not real."));
        return findings;
    }
}
