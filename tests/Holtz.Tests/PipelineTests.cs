using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Holtz.Tests;

public class PipelineUnitTests
{
    [Fact]
    public void CueEndingPastTheAudioIsDroppedWholeNotClamped()
    {
        var (kept, dropped) = Pipeline.DropCuesPastAudio([Make.Cue(0, "Real."), new Cue(239.4, 269.4, "Thank you.")], 241.3);
        Assert.Single(kept);
        Assert.Equal("Thank you.", Assert.Single(dropped).Text);
        Assert.Single(Pipeline.DropCuesPastAudio([new Cue(240, 241.7, "Resampling.")], 241.3).Kept);
    }

    [Fact]
    public void OnlyAnUnlabeledTrailingSilenceTokenIsFlagged()
    {
        Assert.Single(Pipeline.FlagTrailingFabrication([Make.Cue(0, "Hi.", 0), Make.Cue(10, "Thank you.")]));
        Assert.Empty(Pipeline.FlagTrailingFabrication([Make.Cue(0, "Hi.", 0), Make.Cue(10, "Thank you.", 1)]));
        Assert.Empty(Pipeline.FlagTrailingFabrication([Make.Cue(0, "Thank you."), Make.Cue(10, "Bye.", 1)]));
    }

    [Fact]
    public void ShiftMovesTimeAndNamespacesIdsButNotNames()
    {
        var shifted = Pipeline.Shift([Make.Cue(1, "a", 2), new Cue(2, 3, "b", null, "Ana")], 100, 300);
        Assert.Equal((101.0, 302), (shifted[0].Start, shifted[0].Speaker!.Value));
        Assert.Equal("Ana", shifted[1].Voice);
        Assert.Null(shifted[1].Speaker);
    }

    [Fact]
    public void AssembleLeavesOutGatedClipsUnlessAskedAndSaysSo()
    {
        var plan = Planner.Build(100, [new Span(30, 60)], new PlanSettings());
        var good = new ClipResult(plan.Clips[0]) { Cues = [Make.Cue(1, "Good line here.", 0)] };
        var bad = new ClipResult(plan.Clips[1]) { Cues = [Make.Cue(61, "Looping line.", 100)], Warnings = ["it looped"] };
        var (vtt, excluded) = Pipeline.Assemble([good, bad], "a.wav", plan, allowDegraded: false);
        Assert.Single(excluded);
        Assert.DoesNotContain("Looping line.", Vtt.Parse(vtt).Select(c => c.Text));
        Assert.Contains("Clips excluded by the quality gate", vtt);
        var (degraded, none) = Pipeline.Assemble([good, bad], "a.wav", plan, allowDegraded: true);
        Assert.Empty(none);
        Assert.Contains("Looping line.", Vtt.Parse(degraded).Select(c => c.Text));
    }

    [Fact]
    public void WorksheetQuotesFromAcrossTheClipWithStats()
    {
        var cues = Enumerable.Range(0, 20).Select(i => Make.Cue(i * 3, i == 19 ? "Last one?" : $"Line {i}.", 0)).ToList();
        var sheet = Pipeline.SpeakerWorksheet([("Clip 1", cues)], ["Ana: asks"]);
        Assert.Contains("- Ana: asks", sheet);
        Assert.Contains("20 cues", sheet);
        Assert.Contains("1 question(s)", sheet);
        Assert.Contains("\"Line 0.\"", sheet);
        Assert.Contains("\"Last one?\"", sheet);
    }

    [Fact]
    public void ReadinessIsReadGenerously()
    {
        Assert.False(VibeServer.ParseReadiness("""{"status":"not_ready","message":"No model loaded"}""").Ready);
        Assert.True(VibeServer.ParseReadiness("""{"status":"ready"}""").Ready);
        Assert.True(VibeServer.ParseReadiness("OK").Ready);
        Assert.False(VibeServer.ParseReadiness(null).Ready);
    }

    [Fact]
    public void LoadedModelsAreSummarizedNotDumped()
    {
        var payload = JsonNode.Parse("""
            {"object":"list","data":[{"id":"ggml-large-v3.bin","object":"model","owned_by":"local",
              "capabilities":{"engine":"whisper","languages":["en","zh","de"],"streaming":true}}]}
            """);
        Assert.Equal("ggml-large-v3.bin (whisper)", VibeServer.DescribeModels(payload));
        Assert.Equal("none", VibeServer.DescribeModels(JsonNode.Parse("""{"object":"list","data":[]}""")));
        Assert.Null(VibeServer.DescribeModels(null));
    }

    [Fact]
    public async Task MultipartNamesAreQuoted()
    {
        var clip = Path.GetTempFileName();
        try
        {
            using var form = VibeServer.BuildForm(clip, new Dictionary<string, string> { ["diarize_model"] = @"C:\models\diar.gguf" });
            var body = await form.ReadAsStringAsync();
            Assert.Contains("name=\"response_format\"", body);
            Assert.Contains("name=\"diarize_model\"", body);
            Assert.Contains($"name=\"file\"; filename=\"{Path.GetFileName(clip)}\"", body);
            Assert.DoesNotContain("filename*", body);
            Assert.Contains(@"C:\models\diar.gguf", body);
        }
        finally { File.Delete(clip); }
    }
}

