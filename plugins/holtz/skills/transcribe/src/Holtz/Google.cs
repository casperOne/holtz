using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Holtz;

/// <summary>One word of an independent transcript (Google Recorder's text export), with the
/// paragraph it sat in and the export's own speaker label if it had one.</summary>
public sealed record SourceWord(string Text, int Paragraph, string? Label);

/// <summary>A Google Recorder transcript: words, no timings, usually no speakers. Its value is that it
/// was made by a different engine from a different microphone, so where it and Vibe agree the words
/// are corroborated, and where they disagree one of them is worth a listen.</summary>
public sealed partial record GoogleTranscript(List<SourceWord> Words, int Paragraphs, int TimestampLines)
{
    [GeneratedRegex(@"^\s*(?:\(?(?:\d{1,2}:)?\d{1,2}:\d{2}\)?)\s*$")]
    private static partial Regex TimestampLine();

    [GeneratedRegex(@"^\s*(Speaker\s+\d+)\s*(?:\(?(?:\d{1,2}:)?\d{1,2}:\d{2}\)?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^\s*(Speaker\s+\d+)\s*[:\-–—]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelPrefix();

    /// <summary>Plain text: paragraphs separated by blank lines. Tolerates the variants the Recorder
    /// app has been seen to produce or could: a bare timestamp line, a <c>Speaker N</c> line (or
    /// <c>Speaker N: text</c>) before a paragraph. Timestamps are counted and set aside: Google's
    /// clock started on a different device at a different moment, so it is not Vibe's clock.</summary>
    public static GoogleTranscript Parse(string text)
    {
        var words = new List<SourceWord>();
        var paragraph = 0;
        var inParagraph = false;
        var timestamps = 0;
        string? label = null;
        foreach (var rawLine in text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (inParagraph) { paragraph++; inParagraph = false; }
                continue;
            }
            if (TimestampLine().IsMatch(line)) { timestamps++; continue; }
            var labelOnly = LabelLine().Match(line);
            if (labelOnly.Success)
            {
                if (inParagraph) { paragraph++; inParagraph = false; }
                label = Fmt.Squash(labelOnly.Groups[1].Value);
                continue;
            }
            var prefixed = LabelPrefix().Match(line);
            if (prefixed.Success)
            {
                if (inParagraph) { paragraph++; inParagraph = false; }
                label = Fmt.Squash(prefixed.Groups[1].Value);
                line = prefixed.Groups[2].Value;
            }
            foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                words.Add(new SourceWord(word, paragraph, label));
            inParagraph = true;
        }
        return new GoogleTranscript(words, inParagraph ? paragraph + 1 : paragraph, timestamps);
    }
}

/// <summary>Word-level alignment of two transcripts of the same recording.</summary>
public static partial class Aligner
{
    private static readonly Dictionary<string, string[]> Variants = new()
    {
        ["ok"] = ["okay"], ["alright"] = ["all", "right"], ["gonna"] = ["going", "to"],
        ["wanna"] = ["want", "to"], ["gotta"] = ["got", "to"], ["cause"] = ["because"],
        ["zero"] = ["0"], ["one"] = ["1"], ["two"] = ["2"], ["three"] = ["3"], ["four"] = ["4"],
        ["five"] = ["5"], ["six"] = ["6"], ["seven"] = ["7"], ["eight"] = ["8"], ["nine"] = ["9"],
        ["ten"] = ["10"], ["eleven"] = ["11"], ["twelve"] = ["12"], ["twenty"] = ["20"],
        ["thirty"] = ["30"], ["forty"] = ["40"], ["fifty"] = ["50"], ["hundred"] = ["100"],
        ["percent"] = ["%"],
    };

    [GeneratedRegex(@"(?<=\d),(?=\d{3})")]
    private static partial Regex DigitGrouping();

    [GeneratedRegex(@"[^\p{L}\p{N}'%]+")]
    private static partial Regex NonWord();

    /// <summary>Normalize one written word to comparison tokens. Both sides go through the same
    /// function, so a mapping only has to be consistent, not linguistically right ("one" becomes
    /// "1" in "this one" too, on both sides).</summary>
    public static IEnumerable<string> Tokens(string word)
    {
        var w = word.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
        w = DigitGrouping().Replace(w, "");
        w = w.Replace("%", " % ");
        foreach (var piece in NonWord().Split(w))
        {
            var token = piece.Replace("'", "");
            if (token.Length == 0) continue;
            if (Variants.TryGetValue(token, out var mapped)) foreach (var m in mapped) yield return m;
            else yield return token;
        }
    }

