namespace Holtz.Tests;

public class NormalizerTests
{
    [Fact]
    public void SpeakerMapParsingRejectsWhatItCannotRead()
    {
        Assert.Equal(new Dictionary<int, string> { [1] = "Ana", [101] = "Ben" }, Normalizer.ParseSpeakerMap(" 1=Ana, 101=Ben ,"));
        Assert.Throws<TranscribeException>(() => Normalizer.ParseSpeakerMap("Ana"));
        Assert.Throws<TranscribeException>(() => Normalizer.ParseSpeakerMap("x=Ana"));
        Assert.Throws<TranscribeException>(() => Normalizer.ParseSpeakerMap("1="));
        Assert.Throws<TranscribeException>(() => Normalizer.ParseSpeakerMap(" , "));
    }

    [Fact]
    public void CoalescesTurnsAndNeverSilentlyMislabels()
    {
        var cues = new List<Cue>
        {
            Make.Cue(0, "Hello.", 1), Make.Cue(2, "- How are you?", 1), Make.Cue(4, "Fine.", 2),
            Make.Cue(6, "Who said this?"), Make.Cue(8, "Unmapped.", 7),
        };
        var (text, unmapped) = Normalizer.Render(cues, new Dictionary<int, string> { [1] = "Ana", [2] = "Ben" },
            new Normalizer.Options { SourceName = "t.vtt", SourceSha256 = new string('a', 64), Title = "Test", Notes = ["a note"] });
        Assert.Contains("[00:00 Ana] Hello. How are you?", text);
        Assert.Contains("[00:04 Ben] Fine.", text);
        Assert.Contains("[00:06 ?] Who said this?", text);
        Assert.Contains("[00:08 Speaker 7] Unmapped.", text);
        Assert.Contains("# a note", text);
        Assert.Equal([7], unmapped);
    }

    [Theory]
    [InlineData(Normalizer.DefaultTurnGap, new[] { "[00:00 Ana] One. Two.", "[00:23 Ana] Three." })] // pauses of 9 s, then exactly 10 s
    [InlineData(5.0, new[] { "[00:00 Ana] One.", "[00:11 Ana] Two.", "[00:23 Ana] Three." })]
    [InlineData(30.0, new[] { "[00:00 Ana] One. Two. Three." })]
    public void ALongPauseStartsANewLineSoEveryLineKeepsATrueTime(double turnGap, string[] expected)
    {
        var cues = new List<Cue> { Make.Cue(0, "One.", 1), Make.Cue(11, "Two.", 1), Make.Cue(23, "Three.", 1) };
        var (text, _) = Normalizer.Render(cues, new Dictionary<int, string> { [1] = "Ana" },
            new Normalizer.Options { SourceName = "t.vtt", SourceSha256 = new string('a', 64), TurnGap = turnGap });
        Assert.Equal(expected, text.Split('\n').Where(l => l.StartsWith('[')));
    }

    [Fact]
    public void FillOnlyGoesIntoGapsNeverOverWhatVibeTranscribed()
    {
        var primary = new List<Cue> { Make.Cue(0, "a", 1), Make.Cue(60, "b", 1) };
        var fill = new List<Cue> { Make.Cue(1, "overlaps a"), Make.Cue(20, "in the gap"), Make.Cue(61, "overlaps b"), Make.Cue(90, "after the end") };
        var chosen = Normalizer.FillCues(primary, fill, 10).Select(c => c.Text).ToList();
        Assert.Equal(["in the gap", "after the end"], chosen);
    }
}

public class GoogleTests
{
    [Fact]
    public void ParsesPlainParagraphsAndToleratesLabelsAndTimestamps()
    {
        var google = GoogleTranscript.Parse("""
            First paragraph here.

            00:42
            Speaker 2
            Second one,
            continued.

            Speaker 1: Third.
            """);
        Assert.Equal(3, google.Paragraphs);
        Assert.Equal(1, google.TimestampLines);
        Assert.Equal(["First", "paragraph", "here.", "Second", "one,", "continued.", "Third."], google.Words.Select(w => w.Text));
        Assert.Equal("Speaker 2", google.Words[3].Label);
        Assert.Equal("Speaker 1", google.Words[^1].Label);
        Assert.Null(google.Words[0].Label);
    }