/// <summary>The whole pass against an in-process stand-in for vibe-server, on synthetic audio.
/// Needs ffmpeg on PATH; without it there is nothing to cut, and the test returns early.</summary>
public sealed class PipelineEndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("holtz-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Segments as a function of clip length: a loop 40 s into any long clip (so the gate
    /// re-cuts it), plus a cue running past the audio and a trailing unlabeled "Thank you.".</summary>
    private static JsonObject Respond(double duration, bool diarize)
    {
        var segments = new JsonArray();
        var t = 0.0;
        for (var k = 1; t + 2.5 <= duration && !(duration > 100 && t >= 40); k++, t += 3)
            segments.Add(new JsonObject { ["start"] = t, ["end"] = t + 2.4, ["text"] = $" Line {k} mentions item{(int)duration}x{k} plainly.", ["speaker"] = k % 2 });
        if (duration > 100)
            for (var i = 0; i < 20; i++)
                segments.Add(new JsonObject { ["start"] = 40 + i * 2.0, ["end"] = 41.5 + i * 2.0, ["text"] = " So this is the total.", ["speaker"] = 0 });
        segments.Add(new JsonObject { ["start"] = duration - 1, ["end"] = duration + 29, ["text"] = " Thank you." });
        segments.Add(new JsonObject { ["start"] = duration - 0.6, ["end"] = duration - 0.1, ["text"] = " Thank you." });
        if (!diarize) foreach (var s in segments) s!.AsObject().Remove("speaker");
        return new JsonObject { ["text"] = "", ["segments"] = segments };
    }

    private static double WavSeconds(byte[] body)
    {
        var riff = body.AsSpan().IndexOf("RIFF"u8);
        var data = body.AsSpan(riff).IndexOf("data"u8) + riff;
        var size = BitConverter.ToInt32(body, data + 4);
        return size / (16000.0 * 2);
    }

    private static void Serve(HttpListener listener, List<string> log)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (Exception) { return; }
            var path = context.Request.Url!.AbsolutePath;
            byte[] body;
            using (var ms = new MemoryStream()) { context.Request.InputStream.CopyTo(ms); body = ms.ToArray(); }
            JsonNode reply = path switch
            {
                "/ready" or "/health" => new JsonObject { ["status"] = "ready" },
                VibeServer.TranscribePath => Respond(WavSeconds(body), Encoding.UTF8.GetString(body).Contains("name=\"diarize_model\"")),
                _ => new JsonObject { ["error"] = "nope" },
            };
            lock (log) log.Add(path);
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
    }

    [Fact]
    public void CutsTranscribesRecoversAssemblesAndCrossChecks()
    {
        if (Audio.Find("ffmpeg") is null || Audio.Find("ffprobe") is null) return;
        var audio = Path.Combine(_dir, "talk.wav");
        var (code, _, err) = Audio.RunTool(Audio.Find("ffmpeg")!, ["-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i",
            "aevalsrc='0.125*sin(2*PI*440*t)*(between(t,0,30)+between(t,55,200))':d=230:s=16000", "-ac", "1", audio]);
        Assert.True(code == 0, err);

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var log = new List<string>();
        var thread = new Thread(() => Serve(listener, log)) { IsBackground = true };
        thread.Start();

        // A Google transcript of the same talk: the first clip's words, plus a passage only Google heard.
        var google = Path.Combine(_dir, "google.txt");
        File.WriteAllText(google, string.Join(' ', Enumerable.Range(1, 10).Select(k => $"Line {k} mentions item30x{k} plainly."))
            + "\n\nAnd here is a passage that only the phone picked up while the other microphone missed it entirely.\n");

        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        using var server = VibeServer.Attach($"http://127.0.0.1:{port}", "test");
        var exit = Pipeline.Run(server, new RunOptions
        {
            Audio = audio,
            OutDir = outDir,
            Fields = new() { ["diarize_model"] = "diar.gguf" },
            GooglePath = google,
        });
        listener.Stop();

        Assert.Equal(0, exit);
        var cues = Vtt.Parse(File.ReadAllText(Path.Combine(outDir, "transcript.vtt")));
        Assert.Contains(cues, c => c.Speaker is >= 100 and < 110);       // clip 2, first attempt
        Assert.Contains(cues, c => c.Speaker is >= 110 and < 130);       // clip 2, a re-cut's own band
        Assert.DoesNotContain(cues, c => c.Stop > 231);                   // the over-running cue is gone
        Assert.True(cues.Count(c => c.Text == "So this is the total.") < QualityGate.MinHallucinationRun);
        var clips = Directory.GetFiles(Path.Combine(outDir, "clips")).Select(Path.GetFileName).ToList();
        Assert.Contains(clips, f => f!.Contains("_retry_from_"));
        Assert.DoesNotContain(clips, f => f!.EndsWith(".wav"));

        var assembly = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "transcript-assembly.json")))!;
        Assert.Equal(64, assembly["source_audio"]!["sha256"]!.GetValue<string>().Length);
        Assert.Equal("verbose_json", assembly["request_fields"]!["response_format"]!.GetValue<string>());
        Assert.True(assembly["clips"]![1]!["attempts"]!.AsArray().Count > 1);
        Assert.Contains("SUSPECT CUES", File.ReadAllText(Path.Combine(outDir, "transcript.vtt")));

        Assert.True(File.Exists(Path.Combine(outDir, "google.txt")));
        var report = File.ReadAllText(Path.Combine(outDir, "cross-check.md"));
        Assert.Contains("only the phone picked up", report);
        var projected = Vtt.Parse(File.ReadAllText(Path.Combine(outDir, "transcript_google.vtt")));
        Assert.Contains(projected, c => c.Speaker is 0 or 1);
    }
}
