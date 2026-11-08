using System.Text.Json;
using System.Text.Json.Nodes;

namespace Holtz;

/// <summary>What happened to one clip.</summary>
public sealed class ClipResult(Clip clip)
{
    public Clip Clip { get; } = clip;
    public string? RawPath { get; set; }
    public List<Cue> Cues { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? Error { get; set; }
    public List<(double From, string Raw)> Attempts { get; } = [];
    public List<string> Invented { get; } = [];
    public List<string> Suspect { get; set; } = [];
    public List<string> SuspectLoops { get; set; } = [];
    public List<(Span Span, string Reason)> RejectedStretches { get; } = [];

    public JsonObject ToJson()
    {
        var json = Clip.ToJson();
        json["raw"] = RawPath is null ? null : Path.GetFileName(RawPath);
        json["cues"] = Cues.Count;
        json["gate_warnings"] = new JsonArray(Warnings.Select(w => (JsonNode)w).ToArray());
        json["attempts"] = new JsonArray(Attempts.Select(a => (JsonNode)new JsonObject
        {
            ["from"] = Math.Round(a.From, 3),
            ["raw"] = a.Raw,
        }).ToArray());
        json["dropped_cues_past_audio"] = new JsonArray(Invented.Select(w => (JsonNode)w).ToArray());
        json["suspect_cues_kept"] = new JsonArray(Suspect.Select(w => (JsonNode)w).ToArray());
        json["suspect_loops_kept"] = new JsonArray(SuspectLoops.Select(w => (JsonNode)w).ToArray());
        json["rejected_stretches"] = new JsonArray(RejectedStretches.Select(r => (JsonNode)new JsonObject
        {
            ["start"] = Math.Round(r.Span.Start, 3),
            ["stop"] = Math.Round(r.Span.Stop, 3),
            ["reason"] = r.Reason,
        }).ToArray());
        json["error"] = Error;
        return json;
    }
}

public sealed record RunOptions
{
    public required string Audio { get; init; }
    public required string OutDir { get; init; }
    public PlanSettings Settings { get; init; } = new();
    public Dictionary<string, string> Fields { get; init; } = [];
    public bool AllowDegraded { get; init; }
    public bool KeepClipAudio { get; init; }
    public int MaxRetries { get; init; } = Pipeline.DefaultMaxRetries;
    public List<string> People { get; init; } = [];
    public string? ModelPath { get; init; }
    public string? GooglePath { get; init; }
}

/// <summary>Cut, transcribe, gate, recover, assemble.</summary>
public static class Pipeline
{
    /// <summary>A re-cut is its own diarization pass, so its ids get their own band inside the clip's
    /// namespace (+0, +10, +20): retries have come back with two people's ids swapped.</summary>
    public const int RetryNamespace = 10;
    /// <summary>A cue may end a hair past its audio from resampling; beyond this it describes audio
    /// the clip never held, which is not a judgment call.</summary>
    public const double CueOverrunTolerance = 0.5;
    public const int DefaultMaxRetries = 2;
    /// <summary>Never restart less than this far in, or a loop at the first second re-cuts forever.</summary>
    public const double MinRetryAdvance = 2.0;
    /// <summary>Below this there is not enough left to be worth another pass.</summary>
    public const double MinRetryRemainder = 5.0;
    public const int WorksheetQuotes = 4;

    /// <summary>Split cues into those the clip's audio could support and those it could not. A cue
    /// that ends past the audio is dropped WHOLE, not clamped: clamping would leave the same
    /// fabricated sentence behind a plausible span (measured: a 30.0 s cue reaching 28.1 s past a
    /// 241.3 s clip, reading "Thank you."). The raw response keeps every segment.</summary>
    public static (List<Cue> Kept, List<Cue> Dropped) DropCuesPastAudio(IEnumerable<Cue> cues, double audioSeconds, double tolerance = CueOverrunTolerance)
    {
        var kept = new List<Cue>();
        var dropped = new List<Cue>();
        foreach (var cue in cues) (cue.Stop > audioSeconds + tolerance ? dropped : kept).Add(cue);
        return (kept, dropped);
    }

