using System.Globalization;
using System.Text.Json;

namespace Holtz;

/// <summary>The command line. Hand-rolled on purpose: no packages, so the first run needs nothing
/// but the .NET SDK.</summary>
public static class Cli
{
    private const string Usage = """
        holtz — long recordings to transcripts with a local Vibe engine, cut so whisper never starts on silence.

        Usage:
          holtz AUDIO [options]                    same as `run AUDIO`
          holtz probe     [server options]         is this machine ready? (ffmpeg, vibe-server, models)
          holtz profile   AUDIO [--window S]       the loudness map, as runs (never an average)
          holtz plan      AUDIO [--json]           the cut plan, without transcribing
          holtz run       AUDIO [options]          measure, cut, transcribe, gate, assemble
          holtz normalize VTT OUT.txt --speakers "1=Ana,2=Ben,101=Ben" [--fill transcript_google.vtt]
          holtz google    GOOGLE.txt --vtt transcript.vtt [--out DIR]
          holtz worksheet VTT [--out speaker-worksheet.md]

        run options:
          --out DIR             where to write (default: <audio name>_transcription beside the audio)
          --google FILE         optional: a Google Recorder transcript of the same conversation, to cross-check
          --people "TEXT"       optional, repeatable: who is in the recording ("Ana: interviewer, asks questions")
          --from S --to S       transcribe only this window (seconds); times stay on the recording's timeline
          --language CODE       e.g. en
          --prompt TEXT         initial prompt for whisper (names, jargon)
          --param KEY=VALUE     any other field vibe-server reads; repeatable
          --no-diarize          do not ask for speaker labels (every line renders [?])
          --diarize-model PATH  default: found beside Vibe's models
          --vad-model PATH      pass a VAD model (required by parakeet/nemotron models; found automatically for them)
          --allow-degraded      include clips the quality gate rejected
          --max-retries N       automatic re-cuts per clip after a gate hit (default 2)
          --keep-clip-audio     keep the cut WAVs
          --force               overwrite an existing transcript.vtt

        server options (run, probe):
          --model PATH          whisper model (default: Vibe's configured model.path)
          --vibe-server PATH    the engine (default: found beside vibe.exe, e.g. %LOCALAPPDATA%\vibe\vibe-server.exe)
          --server-url URL      attach to a running server instead of starting one (it is left running)
          --ready-timeout S     seconds to wait for a started server (default 300)

        detection options (profile, plan, run):
          --noise-db DB (-40)  --min-silence S (20)  --min-peak-db DB (-35)  --bridge-peak-db DB (-38)
          --pre-roll S (0.35)  --tail S (0.75)

        normalize options:
          --title TEXT  --note TEXT (repeatable)  --fill VTT  --fill-min-gap S (10)  --strict  --allow-degraded

        Exit codes: 0 ok, 1 not ready (probe), 2 error, 3 unmapped speakers (--strict), 4 transcript degraded.
        """;

    private static readonly HashSet<string> Commands = ["probe", "profile", "plan", "run", "normalize", "google", "worksheet", "help"];

    private static readonly HashSet<string> Flags =
    [
        "--json", "--no-diarize", "--allow-degraded", "--keep-clip-audio", "--force", "--strict", "--help", "-h",
    ];

    private static readonly HashSet<string> Repeatable = ["--param", "--people", "--note"];

    private sealed class Args
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, List<string>> Options { get; } = [];
        public HashSet<string> Set { get; } = [];

        public string? Get(string name) => Options.TryGetValue(name, out var v) ? v[^1] : null;
        public List<string> All(string name) => Options.TryGetValue(name, out var v) ? v : [];
        public bool Has(string name) => Set.Contains(name);

        public double Number(string name, double fallback)
        {
            var raw = Get(name);
            if (raw is null) return fallback;
            return double.TryParse(raw, NumberStyles.Float, Fmt.Inv, out var value) ? value
                : throw new TranscribeException($"{name} expects a number, got '{raw}'.");
        }

