using System.Text.Json.Nodes;

namespace Holtz;

/// <summary>Several transcripts of one recording, each named with its own confirmed mapping, joined
/// into one readable transcript on the recording's timeline. The case it exists for: a long run whose
/// clip was excluded or whose diarization lost a person, repaired by re-running windows of it
/// (<c>run --from --to --out</c>). Each window is its own diarization, so its ids mean nothing to the
/// others, and only names can be joined. Measured on a real 59-minute meeting: six runs (a full run
/// whose second clip the gate excluded, three windows split at the loops, a 27-second stretch the plan
/// had skipped as silence, and a 3:45 re-run that gave a lost speaker an id), joined by hand before
/// this existed.
///
/// Parts are applied in the order given. Each part's window replaces whatever earlier parts had there,
/// so the widest run goes first and narrower re-runs after it. A cue belongs to the part whose window
/// holds its start, the same rule a turn's time follows; a cue that starts just before a window and
/// runs into it stays with the earlier part. So windows should be cut at turn boundaries. Nothing here
/// edits text: every line comes from one part's transcript, named by that part's own mapping.</summary>
public static class Joiner
{
    /// <summary>A window cut at a silence's edge overlaps it by the clip's pre-roll (0.35 s): not a
    /// stretch anyone could miss, so not reported.</summary>
    public const double MinReportedGap = 1.0;

    public sealed record Part
    {
        public required string Name { get; init; }
        public required string VttPath { get; init; }
        public required string Sha256 { get; init; }
        public required Span Window { get; init; }
        /// <summary>False when no <c>transcript-assembly.json</c> said what the run covered, and the
        /// window is only the span of its cues.</summary>
        public required bool WindowFromRecord { get; init; }
        public required IReadOnlyDictionary<int, string> Mapping { get; init; }
        public required List<Cue> Cues { get; init; }
        /// <summary>Stretches inside the window the run did not transcribe, and why.</summary>
        public List<(Span Span, string Why, bool Failure)> NotTranscribed { get; init; } = [];
        public List<string> SuspectNotes { get; init; } = [];
    }

    public sealed record Result(
        List<Cue> Cues,
        List<(Span Span, int Part)> Coverage,
        List<(Span Span, string Why, bool Failure, int Part)> Missing,
        List<(int Part, int Id)> Unmapped);

    /// <summary>The deepest folder holding all of <paramref name="folders"/>, so parts are named by
    /// how they differ (<c>recut_08-17_to_08-44</c>) rather than by a long shared path.</summary>
    public static string CommonFolder(IEnumerable<string> folders)
    {
        var split = folders.Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
        if (split.Count == 0) return Directory.GetCurrentDirectory();
        var depth = 0;
        while (split.All(s => s.Length > depth && string.Equals(s[depth], split[0][depth], StringComparison.OrdinalIgnoreCase))) depth++;
        if (depth == 0) return Path.GetPathRoot(Path.GetFullPath(".")) ?? "";
        var common = string.Join(Path.DirectorySeparatorChar, split[0].Take(depth));
        // A bare drive ("C:") or Unix root ("") needs its separator to be a folder.
        return common.Length == 0 || common.EndsWith(':') ? common + Path.DirectorySeparatorChar : common;
    }