    /// <summary>Flag (never drop) a clip's last cue when it is unlabeled and a known silence token.
    /// Every clip ends in near-silence, so every boundary is a place whisper can invent a line, but
    /// a genuine closing courtesy is just as plausible. A fabrication left in is visible to a reader;
    /// a real line deleted is not. So: keep, and say so.</summary>
    public static List<string> FlagTrailingFabrication(IReadOnlyList<Cue> cues)
    {
        if (cues.Count == 0) return [];
        var last = cues.MaxBy(c => c.Stop)!;
        if (last.Labeled) return [];
        var text = Fmt.Squash(last.Text);
        if (!QualityGate.HallucinationStrings.Contains(text.ToLowerInvariant())) return [];
        return [$"[{Fmt.Clock(last.Start)}] \"{text}\""];
    }

    /// <summary>Move cues onto the recording's timeline and namespace their ids. A named voice is
    /// left alone: there is no arithmetic to do on a name.</summary>
    public static List<Cue> Shift(IEnumerable<Cue> cues, double offset, int speakerOffset) =>
        cues.Select(c => c with
        {
            Start = c.Start + offset,
            Stop = c.Stop + offset,
            Speaker = c.Speaker is { } id ? id + speakerOffset : null,
        }).ToList();

    /// <summary>Transcribe one clip, re-cutting where the gate says the model lost its place. Whisper's
    /// loop feeds on its own output, so restarting inside the damaged stretch usually breaks it.
    /// Everything before that point is kept, every attempt's raw response is kept, and the combined
    /// result is re-gated, so a clip that is still bad is still reported as bad.</summary>
    public static ClipResult TranscribeWithRecovery(
        VibeServer server, string audio, Clip clip, string clipsDir, RunOptions options, List<string> commands)
    {
        var result = new ClipResult(clip);
        var kept = new List<Cue>();
        var windowStart = clip.Start;

        for (var attempt = 1; attempt <= options.MaxRetries + 1; attempt++)
        {
            var suffix = attempt == 1 ? "" : $"_retry_from_{Fmt.Stamp(windowStart)}";
            var clipAudio = Path.Combine(clipsDir, clip.Name + suffix + ".wav");
            var windowLength = clip.Stop - windowStart;
            commands.Add(Audio.CutClip(audio, windowStart, clip.Stop, clipAudio));

            string raw;
            try { raw = server.Transcribe(clipAudio, options.Fields); }
            catch (TranscribeException ex)
            {
                result.Error = ex.Message;
                Console.Error.WriteLine($"  FAILED: {ex.Message}");
                return result;
            }
            finally
            {
                if (!options.KeepClipAudio) File.Delete(clipAudio);
            }

            var rawPath = Path.Combine(clipsDir, clip.Name + suffix + ".json");
            Fmt.WriteText(rawPath, raw);
            if (attempt == 1) result.RawPath = rawPath;
            result.Attempts.Add((windowStart, Path.GetFileName(rawPath)));

            var (cues, invented) = DropCuesPastAudio(Segments.ToCues(Segments.TryParse(raw)), windowLength);
            foreach (var cue in invented)
            {
                var note = $"dropped a cue ending {Fmt.F(cue.Stop - windowLength, 1)}s past the "
                    + $"{Fmt.F(windowLength, 1)}s of audio this clip held: \"{cue.Text}\"";
                result.Invented.Add(note);
                Console.Error.WriteLine($"  {note}");
            }
            if (cues.Count == 0)
            {
                result.Error = $"no segments in the response: {(raw.Length > 200 ? raw[..200] : raw)}";
                Console.Error.WriteLine($"  FAILED: {result.Error}");
                return result;
            }

            var shifted = Shift(cues, windowStart, (attempt - 1) * RetryNamespace);
            var combined = kept.Concat(shifted).ToList();
            var findings = QualityGate.Findings(combined);
            if (findings.Count == 0)
            {
                if (attempt > 1)
                {
                    combined = kept.Concat(AcceptStretch(shifted, windowStart, clip.Stop, result, options.AllowDegraded)).ToList();
                    if (combined.Count == 0)
                    {
                        result.Error = "every stretch of this clip was rejected: "
                            + string.Join("; ", result.RejectedStretches.Select(r => r.Reason));
                        Console.Error.WriteLine($"  FAILED: {result.Error}");
                        return result;
                    }
                }
                Finish(result, combined);
                Console.WriteLine($"  ok: {combined.Count} cues"
                    + (attempt > 1 ? $" (recovered by re-cutting from {Fmt.Clock(windowStart)})" : ""));
                return result;
            }

            // `combined` is on the RECORDING's timeline, so the gate's window is an absolute second,
            // never an offset from this clip's start.
            var boundary = findings.Where(f => f.RecutFrom is not null).Select(f => f.RecutFrom!.Value).DefaultIfEmpty(double.NaN).Min();
            double? restart = double.IsNaN(boundary) ? null : Math.Max(boundary, windowStart + MinRetryAdvance);
            var exhausted = attempt > options.MaxRetries;
            var tooShort = restart is null || clip.Stop - restart.Value < MinRetryRemainder;
            if (exhausted || tooShort)
            {
                Finish(result, combined);
                result.Warnings = findings.Select(f => f.Message).ToList();
                foreach (var message in result.Warnings) Console.Error.WriteLine($"  warning: {message}");
                return result;
            }

            // What this attempt got before it lost its place is judged on its own, before it joins a
            // long clip that would dilute it. A finding can also name a second inside earlier kept text.
            var stretch = shifted.Where(c => c.Start < boundary).ToList();
            kept = kept.Where(c => c.Start < boundary)
                .Concat(AcceptStretch(stretch, windowStart, boundary, result, options.AllowDegraded)).ToList();
            windowStart = restart!.Value;
            Console.WriteLine($"  gate hit at {Fmt.Clock(boundary)}; re-cutting from {Fmt.Clock(windowStart)} "
                + $"and retrying ({attempt}/{options.MaxRetries}) ...");
        }
        return result;
    }

