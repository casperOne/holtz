using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Holtz;

/// <summary>Where Vibe keeps things on this machine. Vibe is a Tauri app whose identifier is
/// <c>github.com.thewh1teagle.vibe</c>: the config sits in the roaming app-data folder, the models
/// in the local one, and the engine (<c>vibe-server</c>, called <c>sona</c> before Vibe 3.1.11)
/// beside <c>vibe.exe</c>.</summary>
public static class VibeInstall
{
    public const string AppId = "github.com.thewh1teagle.vibe";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string? ConfigPath()
    {
        foreach (var candidate in ConfigCandidates())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static IEnumerable<string> ConfigCandidates()
    {
        var roaming = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(roaming)) yield return Path.Combine(roaming, AppId, "app_config.json");
        yield return Path.Combine(Home, "Library", "Application Support", AppId, "app_config.json");
        yield return Path.Combine(Home, ".config", AppId, "app_config.json");
    }

    /// <summary>Vibe's config keys are flat dotted strings (<c>"model.path"</c>); a nested form is
    /// read too, so a reshuffle does not silently return nothing.</summary>
    public static string? ConfigValue(string? configText, string dottedKey)
    {
        if (Segments.TryParse(configText) is not JsonObject data) return null;
        var value = Segments.AsString(data[dottedKey]);
        if (value is null)
        {
            var dot = dottedKey.IndexOf('.');
            if (dot > 0 && data[dottedKey[..dot]] is JsonObject nested) value = Segments.AsString(nested[dottedKey[(dot + 1)..]]);
        }
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ReadConfig(string dottedKey)
    {
        var path = ConfigPath();
        if (path is null) return null;
        try { return ConfigValue(File.ReadAllText(path), dottedKey); }
        catch (IOException) { return null; }
    }

    /// <summary>The folder Vibe downloads models into (overridable in its config).</summary>
    public static string ModelsFolder()
    {
        if (ReadConfig("models_folder") is { } configured && Directory.Exists(configured)) return configured;
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) return Path.Combine(local, AppId);
        if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Application Support", AppId);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg, AppId);
    }

    /// <summary>Folders worth searching for model files: the models folder, and the folder of the
    /// model Vibe is configured with (they are usually the same).</summary>
    public static List<string> ModelFolders(string? modelPath)
    {
        var folders = new List<string> { ModelsFolder() };
        if (modelPath is not null && Path.GetDirectoryName(modelPath) is { Length: > 0 } dir) folders.Add(dir);
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public static string? FindModelFile(IEnumerable<string> folders, params string[] patterns)
    {
        foreach (var folder in folders)
            foreach (var pattern in patterns)
                foreach (var found in Directory.EnumerateFiles(folder, pattern).Order(StringComparer.OrdinalIgnoreCase))
                    return found;
        return null;
    }

    /// <summary>Diarization is a model: Vibe 3.3 ships sortformer (4 speakers), later builds a
    /// nemotron diarizer (up to 8). Located by convention beside the whisper model.</summary>
    public static readonly string[] DiarizePatterns =
        ["diar_*sortformer*.gguf", "*diarization*.gguf", "diar_*.gguf", "diar_*.onnx"];

    public static readonly string[] VadPatterns = ["ggml-silero*.bin", "*silero*.bin", "*vad*.bin"];

    /// <summary>Candidate locations of the engine binary, best first.</summary>
    public static IEnumerable<string> ServerCandidates()
    {
        var env = Environment.GetEnvironmentVariable("VIBE_SERVER_PATH");
        if (!string.IsNullOrEmpty(env)) yield return env;
        string[] names = OperatingSystem.IsWindows() ? ["vibe-server.exe", "sona.exe"] : ["vibe-server", "sona"];
        var dirs = new List<string>();
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) dirs.Add(Path.Combine(local, "vibe"));
        foreach (var programs in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            if (!string.IsNullOrEmpty(programs)) dirs.Add(Path.Combine(programs, "vibe"));
        if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Applications/vibe.app/Contents/MacOS");
            dirs.Add("/Applications/vibe.app/Contents/Resources");
        }
        dirs.AddRange(["/usr/lib/vibe", "/usr/lib/vibe/binaries", "/opt/vibe", "/opt/vibe/binaries"]);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).Select(d => d.Trim('"')));
        foreach (var dir in dirs)
            foreach (var name in names)
                yield return Path.Combine(dir, name);
    }

    public static string? FindServer(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath) ? explicitPath
                : throw new TranscribeException($"No vibe-server at {explicitPath}.");
        return ServerCandidates().FirstOrDefault(File.Exists);
    }
}

