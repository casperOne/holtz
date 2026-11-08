using System.Text.Json.Nodes;

namespace Holtz.Tests;

internal static class Make
{
    public static Cue Cue(double start, string text, int? speaker = null, double length = 2.0) =>
        new(start, start + length, text, speaker);

    /// <summary>A healthy-looking exchange: distinct lines, two speakers, a realistic word rate.</summary>
    public static List<Cue> Conversation(int lines, double start = 0, double step = 3.0) =>
        Enumerable.Range(0, lines)
            .Select(i => Cue(start + i * step, $"Line {i} talks about topic number {i} in some detail.", i % 2, 2.5))
            .ToList();
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "00m00s", "00:00", "00:00:00.000")]
    [InlineData(61.4, "01m01s", "01:01", "00:01:01.400")]
    [InlineData(3725.0, "62m05s", "62:05", "01:02:05.000")]
    public void Timestamps(double seconds, string stamp, string clock, string vtt)
    {
        Assert.Equal(stamp, Fmt.Stamp(seconds));
        Assert.Equal(clock, Fmt.Clock(seconds));
        Assert.Equal(vtt, Fmt.VttTimestamp(seconds));
    }

    [Fact]
    public void TimePastAnHourShowsHours() => Assert.Equal("1:02:05", Fmt.Time(3725));

    [Fact]
    public void NumbersAreInvariantWhateverTheCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("-40.5", Fmt.G(-40.5));
            Assert.Equal("1.250", Fmt.F(1.25, 3));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }
}

public class VttTests
{
    private const string Sample = """
        WEBVTT

        NOTE
        provenance that must not leak

        1
        00:00.030 --> 00:06.870
        [Speaker 1] Okay. All right.

        00:00:07.000 --> 00:00:09.500 align:start
        <v Speaker 2>Two lines
        of payload</v>

        00:10.000 --> 00:11.000
        Unlabeled fragment.

        00:12.000 --> 00:13.000
        <v Ana>Named voice.
        """;

    [Fact]
    public void ParsesVibeDialectSpecVoicesNotesAndMultilinePayloads()
    {
        var cues = Vtt.Parse("﻿" + Sample);
        Assert.Equal(4, cues.Count);
        Assert.Equal(new Cue(0.03, 6.87, "Okay. All right.", 1), cues[0]);
        Assert.Equal(2, cues[1].Speaker);
        Assert.Equal("Two lines of payload", cues[1].Text);
        Assert.False(cues[2].Labeled);
        Assert.Equal("Ana", cues[3].Voice);
        Assert.DoesNotContain(cues, c => c.Text.Contains("provenance"));
    }

    [Fact]
    public void RejectsNonVtt() => Assert.Throws<TranscribeException>(() => Vtt.Parse("hello"));

    [Fact]
    public void RenderRoundTripsAndKeepsNotesOutOfTheCues()
    {
        var cues = Vtt.Parse(Sample);
        var again = Vtt.Parse(Vtt.Render(cues, ["a note\nover two lines"]));
        Assert.Equal(cues.Select(c => (c.Start, c.Text, c.Speaker, c.Voice)), again.Select(c => (c.Start, c.Text, c.Speaker, c.Voice)));
    }

    [Fact]
    public void SuspectNotesAreCollectedWholeAndOthersAreNot()
    {
        var raw = Vtt.Render([Make.Cue(0, "hi")], ["plain provenance", $"{Vtt.SuspectNotePrefix} — check these\n  [00:30] \"Thank you.\""]);
        var notes = Vtt.SuspectNotes(raw);
        Assert.Equal(2, notes.Count);
        Assert.StartsWith(Vtt.SuspectNotePrefix, notes[0]);
        Assert.DoesNotContain(notes, n => n.Contains("plain provenance"));
    }

    [Fact]
    public void SegmentsKeepUnlabeledFragmentsUnlabeled()
    {
        var payload = JsonNode.Parse("""
            {"text":"x","segments":[
              {"start":0.0,"end":1.5,"text":" Hello.","speaker":0,"no_speech_prob":0.01},
              {"start":1.6,"end":2.0,"text":" Hm."},
              {"start":2.1,"end":2.5,"text":"   "},
              {"start":2.6,"end":3.0,"text":" Bool speaker.","speaker":true}
            ]}
            """);
        var cues = Segments.ToCues(payload);
        Assert.Equal(3, cues.Count);
        Assert.Equal(0, cues[0].Speaker);
        Assert.Equal(0.01, cues[0].NoSpeechProb);
        Assert.Null(cues[1].Speaker);
        Assert.Null(cues[2].Speaker);
    }
}

public class QualityGateTests
{
    [Fact]
    public void HealthyConversationPasses() => Assert.Empty(QualityGate.Findings(Make.Conversation(200)));