    /// <summary>Above this many cells a stretch is split on anchors before any dynamic programming.
    /// Kept small: an exact LCS over a big stretch is exactly where scattered coincidences come from.</summary>
    public const long DpCellLimit = 250_000;

    /// <summary>A match only counts inside a run of this many tokens consecutive on both sides.</summary>
    public const int MinRun = 3;

    /// <summary>Monotone matched pairs (i in <paramref name="a"/>, j in <paramref name="b"/>). Patience
    /// style: anchor on tokens that occur exactly once in both stretches (and agree with a
    /// neighbour, so a stray coincidence is not an anchor), keep the longest increasing chain of
    /// them, recurse between anchors, and finish small stretches with an exact LCS. Linear-ish on
    /// real transcripts, where unique content words are everywhere.
    ///
    /// Then every match outside a run of <paramref name="minRun"/> is discarded. LCS maximizes the
    /// NUMBER of matches, so across a stretch only one side transcribed it happily pairs a handful
    /// of words with "okay" and "all right" scattered over minutes of the other side (measured on a
    /// real recording: 689 words' estimated times collapsed into one minute). A real agreement is
    /// a phrase, not a word.</summary>
    public static List<(int I, int J)> Align(IReadOnlyList<string> a, IReadOnlyList<string> b, int minRun = MinRun) =>
        KeepRuns(AlignAll(a, b), minRun);

    internal static List<(int I, int J)> KeepRuns(List<(int I, int J)> pairs, int minRun)
    {
        var kept = new List<(int, int)>();
        var start = 0;
        for (var k = 1; k <= pairs.Count; k++)
        {
            var continues = k < pairs.Count && pairs[k].I == pairs[k - 1].I + 1 && pairs[k].J == pairs[k - 1].J + 1;
            if (continues) continue;
            if (k - start >= minRun) kept.AddRange(pairs.GetRange(start, k - start));
            start = k;
        }
        return kept;
    }

    private static List<(int I, int J)> AlignAll(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var pairs = new List<(int, int)>();
        var stack = new Stack<(int A0, int A1, int B0, int B1)>();
        stack.Push((0, a.Count, 0, b.Count));
        while (stack.Count > 0)
        {
            var (a0, a1, b0, b1) = stack.Pop();
            while (a0 < a1 && b0 < b1 && a[a0] == b[b0]) pairs.Add((a0++, b0++));
            while (a0 < a1 && b0 < b1 && a[a1 - 1] == b[b1 - 1]) pairs.Add((--a1, --b1));
            if (a0 >= a1 || b0 >= b1) continue;
            if ((long)(a1 - a0) * (b1 - b0) <= DpCellLimit)
            {
                pairs.AddRange(Lcs(a, a0, a1, b, b0, b1));
                continue;
            }
            var anchors = UniqueAnchors(a, a0, a1, b, b0, b1, requireContext: true);
            if (anchors.Count == 0) anchors = UniqueAnchors(a, a0, a1, b, b0, b1, requireContext: false);
            if (anchors.Count == 0) continue; // nothing to stand on: leave the stretch unaligned
            var chain = LongestIncreasing(anchors);
            int pa = a0, pb = b0;
            foreach (var (i, j) in chain)
            {
                pairs.Add((i, j));
                stack.Push((pa, i, pb, j));
                pa = i + 1;
                pb = j + 1;
            }
            stack.Push((pa, a1, pb, b1));
        }
        pairs.Sort();
        return pairs;
    }

    private static List<(int I, int J)> UniqueAnchors(
        IReadOnlyList<string> a, int a0, int a1, IReadOnlyList<string> b, int b0, int b1, bool requireContext)
    {
        var inA = new Dictionary<string, (int Count, int Index)>();
        for (var i = a0; i < a1; i++) inA[a[i]] = inA.TryGetValue(a[i], out var e) ? (e.Count + 1, e.Index) : (1, i);
        var inB = new Dictionary<string, (int Count, int Index)>();
        for (var j = b0; j < b1; j++) inB[b[j]] = inB.TryGetValue(b[j], out var e) ? (e.Count + 1, e.Index) : (1, j);
        var anchors = new List<(int, int)>();
        foreach (var (token, (count, i)) in inA)
        {
            if (count != 1 || !inB.TryGetValue(token, out var other) || other.Count != 1) continue;
            var j = other.Index;
            if (requireContext)
            {
                var before = i > a0 && j > b0 && a[i - 1] == b[j - 1];
                var after = i + 1 < a1 && j + 1 < b1 && a[i + 1] == b[j + 1];
                if (!before && !after) continue;
            }
            anchors.Add((i, j));
        }
        anchors.Sort();
        return anchors;
    }