    private static void Finish(ClipResult result, List<Cue> combined)
    {
        result.Cues = Shift(combined, 0, result.Clip.SpeakerOffset);
        result.Suspect = FlagTrailingFabrication(result.Cues);
        foreach (var line in result.Suspect) Console.Error.WriteLine($"  suspect, kept unverified: {line}");
        result.SuspectLoops = DescribeLocalLoops(result.Cues);
        foreach (var line in result.SuspectLoops) Console.Error.WriteLine($"  repeated, kept unverified: {line}");
    }

    /// <summary>Short repeats as readable lines, for flagging. Kept, not dropped: nothing here proves
    /// the words were not said, and a fabrication left in is visible while a real line deleted is not.</summary>
    public static List<string> DescribeLocalLoops(IReadOnlyList<Cue> cues)
    {
        var ordered = cues.OrderBy(c => c.Start).ThenBy(c => c.Stop).ToList();
        return QualityGate.LocalLoops(ordered)
            .Select(l => $"[{Fmt.Clock(ordered[l.First].Start)}-{Fmt.Clock(ordered[l.Last].Stop)}] \"{Fmt.Squash(ordered[l.First].Text)}\" x{l.Hits}")
            .ToList();
    }

    /// <summary>Judge one attempt's stretch alone; return the cues to keep. A stretch that fails is
    /// recorded and left out unless degraded output was asked for. The clip's raw response keeps it.
    /// Measured: 60 s of invented text over near-silence, 5 identical lines, passed once it was
    /// joined to the 275 good lines after it, because the phrase check is a share of the clip.</summary>
    internal static List<Cue> AcceptStretch(List<Cue> stretch, double from, double to, ClipResult result, bool allowDegraded)
    {
        var findings = QualityGate.StretchFindings(stretch);
        if (findings.Count == 0) return stretch;
        var reason = string.Join("; ", findings.Select(f => f.Message));
        result.RejectedStretches.Add((new Span(from, to), reason));
        Console.Error.WriteLine($"  stretch {Fmt.Clock(from)}-{Fmt.Clock(to)} rejected: {reason}");
        return allowDegraded ? stretch : [];
    }

