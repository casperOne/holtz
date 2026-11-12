using System.Text.Json.Nodes;

namespace Holtz.Tests;

public class JoinerTests
{
    private static Joiner.Part Part(string name, double from, double to, Dictionary<int, string> mapping, List<Cue> cues,
        List<(Span, string, bool)>? missing = null) => new()
    {
        Name = name, VttPath = name + ".vtt", Sha256 = new string('a', 64), Window = new Span(from, to), WindowFromRecord = true,
        Mapping = mapping, Cues = cues, NotTranscribed = missing ?? [],
    };

    [Fact]
    public void ALaterPartReplacesEverythingThatStartsInsideItsWindow()
    {
        var full = Part("full", 0, 100, new() { [0] = "Ana", [1] = "Ben" },
            [Make.Cue(0, "full a", 0), Make.Cue(39, "starts before, runs in", 1, 4), Make.Cue(50, "full b", 1), Make.Cue(80, "full c", 0)]);
        var recut = Part("recut", 40, 60, new() { [0] = "Cy", [1] = "Ana" },
            [Make.Cue(40, "recut a", 0), Make.Cue(52, "recut b", 1)]);
        var result = Joiner.Merge([full, recut]);

        Assert.Equal(["full a", "starts before, runs in", "recut a", "recut b", "full c"], result.Cues.Select(c => c.Text));
        // Ids become names per part: id 0 is Ana in one run and Cy in the other.
        Assert.Equal(["Ana", "Ben", "Cy", "Ana", "Ana"], result.Cues.Select(c => c.Voice));
        Assert.All(result.Cues, c => Assert.Null(c.Speaker));
        Assert.Equal([(new Span(0, 40), 0), (new Span(40, 60), 1), (new Span(60, 100), 0)], result.Coverage);
    }

    [Fact]
    public void OnlyGapsNoLaterPartFillsAreReported()
    {
        var full = Part("full", 0, 100, new() { [0] = "Ana" }, [Make.Cue(0, "a", 0)],
            [(new Span(30, 70), "clip excluded by the quality gate", true), (new Span(80, 90), "silence, not transcribed", false)]);
        var recut = Part("recut", 30, 60, new() { [0] = "Ana" }, [Make.Cue(31, "b", 0)],
            [(new Span(29.65, 30.2), "silence, not transcribed", false)]);   // the pre-roll edge: under a second
        var result = Joiner.Merge([full, recut]);
        Assert.Equal(2, result.Missing.Count);
        Assert.Equal((new Span(60, 70), "clip excluded by the quality gate", true, 0), result.Missing[0]);
        Assert.Equal((new Span(80, 90), "silence, not transcribed", false, 0), result.Missing[1]);
    }

    [Fact]
    public void UnmappedIdsStayVisibleAndSayWhichPartTheyCameFrom()
    {
        var a = Part("a", 0, 10, new() { [0] = "Ana" }, [Make.Cue(0, "one", 0), Make.Cue(3, "two", 3)]);
        var result = Joiner.Merge([a]);
        Assert.Equal([(0, 3)], result.Unmapped);
        var text = Joiner.Render(result, [a], "T", ["a note"], Normalizer.DefaultTurnGap);
        Assert.Contains("[00:00 Ana] one", text);
        Assert.Contains("[00:03 Speaker 3] two", text);
        Assert.Contains("(part 1 id 3)", text);
        Assert.Contains("# Part 1: a (sha256 aaaaaaaaaaaa), window 00:00-00:10; speakers 0=Ana", text);
        Assert.Contains("# a note", text);
    }

