using System.Text.RegularExpressions;

namespace Holtz;

/// <summary>One timed line of transcript.</summary>
/// <param name="Speaker">Diarization id. A label, never an identity: unstable across
/// recordings and across clips of one recording, sparse, and sometimes absent.</param>
/// <param name="Voice">A non-numeric WebVTT voice span (<c>&lt;v Nick&gt;</c>), kept as written.</param>
/// <param name="NoSpeechProb">Whisper's own confidence that the window held no speech.
/// Carried for the record only: it has been confidently wrong on every fabrication seen.</param>
public sealed record Cue(
    double Start,
    double Stop,
    string Text,
    int? Speaker = null,
    string? Voice = null,
    double? NoSpeechProb = null)
{
    public bool Labeled => Speaker is not null || Voice is not null;
    public string? Label => Speaker?.ToString(Fmt.Inv) ?? Voice;
}

/// <summary>WebVTT in Vibe's dialect: the speaker is a literal <c>[Speaker N]</c> prefix in the
/// cue text. The spec's own <c>&lt;v Speaker N&gt;</c> voice span is read too.</summary>
public static partial class Vtt
{
    /// <summary>A NOTE containing this carries something the reader must act on, so the
    /// normalizer reproduces it in the readable transcript's header. Other NOTE blocks are
    /// provenance and stay where they are.</summary>
    public const string SuspectNotePrefix = "SUSPECT CUES";

    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d+):(\d{2})\.(\d{1,3})\s*-->\s*(?:(\d+):)?(\d+):(\d{2})\.(\d{1,3})")]
    private static partial Regex CueLine();

    [GeneratedRegex(@"^\[Speaker\s+(\d+)\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerPrefix();

    [GeneratedRegex(@"^<v\s+(?:Speaker\s+)?([^>]+)>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceSpan();

    private static double Seconds(Group hours, Group minutes, Group seconds, Group millis) =>
        (hours.Success ? int.Parse(hours.Value, Fmt.Inv) : 0) * 3600
        + int.Parse(minutes.Value, Fmt.Inv) * 60
        + int.Parse(seconds.Value, Fmt.Inv)
        + int.Parse(millis.Value.PadRight(3, '0'), Fmt.Inv) / 1000.0;

    public static bool LooksLikeVtt(string raw) =>
        raw.TrimStart('﻿').TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse WebVTT into cues. Tolerates a BOM, cue identifiers, NOTE/STYLE/REGION
    /// blocks, cue settings and multi-line payloads. A cue with no recognizable speaker has
    /// no speaker, so it renders as an unlabeled fragment rather than being attributed.</summary>
    public static List<Cue> Parse(string raw)
    {
        var lines = raw.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].Trim().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            throw new TranscribeException("Not a WebVTT file: expected a 'WEBVTT' header line.");

        var cues = new List<Cue>();
        (double Start, double Stop)? current = null;
        var payload = new List<string>();
        var skipping = false;

        void Flush()
        {
            if (current is null) return;
            var text = string.Join(' ', payload.Where(p => p.Length > 0));
            int? speaker = null;
            string? voice = null;
            var match = SpeakerPrefix().Match(text);
            if (!match.Success) match = VoiceSpan().Match(text);
            if (match.Success)
            {
                var token = match.Groups[1].Value.Trim();
                text = text[match.Length..];
                if (int.TryParse(token, System.Globalization.NumberStyles.None, Fmt.Inv, out var id)) speaker = id;
                else voice = token;
            }
            text = text.Replace("</v>", "").Trim();
            if (text.Length > 0)
                cues.Add(new Cue(current.Value.Start, current.Value.Stop, text, speaker, voice));
        }

        foreach (var line in lines.Skip(1))
        {
            var stripped = line.Trim();
            var cue = CueLine().Match(stripped);
            if (cue.Success)
            {
                Flush();
                skipping = false;
                var g = cue.Groups;
                current = (Seconds(g[1], g[2], g[3], g[4]), Seconds(g[5], g[6], g[7], g[8]));
                payload = [];
                continue;
            }
            if (stripped.Length == 0)
            {
                Flush();
                current = null;
                payload = [];
                skipping = false;
                continue;
            }
            if (current is null)
            {
                // Between cues: a NOTE/STYLE/REGION block, or a cue identifier line.
                if (stripped.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
                    || stripped.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                    || stripped.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
                    skipping = true;
                continue;
            }
            if (!skipping) payload.Add(stripped);
        }
        Flush();

        if (cues.Count == 0) throw new TranscribeException("WebVTT file contains no cues with text.");
        return cues;
    }

    /// <summary>Lines of every NOTE block that carries <see cref="SuspectNotePrefix"/>. The
    /// parser skips notes by design, so without this a warning recorded at assembly time
    /// would never reach the file people actually read.</summary>
    public static List<string> SuspectNotes(string raw)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var stripped = line.Trim();
            if (stripped.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                var rest = stripped[4..].Trim();
                if (rest.Length > 0) current.Add(rest);
                blocks.Add(current);
                continue;
            }
            if (stripped.Length == 0) { current = null; continue; }
            current?.Add(stripped);
        }
        return blocks.Where(b => b.Any(l => l.Contains(SuspectNotePrefix))).SelectMany(b => b).ToList();
    }

    /// <summary>Write cues as WebVTT in Vibe's dialect, with provenance in NOTE blocks (part of
    /// WebVTT, skipped by every parser, so the file explains itself without the explanation
    /// leaking into the transcript).</summary>
    public static string Render(IEnumerable<Cue> cues, IEnumerable<string>? notes = null)
    {
        var lines = new List<string> { "WEBVTT", "" };
        foreach (var note in notes ?? [])
        {
            lines.Add("NOTE");
            lines.AddRange(note.Replace("\r\n", "\n").Split('\n'));
            lines.Add("");
        }
        foreach (var cue in cues.OrderBy(c => c.Start).ThenBy(c => c.Stop))
        {
            var text = cue.Speaker is { } id ? $"[Speaker {id}] {cue.Text}"
                : cue.Voice is { } voice ? $"<v {voice}>{cue.Text}"
                : cue.Text;
            lines.Add($"{Fmt.VttTimestamp(cue.Start)} --> {Fmt.VttTimestamp(cue.Stop)}");
            lines.Add(text);
            lines.Add("");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Load a transcript file: WebVTT (preferred), or a JSON array of
    /// <c>{start, stop|end, text, speaker}</c> segments, or a <c>verbose_json</c> response.
    /// Chosen by content, not by file name.</summary>
    public static List<Cue> Load(string path)
    {
        var raw = File.ReadAllText(path);
        if (LooksLikeVtt(raw)) return Parse(raw);
        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new TranscribeException($"{path} is neither WebVTT nor valid JSON: {ex.Message}");
        }
        var cues = Segments.ToCues(node);
        if (cues.Count == 0) throw new TranscribeException($"{path} contains no segments with text.");
        return cues;
    }
}
