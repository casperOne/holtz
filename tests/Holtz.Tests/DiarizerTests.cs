namespace Holtz.Tests;

public class DiarizerTests
{
    [Theory]
    [InlineData(@"C:\models\diar_streaming_sortformer_4spk-v2.q8_0.gguf", 4)]
    [InlineData("/m/diar_sortformer_8SPK.gguf", 8)]
    [InlineData("/m/nemotron-3-diarization-Q8_0.gguf", Diarizer.NemotronSlots)]
    [InlineData("/m/diar_unknown.onnx", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void SlotsComeFromTheModelFileName(string? path, int? slots) => Assert.Equal(slots, Diarizer.Slots(path));

    [Fact]
    public void APassIsFullWhenItsIdBandFillsEverySlot()
    {
        var cues = new List<Cue>
        {
            // Clip 1, first pass: three of four slots.
            Make.Cue(0, "a", 0), Make.Cue(3, "b", 1), Make.Cue(6, "c", 2),
            // Clip 1, re-cut: all four, one person split across two of them.
            Make.Cue(60, "d", 10), Make.Cue(63, "e", 11), Make.Cue(66, "f", 12), Make.Cue(69, "g", 13),
            // Clip 2: all four.
            Make.Cue(120, "h", 100), Make.Cue(123, "i", 101), Make.Cue(126, "j", 102), Make.Cue(129, "k", 103),
            Make.Cue(132, "unlabeled"),
        };
        var full = Diarizer.FullPasses(cues, 4);
        Assert.Equal(2, full.Count);
        Assert.Equal([10, 11, 12, 13], full[0].Ids);
        Assert.Equal(60, full[0].Start);
        Assert.Equal(71, full[0].Stop);
        Assert.Equal([100, 101, 102, 103], full[1].Ids);
        Assert.Empty(Diarizer.FullPasses(cues, 8));
    }

    [Fact]
    public void TheWorksheetSaysWhenEverySlotWasUsedAndWhenTooManyPeopleWereNamed()
    {
        var cues = Enumerable.Range(0, 8).Select(i => Make.Cue(i * 3, $"line {i}", i % 4)).ToList();
        var sheet = Pipeline.SpeakerWorksheet([("Clip 1", cues)], ["Ana", "Ben", "Cy", "Di"], 4);
        Assert.Contains("tracks at most 4 voices per pass", sheet);
        Assert.Contains("4 people were named for 4 slots", sheet);
        Assert.Contains("**All 4 speaker slots used** by ids 0, 1, 2, 3 (00:00-00:23)", sheet);

        var roomy = Pipeline.SpeakerWorksheet([("Clip 1", cues)], ["Ana", "Ben"], 8);
        Assert.DoesNotContain("slots used", roomy);
        Assert.DoesNotContain("people were named", roomy);

        var unknown = Pipeline.SpeakerWorksheet([("Clip 1", cues)], ["Ana", "Ben", "Cy", "Di"]);
        Assert.DoesNotContain("slot", unknown);
    }

    [Fact]
    public void TheWorksheetCommandReadsTheModelFromTheRunsRecord()
    {
        var dir = Directory.CreateTempSubdirectory("holtz-slots-").FullName;
        try
        {
            var vtt = Path.Combine(dir, "transcript.vtt");
            File.WriteAllText(vtt, Vtt.Render(Enumerable.Range(0, 4).Select(i => Make.Cue(i * 3, $"line {i}", i))));
            var sheet = Path.Combine(dir, "sheet.md");

            Assert.Equal(0, Cli.Main(["worksheet", vtt, "--out", sheet]));
            Assert.DoesNotContain("slots used", File.ReadAllText(sheet));

            File.WriteAllText(Path.Combine(dir, "transcript-assembly.json"),
                """{"request_fields": {"diarize_model": "C:\\m\\diar_streaming_sortformer_4spk-v2.q8_0.gguf"}}""");
            Assert.Equal(0, Cli.Main(["worksheet", vtt, "--out", sheet]));
            Assert.Contains("**All 4 speaker slots used**", File.ReadAllText(sheet));

            Assert.Equal(0, Cli.Main(["worksheet", vtt, "--out", sheet, "--speaker-slots", "8"]));
            Assert.DoesNotContain("slots used", File.ReadAllText(sheet));
            Assert.Equal(2, Cli.Main(["worksheet", vtt, "--out", sheet, "--speaker-slots", "2.5"]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