/// <summary>A running Vibe engine: one this process started (and will stop), or one it attached to
/// (and will leave alone).</summary>
public sealed class VibeServer : IDisposable
{
    public const string TranscribePath = "/v1/audio/transcriptions";
    public const string ReadyPath = "/ready";
    public const string HealthPath = "/health";
    public const string ModelLoadPath = "/v1/models/load";
    /// <summary><c>verbose_json</c> is the only format carrying <c>speaker</c>; the server's own VTT drops it.</summary>
    public const string ResponseFormat = "verbose_json";

    /// <summary>The multipart fields vibe-server reads (server/crates/vibe-server/src/server/transcription.rs).
    /// Its OpenAPI declares responses only, so an absent field there means unknown, not unsupported.</summary>
    public static readonly string[] KnownFields =
    [
        "file", "response_format", "diarize_model", "vad_model", "enhance_audio", "stable_timestamps",
        "stream", "language", "detect_language", "translate", "n_threads", "prompt", "temperature",
        "max_text_ctx", "word_timestamps", "max_segment_len", "sampling_strategy", "best_of", "beam_size",
    ];

    private static readonly HashSet<HttpStatusCode> BusyStatuses = [(HttpStatusCode)429, HttpStatusCode.ServiceUnavailable];
    private const int MaxBusyRetries = 5;

    private readonly Process? _process;
    private readonly List<string> _log;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Url { get; }
    public bool Owned => _process is not null;
    public string? Version { get; }
    public string Source { get; }

    private VibeServer(string url, Process? process, List<string> log, string? version, string source)
    {
        Url = url.TrimEnd('/');
        _process = process;
        _log = log;
        Version = version;
        Source = source;
    }

    public static VibeServer Attach(string url, string source) => new(url, null, [], null, source);