    [Fact]
    public void PartsAreNamedByWhereTheirFoldersDiffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec_transcription");
        Assert.Equal(root, Joiner.CommonFolder([root, Path.Combine(root, "recut_01"), Path.Combine(root, "recut_02") + Path.DirectorySeparatorChar]));
        Assert.Equal(root, Joiner.CommonFolder([Path.Combine(root, "a"), Path.Combine(root, "b")]));
    }

    private static string RunFolder(string root, string name, double? from, double? to, double duration, IEnumerable<Cue> cues,
        JsonArray? clips = null, JsonArray? skipped = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "transcript.vtt"), Vtt.Render(cues));
        var assembly = new JsonObject
        {
            ["plan"] = new JsonObject
            {
                ["duration"] = duration,
                ["settings"] = new JsonObject { ["from"] = from, ["to"] = to },
                ["skipped_silences"] = skipped ?? [],
                ["dropped_too_quiet"] = new JsonArray(),
            },
            ["clips"] = clips ?? [],
            ["allow_degraded"] = false,
        };
        File.WriteAllText(Path.Combine(dir, "transcript-assembly.json"), assembly.ToJsonString());
        return dir;
    }

    [Fact]
    public void TheJoinCommandRepairsAnExcludedClipWithAReRunAndSaysWhatIsStillMissing()
    {
        var root = Directory.CreateTempSubdirectory("holtz-join-").FullName;
        try
        {
            var excludedClip = new JsonArray(
                new JsonObject { ["start"] = 0.0, ["stop"] = 30.0, ["gate_warnings"] = new JsonArray(), ["error"] = null, ["rejected_stretches"] = new JsonArray() },
                new JsonObject { ["start"] = 32.0, ["stop"] = 90.0, ["gate_warnings"] = new JsonArray("loop"), ["error"] = null, ["rejected_stretches"] = new JsonArray() });
            var full = RunFolder(root, "full", null, null, 90, Make.Conversation(10, 0, 3), excludedClip,
                new JsonArray(new JsonObject { ["start"] = 30.0, ["stop"] = 32.0 }));
            var recut = RunFolder(root, "recut_00-32_to_01-00", 32, 60, 90, Make.Conversation(9, 32, 3).Select(c => c with { Speaker = c.Speaker + 10 }));
            var output = Path.Combine(root, "joined.txt");

            // Ids 0/1 in the full run, 10/11 in the re-cut, each named by its own map.
            Assert.Equal(4, Cli.Main(["join", output, "--part", full, "--speakers", "0=Ana,1=Ben", "--part", recut, "--speakers", "10=Cy,11=Ana"]));
            var text = File.ReadAllText(output);
            Assert.Contains("[00:00 Ana] Line 0", text);
            Assert.Contains("[00:32 Cy] Line 0", text);
            Assert.Contains("# Covers: 00:00-00:32 part 1; 00:32-01:00 part 2; 01:00-01:30 part 1", text);
            Assert.Contains("# Not transcribed: 00:30-00:32 (2s), part 1: silence, not transcribed", text);
            Assert.Contains("# NOT TRANSCRIBED: 01:00-01:30 (30s), part 1: clip excluded by the quality gate", text);

            // Cover the rest and the join is clean.
            var tail = RunFolder(root, "recut_01-00_to_01-30", 60, 90, 90, Make.Conversation(9, 60, 3));
            Assert.Equal(0, Cli.Main(["join", output, "--part", full, "--speakers", "0=Ana,1=Ben", "--part", recut,
                "--speakers", "10=Cy,11=Ana", "--part", tail, "--speakers", "0=Ana,1=Ben"]));
            Assert.DoesNotContain("NOT TRANSCRIBED", File.ReadAllText(output));

            Assert.Equal(2, Cli.Main(["join", output, "--part", full, "--part", recut, "--speakers", "0=Ana"]));
            Assert.Equal(3, Cli.Main(["join", output, "--part", full, "--speakers", "0=Ana", "--strict"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheJoinCommandRefusesALoopingPartUnlessAllowed()
    {
        var root = Directory.CreateTempSubdirectory("holtz-join-").FullName;
        try
        {
            var looping = RunFolder(root, "loop", null, null, 60,
                Make.Conversation(5).Concat(Enumerable.Range(0, 6).Select(i => Make.Cue(30 + i * 2, "Thank you."))));
            var output = Path.Combine(root, "joined.txt");
            Assert.Equal(4, Cli.Main(["join", output, "--part", looping, "--speakers", "0=Ana,1=Ben"]));
            Assert.False(File.Exists(output));
            Assert.Equal(0, Cli.Main(["join", output, "--part", looping, "--speakers", "0=Ana,1=Ben", "--allow-degraded"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