    /// <summary>Merge the usable clips into one VTT on the recording's timeline. A clip the gate
    /// rejected is left out unless degraded output is asked for: a partly degraded transcript is
    /// still evidence, but it has to be asked for, and whoever reads it has to be told.</summary>
    public static (string Vtt, List<string> Excluded) Assemble(IReadOnlyList<ClipResult> results, string audio, Plan plan, bool allowDegraded)
    {
        var included = new List<ClipResult>();
        var excluded = new List<string>();
        foreach (var result in results)
        {
            if (result.Error is not null) excluded.Add($"clip {result.Clip.Index} ({result.Clip.Name}): {result.Error}");
            else if (result.Warnings.Count > 0 && !allowDegraded)
                excluded.Add($"clip {result.Clip.Index} ({result.Clip.Name}): " + string.Join("; ", result.Warnings));
            else included.Add(result);
        }

        var s = plan.Settings;
        var notes = new List<string>
        {
            "Assembled by holtz. This file is DERIVED:\n"
            + $"source audio {Path.GetFileName(audio)}, cut at silences and transcribed clip by clip.\n"
            + "The raw per-clip responses in clips/ are the evidence; see transcript-assembly.json.",
            "Speaker ids are namespaced by clip and are NOT comparable across clips:\n"
            + string.Join('\n', included.Select(r =>
                $"  clip {r.Clip.Index} {Fmt.Clock(r.Clip.Start)}-{Fmt.Clock(r.Clip.Stop)} -> ids +{r.Clip.SpeakerOffset}"
                + (r.Attempts.Count > 1
                    ? $" (re-cut from {string.Join(", ", r.Attempts.Skip(1).Select(a => Fmt.Clock(a.From)))} -> "
                      + string.Join(", ", Enumerable.Range(1, r.Attempts.Count - 1).Select(n => $"+{r.Clip.SpeakerOffset + n * RetryNamespace}")) + ")"
                    : ""))),
        };
        if (plan.TooQuiet.Count > 0)
            notes.Add("Dropped as noise, not transcribed (crossed the silence threshold but never\n"
                + $"reached speech level, peak under {Fmt.G(s.MinPeakDb)} dBFS):\n"
                + string.Join('\n', plan.TooQuiet.Select(q => $"  {Fmt.Clock(q.Span.Start)}-{Fmt.Clock(q.Span.Stop)} ({Fmt.F(q.Span.Duration, 0)}s) peak {Fmt.F(q.PeakDb, 1)} dBFS")));
        if (plan.Skipped.Count > 0)
            notes.Add($"Silence not transcribed (measured below {Fmt.G(s.NoiseDb)} dBFS for >= {Fmt.G(s.MinSilence)}s"
                + (plan.Bridged.Count > 0 ? $", sounds under {Fmt.G(s.BridgePeakDb)} dBFS bridged" : "") + "):\n"
                + string.Join('\n', plan.Skipped.Select(sp => $"  {Fmt.Clock(sp.Start)}-{Fmt.Clock(sp.Stop)} ({Fmt.F(sp.Duration, 0)}s)" + Planner.BridgedSummary(plan, sp))));
        var suspect = included.SelectMany(r => r.Suspect).ToList();
        if (suspect.Count > 0)
            notes.Add($"{Vtt.SuspectNotePrefix} — kept, but do not trust without checking the audio.\n"
                + "Each is a clip's last cue, unlabeled, and a phrase whisper is known to invent\n"
                + "over the near-silence every clip ends in. It may equally be someone being\n"
                + "polite, which is why it was kept rather than dropped:\n"
                + string.Join('\n', suspect.Select(l => "  " + l)));
        var loops = included.SelectMany(r => r.SuspectLoops).ToList();
        if (loops.Count > 0)
            notes.Add($"{Vtt.SuspectNotePrefix} — repeated lines, kept, but check them against the audio.\n"
                + "Each line recurs within a few lines of itself, too briefly for the quality gate.\n"
                + "That is how a short whisper loop looks. People also repeat themselves, so they stay:\n"
                + string.Join('\n', loops.Select(l => "  " + l)));
        foreach (var result in included)
            foreach (var (span, reason) in result.RejectedStretches)
            {
                if (allowDegraded) continue;
                excluded.Add($"clip {result.Clip.Index} ({result.Clip.Name}): stretch {Fmt.Clock(span.Start)}-{Fmt.Clock(span.Stop)} left out: {reason}");
            }
        var keptAnyway = allowDegraded ? included.SelectMany(r => r.RejectedStretches).ToList() : [];
        if (keptAnyway.Count > 0)
            notes.Add("Stretches the quality gate rejected, kept because degraded output was asked for:\n"
                + string.Join('\n', keptAnyway.Select(k => $"  {Fmt.Clock(k.Span.Start)}-{Fmt.Clock(k.Span.Stop)}: {k.Reason}")));
        if (excluded.Count > 0)
            notes.Add("Excluded by the quality gate (a whole clip, or one stretch of it):\n" + string.Join('\n', excluded.Select(e => "  " + e)));

        return (Vtt.Render(included.SelectMany(r => r.Cues), notes), excluded);
    }

    /// <summary>Spread samples across the clip rather than taking the opening lines: recordings tend
    /// to open with one person setting up, so everyone's first lines look like theirs.</summary>
    internal static List<T> Spread<T>(IReadOnlyList<T> items, int count)
    {
        if (items.Count <= count) return items.ToList();
        var step = count > 1 ? (items.Count - 1) / (double)(count - 1) : 1;
        return Enumerable.Range(0, count).Select(i => items[Math.Min(items.Count - 1, (int)Math.Round(i * step))]).ToList();
    }