    /// <summary>Start <c>vibe-server serve [MODEL] --port 0</c> and wait for its ready line,
    /// <c>{"status":"ready","port":N,...}</c> on stdout (logs go to stderr). Port 0 lets the OS pick
    /// a free port, so nothing is configured and nothing collides with Vibe's own server.
    /// Passing the model preloads it, so the server is ready to transcribe when it says so.
    /// <c>--exit-with-parent</c> (the default) means a crash here cannot strand it.</summary>
    public static VibeServer Start(string binary, string? modelPath, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("serve");
        if (modelPath is not null) info.ArgumentList.Add(modelPath);
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add("0");
        // A model can stay loaded for the whole pass; the GUI's idle unload is not ours to inherit.
        info.ArgumentList.Add("--unload-timeout");
        info.ArgumentList.Add("0");

        var log = new List<string>();
        var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.Add(e.Data);
            if (Segments.TryParse(e.Data.Trim()) is JsonObject payload
                && Segments.AsString(payload["status"]) == "ready" && payload["port"] is not null)
                ready.TrySetResult(payload);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.Add(e.Data); };
        process.Exited += (_, _) => ready.TrySetException(new TranscribeException(
            $"{binary} exited ({SafeExitCode(process)}) before reporting a port:\n{Tail(log)}"));
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new TranscribeException($"Could not start {binary}: {ex.Message}"); }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!ready.Task.Wait(timeout))
        {
            Kill(process);
            throw new TranscribeException($"{binary} did not report a ready port within {timeout.TotalSeconds:0}s:\n{Tail(log)}");
        }
        JsonObject line;
        try { line = ready.Task.Result; }
        catch (AggregateException ex) when (ex.InnerException is TranscribeException inner) { throw inner; }
        var port = (int)(Segments.AsDouble(line["port"]) ?? 0);
        var version = Segments.AsString(line["version"]);
        return new VibeServer($"http://127.0.0.1:{port}", process, log, version, binary);
    }

    private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(Fmt.Inv); } catch (InvalidOperationException) { return "?"; } }

    private static string Tail(List<string> log)
    {
        lock (log) return string.Join('\n', log.TakeLast(8).Select(l => "  " + l));
    }

    public string LogTail() => Tail(_log);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Stop the server if this process started it. vibe-server has no shutdown route; Vibe
    /// itself stops its own by killing the child, and so does this.</summary>
    public void Dispose()
    {
        if (_process is not null) Kill(_process);
        _process?.Dispose();
        _http.Dispose();
    }

    /// <summary>Fail fast, and say which fault it is, before any long work: refused (nothing
    /// listening there) and timed out (a firewall dropping packets) need opposite fixes.</summary>
    public void CheckReachable(TimeSpan? timeout = null)
    {
        var uri = new Uri(Url);
        using var client = new TcpClient();
        try
        {
            if (!client.ConnectAsync(uri.Host, uri.Port).Wait(timeout ?? TimeSpan.FromSeconds(5)))
                throw new TranscribeException($"Cannot reach the Vibe server at {Url}: the connection timed out "
                    + "rather than being refused, so packets are being dropped — a firewall, not a missing server.");
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException se)
        {
            throw new TranscribeException(se.SocketErrorCode == SocketError.ConnectionRefused
                ? $"Cannot reach the Vibe server at {Url}: connection refused. Nothing is listening on that port. "
                  + "If you meant Vibe's own server, its Local API toggle (Settings) must be on, and its port changes on every run."
                : $"Cannot reach the Vibe server at {Url}: {se.Message}");
        }
    }

    /// <summary>GET a text endpoint. A non-2xx body is still returned: <c>/ready</c> answers 503 with
    /// the reason, and discarding it turns "no model loaded" into "the server did not answer".</summary>
    public string? GetText(string path, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            using var response = _http.GetAsync(Url + path, cts.Token).GetAwaiter().GetResult();
            return response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Read <c>/ready</c> generously: ready or not, and why not. An unrecognized body counts
    /// as ready, because refusing to work over an unfamiliar format is worse than trying.</summary>
    public static (bool Ready, string Detail) ParseReadiness(string? payload)
    {
        if (payload is null) return (false, "the server did not answer /ready");
        var text = payload.Trim();
        var lowered = text.ToLowerInvariant();
        if (lowered.Contains("no_model") || lowered.Contains("no model loaded"))
            return (false, "no model is loaded: the server is up but has nothing to transcribe with.");
        if (Segments.TryParse(text) is not JsonObject data) return (true, text.Length > 200 ? text[..200] : text);
        var message = Segments.AsString(data["message"]);
        foreach (var key in new[] { "ready", "ok" })
            if (data[key] is JsonValue v && v.TryGetValue<bool>(out var flag))
                return (flag, message ?? data.ToJsonString());
        if (Segments.AsString(data["status"]) is { } status)
            return (status.Equals("ready", StringComparison.OrdinalIgnoreCase) || status.Equals("ok", StringComparison.OrdinalIgnoreCase),
                message is null ? status : $"{status}: {message}");
        return (true, text.Length > 200 ? text[..200] : text);
    }

    /// <summary><c>/v1/models</c> in one line: each model's id and engine (<c>ggml-large-v3.bin
    /// (whisper)</c>). The full answer lists every supported language and is not worth a screen.
    /// An unfamiliar shape is shown raw, shortened, rather than hidden.</summary>
    public static string? DescribeModels(JsonNode? payload)
    {
        if (payload is null) return null;
        if (payload is JsonObject { } obj && obj["data"] is JsonArray data)
        {
            if (data.Count == 0) return "none";
            return string.Join(", ", data.Select(entry =>
            {
                var id = Segments.AsString(entry?["id"]) ?? "?";
                var engine = Segments.AsString(entry?["capabilities"]?["engine"]);
                return engine is null ? id : $"{id} ({engine})";
            }));
        }
        var raw = payload.ToJsonString();
        return raw.Length > 200 ? raw[..200] + " ..." : raw;
    }

    /// <summary>Make the server ready, loading <paramref name="modelPath"/> if it has none. A silent
    /// <c>/ready</c> with a healthy <c>/health</c> means busy (it holds the model lock for a whole
    /// transcription), and loading a model then would evict the one a running job is using.</summary>
    public (bool Ready, string Detail) EnsureModel(string? modelPath)
    {
        var answer = GetText(ReadyPath);
        if (answer is null && GetText(HealthPath) is not null)
            return (false, "the server did not answer /ready but /health is fine: it is busy transcribing "
                + "something else. Wait for it rather than loading a model over a running job.");
        var (ready, detail) = ParseReadiness(answer);
        if (ready) return (true, detail);
        if (modelPath is null) return (false, detail + " No model path to load (pass --model).");
        Console.WriteLine($"loading model {modelPath} ...");
        var (loaded, note) = LoadModel(modelPath);
        if (!loaded) return (false, $"{detail} Loading {modelPath} failed: {note}");
        return ParseReadiness(GetText(ReadyPath));
    }

    /// <summary>POST /v1/models/load with <c>{"path": ...}</c>. Loading a large model is slow.</summary>
    public (bool Loaded, string Note) LoadModel(string modelPath)
    {
        var body = new JsonObject { ["path"] = modelPath }.ToJsonString();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = _http.PostAsync(Url + ModelLoadPath, content, cts.Token).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) return (true, "loaded");
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return (false, $"HTTP {(int)response.StatusCode} {(text.Length > 200 ? text[..200] : text)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"could not reach {ModelLoadPath}: {ex.Message}");
        }
    }

    /// <summary>The multipart body. Names are quoted by hand: .NET writes <c>name=file</c> unquoted,
    /// which RFC 7578 allows and which multipart parsers do not all accept. Quoted is what curl,
    /// browsers and the original Python sent, so it is the form known to work.</summary>
    internal static MultipartFormDataContent BuildForm(string clipPath, IReadOnlyDictionary<string, string> fields)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var form = new MultipartFormDataContent("----holtz" + Guid.NewGuid().ToString("N"));
        form.Add(new StringContent(ResponseFormat), Quote("response_format"));
        foreach (var (key, value) in fields)
            if (key != "response_format") form.Add(new StringContent(value), Quote(key));
        var bytes = new ByteArrayContent(File.ReadAllBytes(clipPath));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        // Set by hand: Add(content, name, fileName) also emits an RFC 5987 filename*, which would
        // carry the quotes along as %22.
        bytes.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = Quote("file"),
            FileName = Quote(Path.GetFileName(clipPath)),
        };
        form.Add(bytes);
        return form;
    }

    /// <summary>POST one clip and return the raw response body. Serial by construction: the server
    /// runs one transcription at a time and answers 429 to the rest, so a busy server is waited
    /// out with backoff rather than worked around.</summary>
    public string Transcribe(string clipPath, IReadOnlyDictionary<string, string> fields, TimeSpan? timeout = null)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 0; ; attempt++)
        {
            using var form = BuildForm(clipPath, fields);

            HttpResponseMessage response;
            try
            {
                using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromHours(1));
                response = _http.PostAsync(Url + TranscribePath, form, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new TranscribeException($"Lost the Vibe server at {Url} during {Path.GetFileName(clipPath)}: {ex.Message}"
                    + (Owned ? "\n" + LogTail() : ""));
            }
            using (response)
            {
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return text;
                if (!BusyStatuses.Contains(response.StatusCode) || attempt >= MaxBusyRetries - 1)
                    throw new TranscribeException($"Vibe server returned {(int)response.StatusCode} for "
                        + $"{Path.GetFileName(clipPath)}: {(text.Length > 400 ? text[..400] : text)}");
            }
            Thread.Sleep(delay);
            delay *= 2;
        }
    }
}
