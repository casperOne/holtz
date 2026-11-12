using System.Globalization;
using System.Text.RegularExpressions;

namespace Holtz;

/// <summary>What the diarization model can and cannot tell apart. A model has a fixed number of
/// speaker slots per pass (Sortformer 4, Vibe's Nemotron diarizer 8). When a pass uses all of them,
/// one more voice cannot get an id of its own: it is folded into whichever slot sounds closest. A
/// pass that also splits one person across two slots leaves a second person with nothing, even when
/// the people present fit the model. Measured on a real four-person meeting: one 30-minute re-cut used
/// all four slots, two of them on one person, and the fourth person's lines (two stretches between
/// 32:18 and 36:02) came out under two other people's ids. Re-running just those 3:45 on their own gave
/// that person an id of their own.</summary>
public static partial class Diarizer
{
    /// <summary>Vibe's Nemotron diarizer, whose file name does not carry its slot count.</summary>
    public const int NemotronSlots = 8;

    [GeneratedRegex(@"(\d+)\s*spk", RegexOptions.IgnoreCase)]
    private static partial Regex SlotsInName();

    /// <summary>Speaker slots of a diarization model, from its file name
    /// (<c>diar_streaming_sortformer_4spk-v2.q8_0.gguf</c> → 4), or null when the name does not say.</summary>
    public static int? Slots(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        var name = Path.GetFileName(modelPath);
        var match = SlotsInName().Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, Fmt.Inv, out var slots) && slots > 0)
            return slots;
        return name.Contains("nemotron", StringComparison.OrdinalIgnoreCase) ? NemotronSlots : null;
    }

    /// <summary>One diarization pass whose ids filled every slot the model has.</summary>
    public sealed record FullPass(IReadOnlyList<int> Ids, double Start, double Stop);

    /// <summary>The passes that used every slot. A pass is a clip or a re-cut inside it, which is
    /// exactly an id band of <see cref="Pipeline.RetryNamespace"/>: ids are namespaced per clip
    /// (+100) and per re-cut (+10), and a model's own ids start at 0.</summary>
    public static List<FullPass> FullPasses(IEnumerable<Cue> cues, int slots) =>
        cues.Where(c => c.Speaker is not null)
            .GroupBy(c => c.Speaker!.Value / Pipeline.RetryNamespace)
            .Select(g => (Ids: g.Select(c => c.Speaker!.Value).Distinct().Order().ToList(), Cues: g.ToList()))
            .Where(p => p.Ids.Count >= slots)
            .OrderBy(p => p.Cues.Min(c => c.Start))
            .Select(p => new FullPass(p.Ids, p.Cues.Min(c => c.Start), p.Cues.Max(c => c.Stop)))
            .ToList();

    /// <summary>The model a run used, from the <c>transcript-assembly.json</c> beside a transcript,
    /// so a worksheet rebuilt later knows the same limit. Null when there is no record.</summary>
    public static string? ModelFromAssembly(string transcriptPath)
    {
        var assembly = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transcriptPath))!, "transcript-assembly.json");
        if (!File.Exists(assembly)) return null;
        return Segments.AsString(Segments.TryParse(File.ReadAllText(assembly))?["request_fields"]?["diarize_model"]);
    }
}