    [Theory]
    [InlineData("$1,170.", "1170")]
    [InlineData("Alright,", "all right")]
    [InlineData("What's", "whats")]
    [InlineData("nine", "9")]
    [InlineData("well—no", "well no")]
    [InlineData("50%", "50 %")]
    public void NormalizesBothSidesTheSameWay(string word, string tokens) =>
        Assert.Equal(tokens.Split(' '), Aligner.Tokens(word));

    [Fact]
    public void AlignsAcrossInsertionsAndDropsScatteredCoincidences()
    {
        string[] a = ["so", "the", "ratio", "is", "four", "to", "five", "okay", "right"];
        string[] b = ["um", "so", "the", "ratio", "is", "four", "to", "five", "x", "y", "okay", "z", "w", "right"];
        var pairs = Aligner.Align(a, b);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (i, i + 1)), pairs);   // the phrase, not the stray "okay"/"right"
    }

    [Fact]
    public void LargeInputsAnchorOnUniqueWords()
    {
        var shared = Enumerable.Range(0, 3000).Select(i => $"w{i}").ToList();
        var b = new List<string>(shared);
        b.InsertRange(1500, Enumerable.Range(0, 400).Select(i => $"extra{i}"));
        var pairs = Aligner.Align(shared, b);
        Assert.Equal(3000, pairs.Count);
        Assert.Equal((1500, 1900), pairs[1500]);
    }

    [Fact]
    public void ProjectsTimesAndSpeakersAndReportsWhatOnlyOneSideHas()
    {
        var vibe = new List<Cue>
        {
            new(0, 4, "So the ratio is four to five.", 100),
            new(5, 8, "Okay that makes sense to me.", 101),
            new(30, 33, "Totally invented words appear right here and nowhere else.", 100),
        };
        var google = GoogleTranscript.Parse("So the ratio is four to five. Okay that makes sense to me.\n\nThen a long passage nobody else transcribed at all today.");
        var result = GoogleCrossCheck.Build(vibe, google, "transcript.vtt", "google.txt", null);
        var cues = Vtt.Parse(result.Vtt);
        Assert.Equal(100, cues[0].Speaker);
        Assert.Contains(cues, c => c.Speaker == 101 && c.Text.StartsWith("Okay"));
        Assert.Contains("Then a long passage", result.Report);
        Assert.Contains("Totally invented words", result.Report);
        Assert.Equal(13, result.Matched);
    }

    [Fact]
    public void ASilenceTokenGoogleNeverHeardIsCalledOut()
    {
        var vibe = new List<Cue> { new(0, 3, "We are going over problem one now.", 0), new(40, 41, "Thank you.") };
        var google = GoogleTranscript.Parse("We are going over problem one now.");
        var report = GoogleCrossCheck.Build(vibe, google, "t.vtt", "g.txt", null).Report;
        Assert.Contains("**Google has no such words here.**", report);
    }
}

public class CliTests
{
    [Fact]
    public void UnknownCommandsAndOptionsFailWithExitCode2()
    {
        Assert.Equal(2, Cli.Main(["nonsense"]));
        Assert.Equal(2, Cli.Main(["normalize", "a", "b", "--bogus", "1"]));
        Assert.Equal(0, Cli.Main(["help"]));
    }

    [Fact]
    public void NormalizeRefusesALoopingTranscriptUnlessAllowed()
    {
        var dir = Directory.CreateTempSubdirectory("holtz-cli-").FullName;
        try
        {
            var vtt = Path.Combine(dir, "t.vtt");
            var cues = Make.Conversation(5).Concat(Enumerable.Range(0, 6).Select(i => Make.Cue(30 + i * 2, "Thank you.")));
            File.WriteAllText(vtt, Vtt.Render(cues));
            var output = Path.Combine(dir, "t.txt");
            Assert.Equal(4, Cli.Main(["normalize", vtt, output, "--speakers", "0=Ana,1=Ben"]));
            Assert.False(File.Exists(output));
            Assert.Equal(0, Cli.Main(["normalize", vtt, output, "--speakers", "0=Ana,1=Ben", "--allow-degraded"]));
            Assert.Contains("[00:00 Ana]", File.ReadAllText(output));
            Assert.Equal(3, Cli.Main(["normalize", vtt, output, "--speakers", "0=Ana", "--allow-degraded", "--strict"]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