    /// <summary>Longest chain increasing in both coordinates (input sorted by I, I unique).</summary>
    private static List<(int I, int J)> LongestIncreasing(List<(int I, int J)> anchors)
    {
        var tails = new List<int>();          // index into anchors of the smallest tail per length
        var previous = new int[anchors.Count];
        for (var k = 0; k < anchors.Count; k++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (anchors[tails[mid]].J < anchors[k].J) lo = mid + 1; else hi = mid;
            }
            previous[k] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(k); else tails[lo] = k;
        }
        var chain = new List<(int, int)>();
        for (var k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k]) chain.Add(anchors[k]);
        chain.Reverse();
        return chain;
    }

    private static List<(int, int)> Lcs(IReadOnlyList<string> a, int a0, int a1, IReadOnlyList<string> b, int b0, int b1)
    {
        int n = a1 - a0, m = b1 - b0;
        var table = new int[(n + 1) * (m + 1)];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i * (m + 1) + j] = a[a0 + i] == b[b0 + j]
                    ? table[(i + 1) * (m + 1) + j + 1] + 1
                    : Math.Max(table[(i + 1) * (m + 1) + j], table[i * (m + 1) + j + 1]);
        var result = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[a0 + x] == b[b0 + y]) { result.Add((a0 + x, b0 + y)); x++; y++; }
            else if (table[(x + 1) * (m + 1) + y] >= table[x * (m + 1) + y + 1]) x++;
            else y++;
        }
        return result;
    }
}

/// <summary>Google's transcript aligned to Vibe's: a timed, speaker-projected second transcript, a
/// cross-check report, and the material for filling Vibe's gaps.</summary>
public static class GoogleCrossCheck
{
    /// <summary>A run of this many unmatched tokens in a row is worth a human's attention.</summary>
    public const int MinStretchTokens = 8;
    public const double ReportWindowSeconds = 60.0;

    private sealed record VibeToken(string Token, double Time, int Cue, int? Speaker, string? Voice);

    private sealed record GoogleToken(string Token, int Word, int Paragraph);

    public sealed record Result(string Vtt, string Report, int VibeTokens, int GoogleTokens, int Matched);

    private static List<VibeToken> VibeTokens(IReadOnlyList<Cue> cues)
    {
        var tokens = new List<VibeToken>();
        for (var c = 0; c < cues.Count; c++)
        {
            var cue = cues[c];
            var words = cue.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SelectMany(Aligner.Tokens).ToList();
            for (var k = 0; k < words.Count; k++)
                tokens.Add(new VibeToken(words[k], cue.Start + (cue.Stop - cue.Start) * (k + 0.5) / words.Count, c, cue.Speaker, cue.Voice));
        }
        return tokens;
    }

    private static List<GoogleToken> GoogleTokens(GoogleTranscript google) =>
        google.Words.SelectMany((w, i) => Aligner.Tokens(w.Text).Select(t => new GoogleToken(t, i, w.Paragraph))).ToList();

    private sealed record Window(double Start, double Stop, string What);

