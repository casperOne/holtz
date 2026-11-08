using System.Text.Json.Nodes;

namespace Holtz;

/// <summary>The server's <c>verbose_json</c> response (or Vibe's older bare JSON array) as cues.</summary>
public static class Segments
{
    /// <summary>Segments to cues. The server's own VTT rendering carries no speaker, so the
    /// transcript is built from these. A segment with no <c>speaker</c> stays unlabeled rather
    /// than being given to whoever spoke last: diarization declines to label a fragment more
    /// often than it mislabels one, and a guess here would be invisible downstream.</summary>
    public static List<Cue> ToCues(JsonNode? payload)
    {
        if (payload is JsonObject obj) payload = obj["segments"];
        if (payload is not JsonArray array) return [];
        var cues = new List<Cue>();
        foreach (var item in array)
        {
            if (item is not JsonObject segment) continue;
            var text = (AsString(segment["text"]) ?? "").Trim();
            if (text.Length == 0) continue;
            if (AsDouble(segment["start"]) is not { } start) continue;
            var stop = AsDouble(segment["end"]) ?? AsDouble(segment["stop"]) ?? start;
            int? speaker = segment["speaker"] is JsonValue sv && sv.TryGetValue<int>(out var id) ? id : null;
            double? noSpeech = AsDouble(segment["no_speech_prob"]);
            cues.Add(new Cue(start, stop, text, speaker, null, noSpeech));
        }
        return cues;
    }

    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    internal static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s)
            && double.TryParse(s, System.Globalization.NumberStyles.Float, Fmt.Inv, out d)) return d;
        return null;
    }
}