    /// <summary>Per clip, per id: how much they said, how often they asked, and quotes from across the
    /// clip. The evidence a speaker mapping is argued from. Cutting a recording multiplies the id
    /// spaces, so this collects all of them in one place and the confirmation stays one pass.</summary>
    public static string SpeakerWorksheet(IEnumerable<(string Heading, List<Cue> Cues)> groups, IReadOnlyList<string> people)
    {
        var lines = new List<string>
        {
            "# Speaker worksheet",
            "",
            "Each clip was diarized on its own, so its ids are unrelated to the other clips' (two cuts",
            "of one recording have come back with the ids reversed). Ids below are already namespaced",
            "by clip and are the ids in `transcript.vtt`. A clip re-cut after a gate hit is a fresh",
            $"diarization from that second, so its ids sit in their own band (+{RetryNamespace}, +{2 * RetryNamespace}).",
            "",
            "**Read the quotes; do not assume one id is one person.** Diarization splits one speaker",
            "across ids and occasionally merges two into one. An id whose quotes mix two people's",
            "lines is one of those: say so rather than forcing a name on it.",
            "",
        };
        if (people.Count > 0)
        {
            lines.Add("## Who is in this recording (given up front)");
            lines.Add("");
            lines.AddRange(people.Select(p => $"- {p}"));
            lines.Add("");
        }
        foreach (var (heading, cues) in groups)
        {
            lines.Add($"## {heading}");
            lines.Add("");
            if (cues.Count == 0) { lines.Add("_No cues._"); lines.Add(""); continue; }
            foreach (var group in cues.GroupBy(c => c.Label ?? "(unlabeled)").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var said = group.ToList();
                var words = said.Sum(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                var talk = said.Sum(c => Math.Max(0, c.Stop - c.Start));
                var questions = said.Count(c => c.Text.TrimEnd().EndsWith('?'));
                lines.Add($"- **id {group.Key}** — {said.Count} cues, {words} words, {Fmt.F(talk, 0)} s, "
                    + $"{questions} question(s)");
                foreach (var cue in Spread(said, WorksheetQuotes))
                    lines.Add($"    - [{Fmt.Clock(cue.Start)}] \"{cue.Text}\"");
            }
            lines.Add("");
        }
        lines.AddRange(["Proposed mapping (fill in, then confirm):", "", "```", "--speakers \"\"", "```", ""]);
        return string.Join('\n', lines);
    }

    /// <summary>Group an already-assembled VTT's cues back into clips by namespace, for a worksheet
    /// rebuilt from the transcript alone.</summary>
    public static IEnumerable<(string, List<Cue>)> GroupByNamespace(IReadOnlyList<Cue> cues) =>
        cues.GroupBy(c => c.Speaker is { } id ? id / Clip.SpeakerNamespace : -1)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key < 0 ? "Unlabeled cues" : $"Ids {g.Key * Clip.SpeakerNamespace}-{g.Key * Clip.SpeakerNamespace + 99}"
                + $" ({Fmt.Clock(g.Min(c => c.Start))}-{Fmt.Clock(g.Max(c => c.Stop))})", g.ToList()));

    public static Plan MeasureAndPlan(string audio, PlanSettings settings, List<string>? commands = null)
    {
        var duration = Audio.ProbeDuration(audio);
        var (silences, coarse) = Audio.DetectSilences(audio, duration, settings.NoiseDb, settings.MinSilence);
        var (fine, fineCmd) = Audio.DetectSilences(audio, duration, settings.NoiseDb, PlanSettings.FineSilenceSeconds);
        // One loudness pass, so a candidate clip can be asked whether it holds speech at all.
        var loudness = Audio.LoudnessProfile(audio, PlanSettings.ProfileStepSeconds);
        commands?.AddRange([coarse, fineCmd]);
        return Planner.Build(duration, silences, settings, fine, loudness);
    }