    /// <summary>Stretches the assembly record says Vibe did not transcribe, so a Google-only stretch
    /// can be explained: a clip the gate excluded, a silence skipped, a stretch dropped as noise.</summary>
    private static List<Window> UntranscribedWindows(string? assemblyPath)
    {
        var windows = new List<Window>();
        if (assemblyPath is null || !File.Exists(assemblyPath)) return windows;
        if (Segments.TryParse(File.ReadAllText(assemblyPath)) is not JsonObject assembly) return windows;
        var allowDegraded = assembly["allow_degraded"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        foreach (var node in assembly["clips"] as JsonArray ?? [])
        {
            if (node is not JsonObject clip) continue;
            var warned = clip["gate_warnings"] is JsonArray { Count: > 0 };
            var failed = clip["error"] is JsonValue;
            if (failed || (warned && !allowDegraded))
                windows.Add(new Window(Segments.AsDouble(clip["start"]) ?? 0, Segments.AsDouble(clip["stop"]) ?? 0,
                    $"clip {clip["index"]} excluded by the quality gate"));
            if (allowDegraded) continue;
            foreach (var rejected in clip["rejected_stretches"] as JsonArray ?? [])
                windows.Add(new Window(Segments.AsDouble(rejected?["start"]) ?? 0, Segments.AsDouble(rejected?["stop"]) ?? 0,
                    $"a stretch of clip {clip["index"]} the quality gate rejected"));
        }
        if (assembly["plan"] is JsonObject plan)
        {
            foreach (var (key, what) in new[] { ("skipped_silences", "a skipped silence"), ("dropped_too_quiet", "a stretch dropped as noise") })
                foreach (var node in plan[key] as JsonArray ?? [])
                    if (node is JsonObject span)
                        windows.Add(new Window(Segments.AsDouble(span["start"]) ?? 0, Segments.AsDouble(span["stop"]) ?? 0, what));
        }
        return windows;
    }

    public static Result Build(IReadOnlyList<Cue> vibeCues, GoogleTranscript google, string vibeName, string googleName, string? assemblyPath)
    {
        var cues = vibeCues.OrderBy(c => c.Start).ThenBy(c => c.Stop).ToList();
        var vt = VibeTokens(cues);
        var gt = GoogleTokens(google);
        var pairs = Aligner.Align(vt.Select(t => t.Token).ToList(), gt.Select(t => t.Token).ToList());
        var vMatch = Enumerable.Repeat(-1, vt.Count).ToArray();
        var gMatch = Enumerable.Repeat(-1, gt.Count).ToArray();
        foreach (var (i, j) in pairs) { vMatch[i] = j; gMatch[j] = i; }

        // Times: a matched Google token takes its Vibe partner's time; the rest are interpolated
        // between the nearest matched neighbours, and remember how wide that interpolation was.
        var times = new double[gt.Count];
        var spread = new double[gt.Count];
        var prev = -1;
        for (var j = 0; j <= gt.Count; j++)
        {
            if (j < gt.Count && gMatch[j] < 0) continue;
            var leftTime = prev >= 0 ? vt[gMatch[prev]].Time : (j < gt.Count ? vt[gMatch[j]].Time : 0);
            var rightTime = j < gt.Count ? vt[gMatch[j]].Time : leftTime;
            for (var k = prev + 1; k < j; k++)
            {
                var fraction = (k - prev) / (double)(j - prev);
                times[k] = leftTime + (rightTime - leftTime) * fraction;
                spread[k] = rightTime - leftTime;
            }
            if (j < gt.Count) times[j] = vt[gMatch[j]].Time;
            prev = j;
        }
        if (pairs.Count == 0) Array.Fill(times, 0);

        // Speakers: projected from Vibe's diarization. An unmatched token inherits only when its
        // matched neighbours on both sides agree and sit in the same Google paragraph.
        var speakers = new (int? Speaker, string? Voice)?[gt.Count];
        for (var j = 0; j < gt.Count; j++)
            if (gMatch[j] >= 0) speakers[j] = (vt[gMatch[j]].Speaker, vt[gMatch[j]].Voice);
        int? lastMatched = null;
        for (var j = 0; j < gt.Count; j++)
        {
            if (gMatch[j] >= 0) { lastMatched = j; continue; }
            var next = j + 1;
            while (next < gt.Count && gMatch[next] < 0) next++;
            if (lastMatched is { } l && next < gt.Count && speakers[l] == speakers[next]
                && gt[l].Paragraph == gt[j].Paragraph && gt[next].Paragraph == gt[j].Paragraph)
                speakers[j] = speakers[l];
        }

        var vtt = RenderVtt(google, gt, times, speakers, vibeName, googleName, pairs.Count);
        var report = RenderReport(cues, vt, gt, google, vMatch, gMatch, times, spread, vibeName, googleName, assemblyPath);
        return new Result(vtt, report, vt.Count, gt.Count, pairs.Count);
    }

    private static string RenderVtt(GoogleTranscript google, List<GoogleToken> gt, double[] times,
        (int? Speaker, string? Voice)?[] speakers, string vibeName, string googleName, int matched)
    {
        // A word's time and speaker are its first token's.
        var firstToken = new Dictionary<int, int>();
        for (var j = 0; j < gt.Count; j++) firstToken.TryAdd(gt[j].Word, j);
        var cues = new List<Cue>();
        var buffer = new List<int>();
        void Flush()
        {
            if (buffer.Count == 0) return;
            var tokenIndexes = buffer.Where(firstToken.ContainsKey).Select(w => firstToken[w]).ToList();
            var start = tokenIndexes.Count > 0 ? times[tokenIndexes[0]] : 0;
            var stop = Math.Max(start + 0.5, tokenIndexes.Count > 0 ? times[tokenIndexes[^1]] + 0.3 : start + 0.5);
            var who = tokenIndexes.Count > 0 ? speakers[tokenIndexes[0]] : null;
            cues.Add(new Cue(start, stop, string.Join(' ', buffer.Select(w => google.Words[w].Text)), who?.Speaker, who?.Voice));
            buffer.Clear();
        }
        (int?, string?)? current = null;
        for (var w = 0; w < google.Words.Count; w++)
        {
            (int?, string?)? who = firstToken.TryGetValue(w, out var t) ? speakers[t] : current;
            var paragraphChanged = buffer.Count > 0 && google.Words[buffer[^1]].Paragraph != google.Words[w].Paragraph;
            if (buffer.Count > 0 && (paragraphChanged || who != current || buffer.Count >= 30)) Flush();
            buffer.Add(w);
            current = who;
            if (buffer.Count >= 8 && google.Words[w].Text.TrimEnd('"', '\'', ')').EndsWith(['.', '?', '!'])) Flush();
        }
        Flush();

        var notes = new List<string>
        {
            $"DERIVED from {googleName} (an independent transcript, kept verbatim beside this file)\n"
            + $"by aligning its words to {vibeName}. {matched} of {gt.Count} Google tokens matched.",
            "Times are ESTIMATES: a matched word takes the time of its Vibe cue; the rest are\n"
            + "interpolated between matched neighbours, so the error grows with the length of the gap.",
            "Speaker ids are PROJECTED from Vibe's diarization and are the same namespaced ids as\n"
            + $"{vibeName}, so the same --speakers mapping applies. A word whose matched neighbours\n"
            + "disagree, or sit in another paragraph, is left unlabeled rather than guessed.",
        };
        // Cues must be in time order for VTT; interpolation keeps them so, but keep it explicit.
        return Vtt.Render(cues, notes);
    }

    private static string RenderReport(List<Cue> cues, List<VibeToken> vt, List<GoogleToken> gt, GoogleTranscript google,
        int[] vMatch, int[] gMatch, double[] times, double[] spread, string vibeName, string googleName, string? assemblyPath)
    {
        var matched = vMatch.Count(m => m >= 0);
        var sb = new StringBuilder();
        sb.AppendLine("# Cross-check: Vibe vs. Google");
        sb.AppendLine();
        sb.AppendLine($"Vibe: `{vibeName}`. Google: `{googleName}`. Two engines, two microphones, one conversation.");
        sb.AppendLine("Where they agree, the words are corroborated. Where they disagree, **neither is the correction");
        sb.AppendLine("of the other**: listen to the audio at that time and decide.");
        sb.AppendLine();
        sb.AppendLine("| | Vibe | Google |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Words (tokens) | {vt.Count} | {gt.Count} |");
        sb.AppendLine($"| Matched by the other | {matched} ({Fmt.Percent(vt.Count == 0 ? 0 : matched / (double)vt.Count)}) | {matched} ({Fmt.Percent(gt.Count == 0 ? 0 : matched / (double)gt.Count)}) |");
        sb.AppendLine();
        if (google.TimestampLines > 0)
            sb.AppendLine($"The Google file had {google.TimestampLines} timestamp line(s). They were set aside: its clock started on another device.");
        var labels = google.Words.Select(w => w.Label).Where(l => l is not null).Distinct().ToList();
        if (labels.Count > 0)
            sb.AppendLine($"The Google file labels speakers itself ({string.Join(", ", labels)}). Those labels are its own id space and are not used; the projected ids come from Vibe.");
        if (google.TimestampLines > 0 || labels.Count > 0) sb.AppendLine();
        if (vt.Count > 0 && matched / (double)vt.Count < 0.3)
        {
            sb.AppendLine("**Under 30% of Vibe's words matched.** Check these are transcripts of the same recording before reading further.");
            sb.AppendLine();
        }

        // Agreement by minute, on the recording's timeline.
        sb.AppendLine("## Agreement by minute");
        sb.AppendLine();
        sb.AppendLine("Agreement = matched / the larger of the two word counts. Below 50% with real speech is worth a listen.");
        sb.AppendLine();
        sb.AppendLine("| Minute | Vibe | Google | Matched | Agreement | |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|");
        var end = Math.Max(cues.Count > 0 ? cues.Max(c => c.Stop) : 0, times.DefaultIfEmpty(0).Max());
        // Minutes where Vibe has nothing are merged into one row: Google's words there carry times
        // interpolated across the whole gap, so a per-minute split of them would be invented detail.
        (double Start, double Stop, int Google)? silent = null;
        void FlushSilent()
        {
            if (silent is not { } s) return;
            sb.AppendLine($"| {Fmt.Clock(s.Start)}–{Fmt.Clock(s.Stop)} | 0 | {s.Google} | 0 | — | Vibe has nothing here; Google's times are estimates |");
            silent = null;
        }
        for (var start = 0.0; start < end; start += ReportWindowSeconds)
        {
            var stop = start + ReportWindowSeconds;
            var v = vt.Where(t => t.Time >= start && t.Time < stop).ToList();
            var g = Enumerable.Range(0, gt.Count).Where(j => times[j] >= start && times[j] < stop).ToList();
            var m = Enumerable.Range(0, vt.Count).Count(i => vMatch[i] >= 0 && vt[i].Time >= start && vt[i].Time < stop);
            if (v.Count == 0 && g.Count == 0) continue;
            if (v.Count == 0)
            {
                silent = silent is { } s ? (s.Start, stop, s.Google + g.Count) : (start, stop, g.Count);
                continue;
            }
            FlushSilent();
            var agreement = m / (double)Math.Max(1, Math.Max(v.Count, g.Count));
            var flag = Math.Max(v.Count, g.Count) >= 10 && agreement < 0.5 ? "**check**" : "";
            sb.AppendLine($"| {Fmt.Clock(start)} | {v.Count} | {g.Count} | {m} | {Fmt.Percent(agreement)} | {flag} |");
        }
        FlushSilent();
        sb.AppendLine();

        var untranscribed = UntranscribedWindows(assemblyPath);

        // Google heard words Vibe has nothing for.
        sb.AppendLine("## Google has words Vibe does not");
        sb.AppendLine();
        sb.AppendLine($"Runs of {MinStretchTokens}+ Google words with no match in Vibe. Inside a clip the gate excluded, this is the");
        sb.AppendLine("missing stretch. Inside a skipped silence or a dropped-as-noise stretch, the cut thresholds may have");
        sb.AppendLine("thrown away real speech. Elsewhere, Vibe dropped or garbled it, or Google invented it.");
        sb.AppendLine();
        var googleRuns = Runs(gMatch).ToList();
        if (googleRuns.Count == 0) sb.AppendLine("_None._");
        foreach (var (from, to) in googleRuns)
        {
            var words = gt.Skip(from).Take(to - from).Select(t => t.Word).Distinct().ToList();
            var text = string.Join(' ', words.Select(w => google.Words[w].Text));
            var t0 = times[from];
            var t1 = times[to - 1];
            var where = untranscribed.Where(w => w.Start < t1 + 1 && t0 - 1 < w.Stop).Select(w => w.What).Distinct().ToList();
            var approx = spread[from] > 30 ? $" (times interpolated across {Fmt.F(spread[from], 0)} s, so rough)" : "";
            sb.AppendLine($"- **~{Fmt.Clock(t0)}–{Fmt.Clock(t1)}**, {to - from} words{approx}"
                + (where.Count > 0 ? $" — inside {string.Join("; ", where)}" : ""));
            sb.AppendLine($"  > {Clip(text)}");
        }
        sb.AppendLine();

        // Vibe has words Google does not.
        sb.AppendLine("## Vibe has words Google does not");
        sb.AppendLine();
        sb.AppendLine($"Runs of {MinStretchTokens}+ Vibe words with no match in Google. A repeated phrase here is a whisper loop;");
        sb.AppendLine("a fluent passage may be speech Google missed (it was further away, or it dropped it).");
        sb.AppendLine();
        var vibeRuns = Runs(vMatch).ToList();
        if (vibeRuns.Count == 0) sb.AppendLine("_None._");
        foreach (var (from, to) in vibeRuns)
        {
            var cueIndexes = vt.Skip(from).Take(to - from).Select(t => t.Cue).Distinct().ToList();
            var text = string.Join(' ', cueIndexes.Select(c => (cues[c].Label is { } l ? $"[{l}] " : "") + cues[c].Text));
            sb.AppendLine($"- **{Fmt.Clock(vt[from].Time)}–{Fmt.Clock(vt[to - 1].Time)}**, {to - from} words");
            sb.AppendLine($"  > {Clip(text)}");
        }
        sb.AppendLine();

        // Silence tokens: the lines whisper invents. Did Google hear them?
        var tokensByCue = vt.Select((t, i) => (t.Cue, i)).GroupBy(x => x.Cue).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        var silenceCues = Enumerable.Range(0, cues.Count)
            .Where(c => QualityGate.HallucinationStrings.Contains(Fmt.Squash(cues[c].Text).ToLowerInvariant())).ToList();
        if (silenceCues.Count > 0)
        {
            sb.AppendLine("## Lines whisper is known to invent");
            sb.AppendLine();
            sb.AppendLine("Each Vibe line below is a phrase whisper produces over silence. If Google heard the same words at");
            sb.AppendLine("that point, it was probably said. If not, it is probably invented. Either way, it is a hint, not proof.");
            sb.AppendLine();
            foreach (var c in silenceCues)
            {
                var heard = tokensByCue.TryGetValue(c, out var idx) && idx.All(i => vMatch[i] >= 0);
                sb.AppendLine($"- [{Fmt.Clock(cues[c].Start)}] \"{Fmt.Squash(cues[c].Text)}\"{(cues[c].Label is { } l ? $" (id {l})" : " (unlabeled)")}: "
                    + (heard ? "Google heard it too." : "**Google has no such words here.**"));
            }
            sb.AppendLine();
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Clip(string text) => text.Length > 400 ? text[..400] + " …" : text;

    private static IEnumerable<(int From, int To)> Runs(int[] matches)
    {
        var start = -1;
        for (var i = 0; i <= matches.Length; i++)
        {
            var unmatched = i < matches.Length && matches[i] < 0;
            if (unmatched && start < 0) start = i;
            else if (!unmatched && start >= 0)
            {
                if (i - start >= MinStretchTokens) yield return (start, i);
                start = -1;
            }
        }
    }

    /// <summary>Copy the Google file into the output folder verbatim (it is evidence), align it, and
    /// write <c>transcript_google.vtt</c> and <c>cross-check.md</c>.</summary>
    public static Result RunInto(string googlePath, string vibeVttPath, string outDir, string? assemblyPath)
    {
        if (!File.Exists(googlePath)) throw new TranscribeException($"No such Google transcript: {googlePath}");
        var kept = Path.Combine(outDir, Path.GetFileName(googlePath));
        if (!string.Equals(Path.GetFullPath(kept), Path.GetFullPath(googlePath), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(kept) && !File.ReadAllBytes(kept).SequenceEqual(File.ReadAllBytes(googlePath)))
                throw new TranscribeException($"{kept} already exists with different content; not overwriting evidence.");
            File.Copy(googlePath, kept, overwrite: true);
            Console.WriteLine($"kept   {kept} (verbatim copy)");
        }
        var google = GoogleTranscript.Parse(File.ReadAllText(googlePath));
        if (google.Words.Count == 0) throw new TranscribeException($"{googlePath} has no words.");
        var cues = Vtt.Parse(File.ReadAllText(vibeVttPath));
        assemblyPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vibeVttPath))!, "transcript-assembly.json");
        var result = Build(cues, google, Path.GetFileName(vibeVttPath), Path.GetFileName(kept), assemblyPath);
        var vttOut = Path.Combine(outDir, "transcript_google.vtt");
        var reportOut = Path.Combine(outDir, "cross-check.md");
        Fmt.WriteText(vttOut, result.Vtt);
        Fmt.WriteText(reportOut, result.Report);
        Console.WriteLine($"google {result.Matched} of {result.GoogleTokens} Google tokens matched {result.VibeTokens} Vibe tokens "
            + $"({Fmt.Percent(result.GoogleTokens == 0 ? 0 : result.Matched / (double)result.GoogleTokens)} of Google's)");
        Console.WriteLine($"wrote  {vttOut}");
        Console.WriteLine($"wrote  {reportOut}");
        return result;
    }
}