    [Fact]
    public void SilenceTokenRunNamesItsStart()
    {
        var cues = Make.Conversation(10);
        cues.AddRange(Enumerable.Range(0, 6).Select(i => Make.Cue(40 + i * 2, "Thank you.")));
        var finding = Assert.Single(QualityGate.Findings(cues), f => f.Kind == "silence_hallucination");
        Assert.Equal(40, finding.RecutFrom);
    }

    [Fact]
    public void OneCueThatIsOnlyASilenceTokenFailsWhateverItsLength()
    {
        var finding = Assert.Single(QualityGate.Findings([Make.Cue(0, "Thank you.")]));
        Assert.Equal("all_hallucination", finding.Kind);
        Assert.Null(finding.RecutFrom);
    }

    [Fact]
    public void PhraseLoopWithoutAdjacencyIsCaught()
    {
        var cues = Make.Conversation(40);
        for (var i = 0; i < 8; i++) cues.Insert(20 + i * 2, Make.Cue(60 + i, "So this is the total amount."));
        var findings = QualityGate.Findings(cues);
        Assert.Contains(findings, f => f.Kind == "phrase_loop" && f.RecutFrom == 60);
    }

    [Fact]
    public void ShortLineCycleIsCaughtAndWalkedBackOverAPhaseSlip()
    {
        var cues = Make.Conversation(10);
        // A slip (the same line twice) and then a long two-line cycle.
        cues.Add(Make.Cue(100, "So that's 1,200."));
        cues.Add(Make.Cue(101, "Okay."));
        cues.Add(Make.Cue(102, "Okay."));
        for (var i = 0; i < 16; i++) cues.Add(Make.Cue(103 + i, i % 2 == 0 ? "So that's 1,200." : "Okay."));
        var (lines, start, period) = QualityGate.LongestCycle(cues)!.Value;
        Assert.Equal(2, period);
        Assert.Equal(10, start); // walked back to the first "So that's 1,200."
        Assert.True(lines >= 16);
        Assert.Contains(QualityGate.Findings(cues), f => f.Kind == "cycle_loop" && f.RecutFrom == 100);
    }

    [Fact]
    public void LocalLoopsFindInterleavedRepeatsAcrossPunctuation()
    {
        var cues = Make.Conversation(6);
        for (var i = 0; i < 6; i++)
        {
            cues.Add(Make.Cue(20 + i * 2, i % 2 == 0 ? "So I did nine miles." : "So I did nine miles"));
            cues.Add(Make.Cue(21 + i * 2, "Okay."));
        }
        var (first, last, hits) = Assert.Single(QualityGate.LocalLoops(cues));
        Assert.Equal((6, 16, 6), (first, last, hits));
    }

    [Theory]
    [InlineData("Okay sure.", 6, 1)]                 // too short to count
    [InlineData("A real line of words.", 2, 1)]      // not repeated enough
    [InlineData("A real line of words.", 4, 6)]      // too far apart
    public void LocalLoopsIgnoreOrdinaryRepetition(string line, int copies, int spacing)
    {
        var cues = new List<Cue>();
        for (var i = 0; i < copies; i++)
        {
            cues.Add(Make.Cue(cues.Count * 2, line));
            for (var k = 1; k < spacing; k++) cues.Add(Make.Cue(cues.Count * 2, $"Filler {i} number {k} right here."));
        }
        Assert.Empty(QualityGate.LocalLoops(cues));
    }

    [Fact]
    public void ALoopOnlyFailsAStretchWhenItDominatesIt()
    {
        var loop = Enumerable.Range(0, 5).Select(i => Make.Cue(i * 10, "Words that were never spoken here.")).ToList();
        Assert.NotEmpty(QualityGate.StretchFindings(loop));
        Assert.NotEmpty(QualityGate.StretchFindings([Make.Cue(0, "Thank you.")]));

        var diluted = Make.Conversation(25).Concat(loop).ToList();
        Assert.Empty(QualityGate.StretchFindings(diluted));
        // Short real backchannel: repeats, but no loop.
        Assert.Empty(QualityGate.StretchFindings([Make.Cue(0, "Okay."), Make.Cue(2, "Yeah."), Make.Cue(4, "Okay."), Make.Cue(6, "Okay.")]));
    }

    [Fact]
    public void LowWordRateOverALongStretch()
    {
        var cues = new List<Cue> { Make.Cue(0, "Hello there."), Make.Cue(400, "Goodbye now.") };
        Assert.Contains(QualityGate.Findings(cues), f => f.Kind == "word_rate");
    }

    [Fact]
    public void FallbackWindowSegmentation()
    {
        var cues = Enumerable.Range(0, 6).Select(i => new Cue(i * 30, i * 30 + 29.5, $"Different words number {i} here.")).ToList();
        Assert.Contains(QualityGate.Findings(cues), f => f.Kind == "fallback_window");
    }
}