    /// <summary>The whole pass. Returns the exit code: 0 clean, 4 when a clip was left out or
    /// nothing transcribed (the transcript is written either way, and says what is missing).</summary>
    public static int Run(VibeServer server, RunOptions options)
    {
        var commands = new List<string>();
        var plan = MeasureAndPlan(options.Audio, options.Settings, commands);
        Console.WriteLine(Planner.Render(plan));
        Console.WriteLine();
        if (plan.Clips.Count == 0)
            throw new TranscribeException("The plan has no clips: nothing in this recording measured as speech. "
                + "Check `profile` — the levels may need a different --noise-db / --min-peak-db.");

        var clipsDir = Path.Combine(options.OutDir, "clips");
        Directory.CreateDirectory(clipsDir);
        var results = new List<ClipResult>();
        foreach (var clip in plan.Clips)
        {
            Console.WriteLine($"transcribing clip {clip.Index}/{plan.Clips.Count} ({Fmt.Clock(clip.Start)}-{Fmt.Clock(clip.Stop)}) ...");
            results.Add(TranscribeWithRecovery(server, options.Audio, clip, clipsDir, options, commands));
        }

        var (vtt, excluded) = Assemble(results, options.Audio, plan, options.AllowDegraded);
        var transcriptPath = Path.Combine(options.OutDir, "transcript.vtt");
        Fmt.WriteText(transcriptPath, vtt);

        var worksheetPath = Path.Combine(options.OutDir, "speaker-worksheet.md");
        Fmt.WriteText(worksheetPath, SpeakerWorksheet(
            results.Where(r => r.Cues.Count > 0).Select(r =>
                ($"Clip {r.Clip.Index} — {Fmt.Clock(r.Clip.Start)}-{Fmt.Clock(r.Clip.Stop)}"
                 + (r.Warnings.Count > 0 || r.Error is not null ? " (EXCLUDED by the gate; ids shown for reference)" : ""),
                 r.Cues)),
            options.People));

        var assembly = new JsonObject
        {
            ["tool"] = "holtz",
            ["source_audio"] = new JsonObject
            {
                ["path"] = Path.GetFullPath(options.Audio),
                ["name"] = Path.GetFileName(options.Audio),
                ["sha256"] = Fmt.FileSha256(options.Audio),
                ["duration"] = Math.Round(plan.Duration, 3),
            },
            ["server"] = new JsonObject
            {
                ["url"] = server.Url,
                ["started_by_this_run"] = server.Owned,
                ["source"] = server.Source,
                ["version"] = server.Version,
                ["model"] = options.ModelPath,
            },
            // Built from the same dictionary the request is built from: a provenance record that
            // states a format the request did not use is worse than none.
            ["request_fields"] = new JsonObject(new[] { KeyValuePair.Create("response_format", (JsonNode?)VibeServer.ResponseFormat) }
                .Concat(options.Fields.Where(f => f.Key != "response_format").Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value)))),
            ["people"] = new JsonArray(options.People.Select(p => (JsonNode)p).ToArray()),
            ["plan"] = plan.ToJson(),
            ["clips"] = new JsonArray(results.Select(r => (JsonNode)r.ToJson()).ToArray()),
            ["excluded_from_transcript"] = new JsonArray(excluded.Select(e => (JsonNode)e).ToArray()),
            ["allow_degraded"] = options.AllowDegraded,
            ["ffmpeg_commands"] = new JsonArray(commands.Select(c => (JsonNode)c).ToArray()),
        };
        var assemblyPath = Path.Combine(options.OutDir, "transcript-assembly.json");
        Fmt.WriteText(assemblyPath, assembly.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Console.WriteLine();
        Console.WriteLine($"wrote {transcriptPath}");
        Console.WriteLine($"wrote {worksheetPath}");
        Console.WriteLine($"wrote {assemblyPath}");

        if (options.GooglePath is not null)
        {
            Console.WriteLine();
            GoogleCrossCheck.RunInto(options.GooglePath, transcriptPath, options.OutDir, assemblyPath);
        }

        if (excluded.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Some clips or stretches were left out of the transcript:");
            foreach (var item in excluded) Console.Error.WriteLine($"  {item}");
            Console.Error.WriteLine("Re-run just that window (run AUDIO --from SECONDS --to SECONDS --out OTHER_DIR), or pass "
                + "--allow-degraded to include them anyway — and then say the transcript is degraded and where.");
            return 4;
        }
        if (results.All(r => r.Cues.Count == 0))
        {
            Console.Error.WriteLine("No clip produced any cues.");
            return 4;
        }
        Console.WriteLine();
        Console.WriteLine("Next: read speaker-worksheet.md, map each id to a person from the quotes, confirm, then run `normalize`.");
        return 0;
    }
}
