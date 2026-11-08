namespace Holtz;

/// <summary>A transcript turned into the readable <c>[MM:SS Name] text</c> form, given a confirmed
/// id-to-name mapping. The deterministic half only: who an id IS gets decided from the dialogue and
/// confirmed by a person, then handed in here. Nothing is silently mislabeled: a present but
/// unmapped id renders <c>[Speaker N]</c>, an unlabeled fragment renders <c>[?]</c>.</summary>
public static class Normalizer
{
    public const string UnlabeledLabel = "?";
    /// <summary>Marks a turn that came from the fill source (the Google transcript) rather than Vibe.</summary>
    public const string FillMarker = "~google";
    /// <summary>A pause this long (seconds) starts a new line even for the same speaker.</summary>
    public const double DefaultTurnGap = 10;

    public static Dictionary<int, string> ParseSpeakerMap(string raw)
    {
        var mapping = new Dictionary<int, string>();
        foreach (var pair in raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) throw new TranscribeException($"Bad --speakers entry '{pair}'; expected 'id=Name' (e.g. '0=Ana').");
            var key = pair[..eq].Trim();
            var name = pair[(eq + 1)..].Trim();
            if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, Fmt.Inv, out var id))
                throw new TranscribeException($"Speaker id '{key}' is not an integer.");
            if (name.Length == 0) throw new TranscribeException($"Speaker id {id} has an empty name.");
            mapping[id] = name;
        }
        if (mapping.Count == 0) throw new TranscribeException("--speakers is empty; give at least one 'id=Name'.");
        return mapping;
    }

    private static string Clean(string text)
    {
        text = Fmt.Squash(text);
        if (text.StartsWith("- ", StringComparison.Ordinal)) text = text[2..];
        return text.Trim();
    }

    public static (string Name, int? Unmapped) Resolve(Cue cue, IReadOnlyDictionary<int, string> mapping)
    {
        if (cue.Speaker is { } id)
            return mapping.TryGetValue(id, out var name) ? (name, null) : ($"Speaker {id}", id);
        if (cue.Voice is { } voice) return (voice, null);
        return (UnlabeledLabel, null);
    }

    /// <summary>Stretches with no primary cue at least <paramref name="minGap"/> long, including the
    /// open stretches before the first cue and after the last.</summary>
    public static List<Span> Gaps(IReadOnlyList<Cue> cues, double minGap)
    {
        var sorted = cues.OrderBy(c => c.Start).ToList();
        var gaps = new List<Span>();
        var cursor = 0.0;
        foreach (var cue in sorted)
        {
            if (cue.Start - cursor >= minGap) gaps.Add(new Span(cursor, cue.Start));
            cursor = Math.Max(cursor, cue.Stop);
        }
        gaps.Add(new Span(cursor, double.PositiveInfinity));
        return gaps;
    }

    /// <summary>Fill cues lying wholly inside a gap in the primary transcript: where Vibe has nothing
    /// and the second source heard something. Only gaps; never a correction of something Vibe did
    /// transcribe, because two transcripts of one recording are two pieces of evidence.</summary>
    public static List<Cue> FillCues(IReadOnlyList<Cue> primary, IReadOnlyList<Cue> fill, double minGap)
    {
        var gaps = Gaps(primary, minGap);
        return fill.Where(f => gaps.Any(g => g.Start <= f.Start && f.Stop <= g.Stop)).ToList();
    }

    /// <summary>Consecutive cues by one speaker become one turn, unless a pause of at least
    /// <paramref name="turnGap"/> seconds separates them: a turn shows only its first time, so merging
    /// across a long silence would hide when the later words were said.</summary>
    public static (List<(string Name, string Text, double Start)> Turns, SortedSet<int> Unmapped) BuildTurns(
        IReadOnlyList<Cue> cues, IReadOnlyDictionary<int, string> mapping, ISet<Cue>? filled = null, double turnGap = DefaultTurnGap)
    {
        var turns = new List<(string Name, string Text, double Start)>();
        var unmapped = new SortedSet<int>();
        var heardUntil = 0.0;
        foreach (var cue in cues)
        {
            var (name, missing) = Resolve(cue, mapping);
            if (missing is { } id) unmapped.Add(id);
            if (filled?.Contains(cue) == true) name += " " + FillMarker;
            var text = Clean(cue.Text);
            if (text.Length == 0) continue;
            if (turns.Count > 0 && turns[^1].Name == name && cue.Start - heardUntil < turnGap)
                turns[^1] = (name, turns[^1].Text + " " + text, turns[^1].Start);
            else turns.Add((name, text, cue.Start));
            heardUntil = Math.Max(heardUntil, cue.Stop);
        }
        return (turns, unmapped);
    }

    public sealed record Options
    {
        public string? Title { get; init; }
        public List<string> Notes { get; init; } = [];
        public string SourceName { get; init; } = "";
        public string SourceSha256 { get; init; } = "";
        public string? FillSourceName { get; init; }
        public int FilledCount { get; init; }
        public double TurnGap { get; init; } = DefaultTurnGap;
    }

    public static (string Text, SortedSet<int> Unmapped) Render(
        IReadOnlyList<Cue> cues, IReadOnlyDictionary<int, string> mapping, Options options, ISet<Cue>? filled = null)
    {
        var ordered = cues.OrderBy(c => c.Start).ToList();
        var (turns, unmapped) = BuildTurns(ordered, mapping, filled, options.TurnGap);
        var duration = cues.Max(c => c.Stop);
        var header = new List<string>
        {
            "# Transcript" + (options.Title is null ? "" : $" — {options.Title}"),
            $"# Source: {options.SourceName} (sha256 {options.SourceSha256[..Math.Min(12, options.SourceSha256.Length)]})",
            $"# Duration: {Fmt.Time(duration)}",
            $"# Speakers: {string.Join(", ", mapping.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"))}; "
            + $"unmapped ids shown as [Speaker N], unlabeled fragments as [{UnlabeledLabel}]",
        };
        if (options.FilledCount > 0)
            header.Add($"# {options.FilledCount} passage(s) marked {FillMarker} come from {options.FillSourceName}, inserted only where "
                + "the Vibe transcript has nothing; their times and speakers are estimates from alignment.");
        header.AddRange(options.Notes.Select(n => $"# {n}"));
        header.Add("");
        var body = turns.Select(t => $"[{Fmt.Time(t.Start)} {t.Name}] {t.Text}");
        return (string.Join('\n', header.Concat(body)) + "\n", unmapped);
    }
}