    /// <summary>Load a run folder (its <c>transcript.vtt</c>) or a VTT, with what its
    /// <c>transcript-assembly.json</c> says it covered and left out.</summary>
    public static Part Load(string path, IReadOnlyDictionary<int, string> mapping, string relativeTo)
    {
        var vtt = Directory.Exists(path) ? Path.Combine(path, "transcript.vtt") : path;
        if (!File.Exists(vtt)) throw new TranscribeException($"No such transcript: {vtt}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(vtt))!;
        var raw = File.ReadAllText(vtt);
        var cues = Vtt.Load(vtt);
        var record = Path.Combine(dir, "transcript-assembly.json");
        var assembly = File.Exists(record) ? Segments.TryParse(File.ReadAllText(record)) : null;

        var name = Path.GetRelativePath(relativeTo, dir);
        if (name == ".") name = Path.GetFileName(dir);
        if (!Path.GetFileName(vtt).Equals("transcript.vtt", StringComparison.OrdinalIgnoreCase))
            name = Path.Combine(name, Path.GetFileName(vtt));

        if (assembly?["plan"] is not JsonObject plan)
            return new Part
            {
                Name = name, VttPath = vtt, Sha256 = Fmt.FileSha256(vtt), Mapping = mapping, Cues = cues,
                Window = new Span(cues.Min(c => c.Start), cues.Max(c => c.Stop)), WindowFromRecord = false,
                SuspectNotes = Vtt.SuspectNotes(raw),
            };

        var duration = Segments.AsDouble(plan["duration"]) ?? cues.Max(c => c.Stop);
        var window = new Span(Segments.AsDouble(plan["settings"]?["from"]) ?? 0,
            Math.Min(Segments.AsDouble(plan["settings"]?["to"]) ?? duration, duration));
        var degradedAllowed = assembly["allow_degraded"] is JsonValue v && v.TryGetValue<bool>(out var allowed) && allowed;

        var missing = new List<(Span, string, bool)>();
        foreach (var span in Spans(plan["skipped_silences"])) missing.Add((span, "silence, not transcribed", false));
        foreach (var span in Spans(plan["dropped_too_quiet"])) missing.Add((span, "too quiet to be speech, not transcribed", false));
        foreach (var clip in (assembly["clips"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SpanOf(clip) is not { } clipSpan) continue;
            var gateHit = clip["gate_warnings"] is JsonArray { Count: > 0 };
            if (clip["error"] is not null || (gateHit && !degradedAllowed))
                missing.Add((clipSpan, "clip excluded by the quality gate", true));
            else if (!degradedAllowed)
                foreach (var stretch in Spans(clip["rejected_stretches"]))
                    missing.Add((stretch, "stretch left out by the quality gate", true));
        }
        return new Part
        {
            Name = name, VttPath = vtt, Sha256 = Fmt.FileSha256(vtt), Mapping = mapping, Cues = cues,
            Window = window, WindowFromRecord = true, NotTranscribed = missing, SuspectNotes = Vtt.SuspectNotes(raw),
        };
    }

    private static Span? SpanOf(JsonObject o) =>
        Segments.AsDouble(o["start"]) is { } start && Segments.AsDouble(o["stop"]) is { } stop && stop > start ? new Span(start, stop) : null;

    private static IEnumerable<Span> Spans(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonObject>().Select(SpanOf).OfType<Span>();

    private static bool Holds(Span window, double t) => t >= window.Start && t < window.Stop;

    /// <summary>What remains of <paramref name="spans"/> outside <paramref name="cut"/>.</summary>
    private static List<(Span Span, int Part)> Subtract(IEnumerable<(Span Span, int Part)> spans, Span cut)
    {
        var kept = new List<(Span, int)>();
        foreach (var (span, part) in spans)
        {
            if (span.Stop <= cut.Start || span.Start >= cut.Stop) { kept.Add((span, part)); continue; }
            if (span.Start < cut.Start) kept.Add((new Span(span.Start, cut.Start), part));
            if (span.Stop > cut.Stop) kept.Add((new Span(cut.Stop, span.Stop), part));
        }
        return kept;
    }

    /// <summary>Apply the parts in order: each replaces what earlier ones had inside its window.
    /// Ids become names here, per part, so ids from different runs never meet.</summary>
    public static Result Merge(IReadOnlyList<Part> parts)
    {
        var kept = new List<(Cue Cue, int Part)>();
        var coverage = new List<(Span Span, int Part)>();
        var unmapped = new SortedSet<(int Part, int Id)>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            kept.RemoveAll(k => Holds(part.Window, k.Cue.Start));
            coverage = Subtract(coverage, part.Window);
            coverage.Add((part.Window, i));
            foreach (var cue in part.Cues.Where(c => Holds(part.Window, c.Start)))
            {
                var named = cue;
                if (cue.Speaker is { } id)
                {
                    if (part.Mapping.TryGetValue(id, out var name)) named = cue with { Speaker = null, Voice = name };
                    else
                    {
                        named = cue with { Speaker = null, Voice = $"Speaker {id}" };
                        unmapped.Add((i, id));
                    }
                }
                kept.Add((named, i));
            }
        }
        coverage = coverage.OrderBy(c => c.Span.Start).ToList();
        var missing = new List<(Span, string, bool, int)>();
        foreach (var (span, i) in coverage)
            foreach (var (gap, why, failure) in parts[i].NotTranscribed)
            {
                var overlap = new Span(Math.Max(span.Start, gap.Start), Math.Min(span.Stop, gap.Stop));
                if (overlap.Duration >= MinReportedGap) missing.Add((overlap, why, failure, i));
            }
        return new Result(
            kept.Select(k => k.Cue).OrderBy(c => c.Start).ThenBy(c => c.Stop).ToList(),
            coverage,
            missing.OrderBy(m => m.Item1.Start).ToList(),
            [.. unmapped]);
    }

    public static string Render(Result result, IReadOnlyList<Part> parts, string? title, IEnumerable<string> notes, double turnGap)
    {
        var (turns, _) = Normalizer.BuildTurns(result.Cues, new Dictionary<int, string>(), null, turnGap);
        var header = new List<string>
        {
            "# Transcript" + (title is null ? "" : $" — {title}"),
            $"# Joined from {parts.Count} transcripts of one recording. Each later part replaces the earlier ones inside its",
            "# window; a line belongs to the part whose window holds its start. Every line's text is that part's own.",
        };
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            header.Add($"# Part {i + 1}: {p.Name} (sha256 {p.Sha256[..Math.Min(12, p.Sha256.Length)]}), window "
                + $"{Fmt.Time(p.Window.Start)}-{Fmt.Time(p.Window.Stop)}"
                + (p.WindowFromRecord ? "" : " (from its cues: no transcript-assembly.json)")
                + $"; speakers {string.Join(", ", p.Mapping.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"))}");
        }
        header.Add("# Covers: " + string.Join("; ", result.Coverage.Select(c => $"{Fmt.Time(c.Span.Start)}-{Fmt.Time(c.Span.Stop)} part {c.Part + 1}")));
        foreach (var (span, why, failure, part) in result.Missing)
            header.Add($"# {(failure ? "NOT TRANSCRIBED" : "Not transcribed")}: {Fmt.Time(span.Start)}-{Fmt.Time(span.Stop)} "
                + $"({Fmt.F(span.Duration, 0)}s), part {part + 1}: {why}");
        header.Add("# Unmapped ids shown as [Speaker N]"
            + (result.Unmapped.Count > 0 ? $" ({string.Join(", ", result.Unmapped.Select(u => $"part {u.Part + 1} id {u.Id}"))})" : "")
            + $", unlabeled fragments as [{Normalizer.UnlabeledLabel}]");
        for (var i = 0; i < parts.Count; i++)
            header.AddRange(parts[i].SuspectNotes.Select(n => $"# part {i + 1}: {n}"));
        header.AddRange(notes.Select(n => $"# {n}"));
        header.Add("");
        var body = turns.Select(t => $"[{Fmt.Time(t.Start)} {t.Name}] {t.Text}");
        return string.Join('\n', header.Concat(body)) + "\n";
    }
}