        public double? OptionalNumber(string name) => Get(name) is null ? null : Number(name, 0);
    }

    private static Args Parse(IEnumerable<string> argv)
    {
        var args = new Args();
        using var e = argv.GetEnumerator();
        while (e.MoveNext())
        {
            var token = e.Current;
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Contains('='))
            {
                var eq = token.IndexOf('=');
                Add(args, token[..eq], token[(eq + 1)..]);
            }
            else if (Flags.Contains(token)) args.Set.Add(token);
            else if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (!e.MoveNext()) throw new TranscribeException($"{token} needs a value.");
                Add(args, token, e.Current);
            }
            else args.Positional.Add(token);
        }
        return args;
    }

    private static void Add(Args args, string name, string value)
    {
        if (!args.Options.TryGetValue(name, out var list)) args.Options[name] = list = [];
        if (list.Count > 0 && !Repeatable.Contains(name)) list.Clear();
        list.Add(value);
    }

    private static void Allow(Args args, params string[] names)
    {
        var allowed = names.ToHashSet();
        foreach (var name in args.Options.Keys.Concat(args.Set))
            if (!allowed.Contains(name) && name is not ("--help" or "-h"))
                throw new TranscribeException($"Unknown option {name} for this command. Run `holtz help`.");
    }

    private static readonly string[] Detection = ["--noise-db", "--min-silence", "--min-peak-db", "--bridge-peak-db"];
    private static readonly string[] Server = ["--model", "--vibe-server", "--server-url", "--ready-timeout"];

    public static int Main(string[] argv)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var list = argv.ToList();
            if (list.Count == 0 || list[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine(Usage);
                return list.Count == 0 ? 2 : 0;
            }
            // Pointing at a recording is the whole common path, so it should not also need a verb.
            if (!Commands.Contains(list[0]) && !list[0].StartsWith('-') && File.Exists(list[0])) list.Insert(0, "run");
            var command = list[0];
            if (!Commands.Contains(command)) throw new TranscribeException($"Unknown command '{command}'. Run `holtz help`.");
            var args = Parse(list.Skip(1));
            if (args.Has("--help") || args.Has("-h")) { Console.WriteLine(Usage); return 0; }
            return command switch
            {
                "probe" => Probe(args),
                "profile" => Profile(args),
                "plan" => PlanCommand(args),
                "run" => RunCommand(args),
                "normalize" => Normalize(args),
                "google" => GoogleCommand(args),
                "worksheet" => Worksheet(args),
                _ => 2,
            };
        }
        catch (TranscribeException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static string RequireAudio(Args args)
    {
        if (args.Positional.Count != 1) throw new TranscribeException("Give exactly one audio file.");
        var audio = args.Positional[0];
        return File.Exists(audio) ? audio : throw new TranscribeException($"No such audio file: {audio}");
    }

    private static PlanSettings Settings(Args args)
    {
        var defaults = new PlanSettings();
        var settings = new PlanSettings
        {
            NoiseDb = args.Number("--noise-db", defaults.NoiseDb),
            MinSilence = args.Number("--min-silence", defaults.MinSilence),
            MinPeakDb = args.Number("--min-peak-db", defaults.MinPeakDb),
            BridgePeakDb = args.Number("--bridge-peak-db", defaults.BridgePeakDb),
            PreRoll = args.Number("--pre-roll", defaults.PreRoll),
            Tail = args.Number("--tail", defaults.Tail),
            From = args.OptionalNumber("--from"),
            To = args.OptionalNumber("--to"),
        };
        if (settings.From is { } from && settings.To is { } to && to <= from)
            throw new TranscribeException("--to must be after --from.");
        return settings;
    }

    private static int Profile(Args args)
    {
        Allow(args, [.. Detection, "--window"]);
        var audio = RequireAudio(args);
        var settings = Settings(args);
        var window = args.Number("--window", PlanSettings.DefaultProfileWindow);
        var duration = Audio.ProbeDuration(audio);
        Console.WriteLine(Planner.RenderProfile(Audio.LoudnessProfile(audio, window), window, settings.NoiseDb));
        Console.WriteLine();
        var (silences, _) = Audio.DetectSilences(audio, duration, settings.NoiseDb, settings.MinSilence);
        if (silences.Count == 0) Console.WriteLine($"No silence run reaches {Fmt.G(settings.MinSilence)}s.");
        else
        {
            Console.WriteLine($"Silence runs >= {Fmt.G(settings.MinSilence)}s (the coarse cut points):");
            foreach (var s in silences) Console.WriteLine($"  {Fmt.Clock(s.Start)}-{Fmt.Clock(s.Stop)}  ({Fmt.F(s.Duration, 1)} s)");
        }
        return 0;
    }

    private static int PlanCommand(Args args)
    {
        Allow(args, [.. Detection, "--json", "--pre-roll", "--tail", "--from", "--to"]);
        var audio = RequireAudio(args);
        // The same measurement `run` makes, so the preview cannot disagree with the run.
        var plan = Pipeline.MeasureAndPlan(audio, Settings(args));
        Console.WriteLine(args.Has("--json")
            ? plan.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            : Planner.Render(plan));
        return 0;
    }

    /// <summary>The model to load: --model, else the one Vibe itself is configured with.</summary>
    private static string? ResolveModel(Args args)
    {
        var model = args.Get("--model") ?? VibeInstall.ReadConfig("model.path");
        if (model is not null && !File.Exists(model))
            throw new TranscribeException($"Model not found: {model}" + (args.Get("--model") is null ? " (from Vibe's config model.path)" : ""));
        return model;
    }

    /// <summary>Attach to --server-url / VIBE_SERVER_URL, or start our own vibe-server.</summary>
    private static VibeServer OpenServer(Args args, string? model)
    {
        var url = args.Get("--server-url") ?? Environment.GetEnvironmentVariable("VIBE_SERVER_URL");
        if (url is not null)
        {
            var server = VibeServer.Attach(url, args.Get("--server-url") is null ? "VIBE_SERVER_URL" : "--server-url");
            server.CheckReachable();
            Console.WriteLine($"server  attached to {server.Url} (from {server.Source}; it will be left running)");
            return server;
        }
        var binary = VibeInstall.FindServer(args.Get("--vibe-server")) ?? throw new TranscribeException(
            "vibe-server was not found. Install Vibe (https://thewh1teagle.github.io/vibe/), or pass --vibe-server "
            + "PATH (Windows: %LOCALAPPDATA%\\vibe\\vibe-server.exe; older installs call it sona.exe), or set VIBE_SERVER_PATH. "
            + "To use a server that is already running, pass --server-url.");
        Console.WriteLine($"server  starting {binary}" + (model is null ? "" : $" with {Path.GetFileName(model)}") + " ...");
        var started = VibeServer.Start(binary, model, TimeSpan.FromSeconds(args.Number("--ready-timeout", 300)));
        Console.WriteLine($"server  {started.Url} (vibe-server {started.Version ?? "?"}; stopped when this run ends)");
        return started;
    }

    private static int Probe(Args args)
    {
        Allow(args, Server);
        var ok = true;
        foreach (var tool in new[] { "ffmpeg", "ffprobe" })
        {
            var found = Audio.Find(tool);
            Console.WriteLine($"{tool,-9} {(found is null ? "MISSING (winget install Gyan.FFmpeg)" : "found: " + found)}");
            ok &= found is not null;
        }
        var config = VibeInstall.ConfigPath();
        Console.WriteLine($"config    {config ?? "no Vibe config found (looked in: " + string.Join(", ", VibeInstall.ConfigCandidates()) + ")"}");
        string? model = null;
        try { model = ResolveModel(args); }
        catch (TranscribeException ex) { Console.WriteLine($"model     {ex.Message}"); ok = false; }
        if (model is not null) Console.WriteLine($"model     {model}");
        else if (ok) { Console.WriteLine("model     none configured: pass --model PATH (a whisper .bin/.gguf)"); ok = false; }
        var folders = VibeInstall.ModelFolders(model);
        Console.WriteLine($"models    {(folders.Count > 0 ? string.Join(", ", folders) : VibeInstall.ModelsFolder() + " (missing)")}");
        var diarize = VibeInstall.FindModelFile(folders, VibeInstall.DiarizePatterns);
        Console.WriteLine(diarize is null
            ? "diarize   NO diarization model found: transcripts will have no speakers. In Vibe, turn on speaker recognition once so it downloads the model."
            : $"diarize   {diarize}");
        var vad = VibeInstall.FindModelFile(folders, VibeInstall.VadPatterns);
        Console.WriteLine($"vad       {vad ?? "(none found; only needed for parakeet/nemotron models)"}");

        string? binary = null;
        if (args.Get("--server-url") is null && Environment.GetEnvironmentVariable("VIBE_SERVER_URL") is null)
        {
            try { binary = VibeInstall.FindServer(args.Get("--vibe-server")); }
            catch (TranscribeException ex) { Console.WriteLine($"engine    {ex.Message}"); }
            Console.WriteLine($"engine    {binary ?? "vibe-server NOT FOUND (pass --vibe-server PATH)"}");
            if (binary is null) return 1;
        }
        if (!ok) return 1;

        try
        {
            using var server = OpenServer(args, model);
            var (ready, detail) = server.Owned ? VibeServer.ParseReadiness(server.GetText(VibeServer.ReadyPath)) : server.EnsureModel(model);
            Console.WriteLine($"ready     {(ready ? "yes" : "NO — " + detail)}");
            var models = Segments.TryParse(server.GetText("/v1/models"));
            if (models is not null) Console.WriteLine($"loaded    {models.ToJsonString()}");
            if (server.Owned) Console.WriteLine("stopping  the server this probe started");
            return ready ? 0 : 1;
        }
        catch (TranscribeException ex)
        {
            Console.WriteLine($"server    {ex.Message}");
            return 1;
        }
    }

    private static string DefaultOutDir(string audio) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(audio))!, Path.GetFileNameWithoutExtension(audio) + "_transcription");

    private static int RunCommand(Args args)
    {
        Allow(args, [.. Detection, .. Server, "--out", "--google", "--people", "--from", "--to", "--language", "--prompt",
            "--param", "--no-diarize", "--diarize-model", "--vad-model", "--allow-degraded", "--max-retries",
            "--keep-clip-audio", "--force", "--pre-roll", "--tail"]);
        var audio = RequireAudio(args);
        var settings = Settings(args);
        var google = args.Get("--google");
        if (google is not null && !File.Exists(google)) throw new TranscribeException($"No such Google transcript: {google}");

        var outDir = args.Get("--out") ?? DefaultOutDir(audio);
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"output  {outDir}");
        var existing = Path.Combine(outDir, "transcript.vtt");
        if (File.Exists(existing) && !args.Has("--force"))
            throw new TranscribeException($"{existing} already exists. Two transcripts of one recording are two pieces of "
                + "evidence, not a correction: pass --out to write this run somewhere else, or --force to replace it.");

        Audio.Require("ffmpeg");
        Audio.Require("ffprobe");
        var model = ResolveModel(args);
        var folders = VibeInstall.ModelFolders(model);
        var fields = new Dictionary<string, string>();
        if (!args.Has("--no-diarize"))
        {
            var diarize = args.Get("--diarize-model") ?? VibeInstall.FindModelFile(folders, VibeInstall.DiarizePatterns);
            if (diarize is not null) fields["diarize_model"] = diarize;
            else Console.Error.WriteLine("warning: no diarization model found beside Vibe's models, so the transcript will have NO "
                + "speaker labels and every line will render [?]. Pass --diarize-model PATH, or --no-diarize to accept that.");
        }
        var vad = args.Get("--vad-model");
        if (vad is null && model is not null && (model.Contains("parakeet", StringComparison.OrdinalIgnoreCase) || model.Contains("nemotron", StringComparison.OrdinalIgnoreCase)))
            vad = VibeInstall.FindModelFile(folders, VibeInstall.VadPatterns);
        if (vad is not null) fields["vad_model"] = vad;
        if (args.Get("--language") is { } language) fields["language"] = language;
        if (args.Get("--prompt") is { } prompt) fields["prompt"] = prompt;
        foreach (var pair in args.All("--param"))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw new TranscribeException($"Bad --param '{pair}'; expected key=value.");
            fields[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        using var server = OpenServer(args, model);
        var (ready, detail) = server.EnsureModel(model);
        if (!ready) throw new TranscribeException($"The Vibe server at {server.Url} is not ready: {detail}");
        return Pipeline.Run(server, new RunOptions
        {
            Audio = audio,
            OutDir = outDir,
            Settings = settings,
            Fields = fields,
            AllowDegraded = args.Has("--allow-degraded"),
            KeepClipAudio = args.Has("--keep-clip-audio"),
            MaxRetries = (int)args.Number("--max-retries", Pipeline.DefaultMaxRetries),
            People = args.All("--people"),
            ModelPath = model,
            GooglePath = google,
        });
    }

    private static int Normalize(Args args)
    {
        Allow(args, "--speakers", "--title", "--note", "--fill", "--fill-min-gap", "--strict", "--allow-degraded");
        if (args.Positional.Count != 2) throw new TranscribeException("normalize takes INPUT (VTT or JSON) and OUTPUT (.txt).");
        var (input, output) = (args.Positional[0], args.Positional[1]);
        if (!File.Exists(input)) throw new TranscribeException($"No such transcript: {input}");
        var mapping = Normalizer.ParseSpeakerMap(args.Get("--speakers") ?? throw new TranscribeException("--speakers is required (e.g. \"1=Ana,2=Ben\")."));
        var raw = File.ReadAllText(input);
        var cues = Vtt.Load(input);

        // The gate looks at Vibe's transcript alone: fill text is a second source, not a repair.
        var quality = QualityGate.Assess(cues);
        foreach (var warning in quality) Console.Error.WriteLine($"warning: {warning}");
        if (quality.Count > 0 && !args.Has("--allow-degraded"))
        {
            Console.Error.WriteLine("error: refusing to write a transcript that looks like a transcription failure. Re-transcribe "
                + "that window, or pass --allow-degraded to keep it anyway (and say so wherever it is used).");
            return 4;
        }

        var all = cues;
        HashSet<Cue>? filled = null;
        string? fillName = null;
        if (args.Get("--fill") is { } fillPath)
        {
            if (!File.Exists(fillPath)) throw new TranscribeException($"No such fill transcript: {fillPath}");
            var fill = Normalizer.FillCues(cues, Vtt.Load(fillPath), args.Number("--fill-min-gap", 10));
            filled = new HashSet<Cue>(fill, ReferenceEqualityComparer.Instance);
            all = [.. cues, .. fill];
            fillName = Path.GetFileName(fillPath);
        }

        var notes = Vtt.SuspectNotes(raw).Concat(args.All("--note")).ToList();
        var (text, unmapped) = Normalizer.Render(all, mapping, new Normalizer.Options
        {
            Title = args.Get("--title"),
            Notes = notes,
            SourceName = Path.GetFileName(input),
            SourceSha256 = Fmt.FileSha256(input),
            FillSourceName = fillName,
            FilledCount = filled?.Count ?? 0,
        }, filled);
        if (unmapped.Count > 0)
        {
            Console.Error.WriteLine($"warning: unmapped speaker id(s) {string.Join(", ", unmapped)} rendered as [Speaker N]; "
                + "add them to --speakers or resolve them before relying on the transcript.");
            if (args.Has("--strict")) return 3;
        }
        Fmt.WriteText(output, text);
        Console.WriteLine($"wrote {output} ({text.Count(c => c == '\n')} lines"
            + (filled is { Count: > 0 } ? $", {filled.Count} passage(s) filled from {fillName}" : "") + ").");
        return 0;
    }

    private static int GoogleCommand(Args args)
    {
        Allow(args, "--vtt", "--out", "--assembly");
        if (args.Positional.Count != 1) throw new TranscribeException("google takes one Google transcript file.");
        var vtt = args.Get("--vtt") ?? throw new TranscribeException("--vtt transcript.vtt is required.");
        if (!File.Exists(vtt)) throw new TranscribeException($"No such transcript: {vtt}");
        var outDir = args.Get("--out") ?? Path.GetDirectoryName(Path.GetFullPath(vtt))!;
        Directory.CreateDirectory(outDir);
        GoogleCrossCheck.RunInto(args.Positional[0], vtt, outDir, args.Get("--assembly"));
        return 0;
    }

    private static int Worksheet(Args args)
    {
        Allow(args, "--out", "--people");
        if (args.Positional.Count != 1) throw new TranscribeException("worksheet takes one transcript (VTT or JSON).");
        var cues = Vtt.Load(args.Positional[0]);
        var text = Pipeline.SpeakerWorksheet(Pipeline.GroupByNamespace(cues), args.All("--people"));
        if (args.Get("--out") is { } outPath) { Fmt.WriteText(outPath, text); Console.WriteLine($"wrote {outPath}"); }
        else Console.WriteLine(text);
        return 0;
    }
}
