# holtz: agent instructions

Repository for the `holtz` Claude Code plugin (marketplace `holtz`, one plugin `holtz`, one skill
`transcribe`). The skill drives a C# (.NET 10) tool that transcribes long recordings with Vibe's
local engine. Read `plugins/holtz/skills/transcribe/reference/design-notes.md` before changing any
threshold, check or file format: every default there was measured on a real failure.

## Layout

- `.claude-plugin/marketplace.json`: the marketplace; plugin source `./plugins/holtz`.
- `plugins/holtz/`: the plugin. Everything a user installs lives here and only here (files outside the
  plugin directory are not copied to the plugin cache).
  - `skills/transcribe/SKILL.md`: the agent workflow. Keep under 500 lines; agent-dense.
  - `skills/transcribe/scripts/transcribe.cs`: file-based app, `#:project ../src/Holtz/Holtz.csproj`.
  - `skills/transcribe/src/Holtz/`: the engine. Base class library only; no NuGet packages, ever
    (a first run must need nothing but the .NET SDK).
  - `skills/transcribe/reference/design-notes.md`: rationale and measurements.
- `tests/Holtz.Tests/`: xUnit. Outside the plugin so it is not installed.
- `Holtz.slnx`: the solution.

## Build and test

```bash
dotnet build Holtz.slnx
dotnet test Holtz.slnx
dotnet run --no-cache plugins/holtz/skills/transcribe/scripts/transcribe.cs -- help
```

- **Always `--no-cache`** when running the script after changing `src/`: a .NET 10 file-based app's
  up-to-date check ignores the `#:project` it references, so it silently runs the old build.
- `TreatWarningsAsErrors` is on in `Holtz.csproj`.
- Do not name a file-based app after the project it references (`holtz.cs` + `Holtz.csproj` fails
  NuGet restore: "Ambiguous project name").

## Invariants (do not break)

- Raw engine responses (`clips/*.json`) and any second-source transcript are evidence: written once,
  never edited. `transcript.vtt` is derived and must stay reproducible from them plus
  `transcript-assembly.json`.
- Speaker ids are namespaced (+100 per clip, +10 per re-cut) and never merged or inferred across clips.
- Never clamp an over-running cue (drop it whole); never delete a suspect trailing cue or short
  repeat (flag it). Only a stretch that is mostly a loop may be left out, and it must be reported.
- Never average loudness across a recording; peaks for speech detection, never means.
- The Google transcript is optional and never corrects Vibe's text. `normalize --fill` inserts it only
  into gaps, marked `~google`.
- All number formatting is invariant culture (`Fmt`); all text output is UTF-8 without BOM, `\n` line
  endings (`Fmt.WriteText`).
- `vibe-server` started by the tool is always stopped (`VibeServer.Dispose` kills the process tree);
  an attached server (`--server-url`) is never stopped.
- Multipart field names stay quoted (`VibeServer.BuildForm`).

## Changing things

- New CLI option: add it to `Cli.Usage`, the command's `Allow(...)` list, `SKILL.md` if the agent
  should use it, and a test.
- New gate check: it must be tuned on a real failure, documented in design-notes with its numbers, and
  tested with a synthetic case. Ask: can a single fabricated cue fail it on its own?
- Changed output files: update the tables in `SKILL.md` and `README.md`.
- `README.md` is for people: plain language, main point first. `SKILL.md`, `design-notes.md` and this
  file are for agents: dense and complete.
- Never commit audio or real transcripts; tests use synthetic data only.
