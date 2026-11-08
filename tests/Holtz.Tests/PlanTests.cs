namespace Holtz.Tests;

public class PlanTests
{
    private static readonly PlanSettings Defaults = new();

    [Fact]
    public void ParsesSilencedetectIncludingARunToTheEnd()
    {
        const string stderr = """
            [silencedetect @ 0x1] silence_start: -0.002
            [silencedetect @ 0x1] silence_end: 25.5 | silence_duration: 25.5
            [silencedetect @ 0x1] silence_start: 280
            """;
        var runs = Audio.ParseSilencedetect(stderr, 300);
        Assert.Equal([new Span(0, 25.5), new Span(280, 300)], runs);
    }

    [Fact]
    public void ParsesAstatsKeepingDigitalSilence()
    {
        const string output = """
            frame:0    pts:0       pts_time:0
            lavfi.astats.Overall.RMS_level=-21.3
            frame:1    pts:16000   pts_time:1
            lavfi.astats.Overall.RMS_level=-inf
            frame:2    pts:32000   pts_time:2
            lavfi.astats.Overall.RMS_level=nan
            """;
        var levels = Audio.ParseAstats(output);
        Assert.Equal(2, levels.Count);
        Assert.Equal(-21.3, levels[0].Db);
        Assert.True(double.IsNegativeInfinity(levels[1].Db));
    }

    [Fact]
    public void NoSilenceMeansOneClipTheWholeFile()
    {
        var plan = Planner.Build(120, [], Defaults);
        var clip = Assert.Single(plan.Clips);
        Assert.Equal((0.0, 120.0), (clip.Start, clip.Stop));
    }

    [Fact]
    public void ClipsGetPreRollAndTailAndNamespacedIds()
    {
        var plan = Planner.Build(100, [new Span(30, 60)], Defaults);
        Assert.Equal(2, plan.Clips.Count);
        Assert.Equal(30 + Defaults.Tail, plan.Clips[0].Stop);
        Assert.Equal(60 - Defaults.PreRoll, plan.Clips[1].Start, 6);
        Assert.Equal(100, plan.Clips[1].SpeakerOffset);
        Assert.Equal("clip_02_01m00s-01m40s", plan.Clips[1].Name);
    }

    [Fact]
    public void BoundaryInsideDeadAirMovesToWhereSpeechResumes()
    {
        // The coarse pass says speech resumes at 60, but a blip at 60-60.5 is followed by more silence.
        var fine = new List<Span> { new(30, 60), new(60.5, 90) };
        Assert.Equal(90, Planner.FirstSpeechAfter(60, fine));
        // A gap longer than a blip is speech: stay put.
        Assert.Equal(60, Planner.FirstSpeechAfter(60, [new Span(30, 60), new Span(65, 90)]));
    }

    [Fact]
    public void QuietBlipsAreBridgedIntoOneLongSilenceButSpeechIsNot()
    {
        var fine = new List<Span> { new(10, 22), new(23, 35), new(36, 48) };
        var loudness = Enumerable.Range(0, 60).Select(s => new Level(s, s switch
        {
            22 => -39.5,   // a pencil: bridged
            35 => -30.0,   // a word: not bridged
            _ => -60.0,
        })).ToList();
        var (joined, bridged) = Planner.BridgeNoiseBlips(fine, loudness, Defaults.BridgePeakDb);
        Assert.Equal([new Span(10, 35), new Span(36, 48)], joined);
        Assert.Equal(-39.5, Assert.Single(bridged).PeakDb);
    }

    [Fact]
    public void ABridgedSilenceLongEnoughBecomesACut()
    {
        // Two 15 s runs split by a quiet blip: neither reaches 20 s on its own.
        var fine = new List<Span> { new(30, 45), new(46, 61) };
        var loudness = Enumerable.Range(0, 100).Select(s => new Level(s, s is >= 30 and < 61 ? (s == 45 ? -39.0 : -70.0) : -20.0)).ToList();
        var plan = Planner.Build(100, [], Defaults, fine, loudness);
        Assert.Equal(2, plan.Clips.Count);
        Assert.Single(plan.Bridged);
    }

    [Fact]
    public void ACandidateThatNeverReachesSpeechLevelIsDroppedBeforeNumbering()
    {
        var loudness = Enumerable.Range(0, 100).Select(s => new Level(s, s switch
        {
            < 20 => -20.0,
            >= 50 and < 55 => -44.0, // a rustle
            >= 80 => -20.0,
            _ => -70.0,
        })).ToList();
        var plan = Planner.Build(100, [new Span(20, 50), new Span(55, 80)], Defaults, null, loudness);
        Assert.Equal(2, plan.Clips.Count);
        Assert.Equal(2, plan.Clips[1].Index);
        Assert.Single(plan.TooQuiet);
    }

    [Fact]
    public void AWindowIsHonouredExactlyAndKeepsTheRecordingTimeline()
    {
        var plan = Planner.Build(300, [new Span(100, 130)], Defaults with { From = 50, To = 200 });
        Assert.Equal(2, plan.Clips.Count);
        Assert.Equal(50, plan.Clips[0].Start);
        Assert.Equal(200, plan.Clips[1].Stop);
    }

    [Fact]
    public void RenderMentionsEveryClipAndSkip()
    {
        var text = Planner.Render(Planner.Build(100, [new Span(30, 60)], Defaults));
        Assert.Contains("2 clip(s)", text);
        Assert.Contains("00:30-01:00", text);
    }
}
