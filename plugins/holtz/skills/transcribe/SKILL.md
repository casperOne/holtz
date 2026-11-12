---
name: transcribe
description: Transcribe a long audio recording (meeting, interview, lesson, call) to a timed, speaker-labeled transcript with the local Vibe engine. Measures the audio, cuts it at its true silences so whisper never starts on silence, transcribes clip by clip, detects and automatically re-cuts whisper hallucination loops, and assembles a WebVTT plus a readable transcript with names. Optionally cross-checks against a Google Recorder transcript of the same conversation. Use when the user wants a recording transcribed, a Vibe transcript checked or repaired, speakers named in a transcript, or two transcripts of one recording compared.
argument-hint: "[audio file] [--google transcript.txt]"
---

# Transcribe a recording with Vibe (holtz)

The tool is a .NET 10 C# script bundled with this skill. Every command below is:

```bash
dotnet run --no-cache "${CLAUDE_SKILL_DIR}/scripts/transcribe.cs" -- <command> [args]
```

`--no-cache` is required, not optional: a .NET 10 file-based app does not notice changes in the
project it references (`src/Holtz`), so without it a stale build can run. It costs 2–3 s.
Quote every path; recordings often have spaces in their names. On Windows, forward slashes work.

Read `${CLAUDE_SKILL_DIR}/reference/design-notes.md` when a run fails, a threshold needs changing,
or the user asks why the tool does something. It holds the measurements behind every default.

## What you must never do

- Never hand-edit `clips/*.json`, `transcript.vtt`, or the Google file. They are evidence. To change a
  result, re-run the tool (with `--out` to a new folder; two transcripts of one recording are two
  pieces of evidence, not a correction).
- Never treat a diarization id as an identity. Ids are per clip (namespaced +100 per clip, +10 per
  re-cut inside a clip) and are NOT comparable across clips: two cuts of one recording have come back
  with the ids reversed. Map every id from its own quotes.
- Never "correct" Vibe's text with Google's or the reverse. The cross-check says where they disagree;
  the audio decides. The only merge is `normalize --fill`, which inserts Google text only where Vibe
  has nothing, and marks it `~google`.
- Never use `--allow-degraded` silently. If you use it, say in your reply which stretches are degraded.
- Never send audio anywhere. Transcription is local; there is no cloud fallback.

## Step 0: preflight (once per machine, or when something fails)

```bash
dotnet run --no-cache "${CLAUDE_SKILL_DIR}/scripts/transcribe.cs" -- probe
```

It checks ffmpeg/ffprobe, finds Vibe's config, model, diarization model and the `vibe-server`
engine, starts the engine, confirms it is ready, and stops it. Exit 0 means ready. Fixes:

| Probe says | Tell the user |
|---|---|
| `dotnet` not found | Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` |
| ffmpeg MISSING | `winget install Gyan.FFmpeg`, then open a new terminal (or set `FFMPEG_DIR`) |
| engine NOT FOUND | Install Vibe, or pass `--vibe-server PATH` (Windows: `%LOCALAPPDATA%\vibe\vibe-server.exe`; installs before Vibe 3.1.11 call it `sona.exe`) |
| model none configured | Pick a model in Vibe once, or pass `--model PATH` to a whisper `.bin`/`.gguf` |
| NO diarization model | In Vibe, turn on speaker recognition once so it downloads the model; or accept `--no-diarize` (every line becomes `[?]`) |

This skill needs the machine that has Vibe and the audio. In a cloud session with neither, say so and
work only on transcript files that are already present (`normalize`, `google`, `worksheet`).

## Step 1: before running, ask about speakers (optional, one question)

Ask once, briefly: *"Do you want to tell me who is in the recording now? If you do, I can name the
speakers when it finishes. Otherwise I'll show you who said what and ask then."* Accept whatever they
give: names, roles, how to tell them apart ("Ana interviews, asks the questions; Ben answers").
Also ask whether to **apply** the names without checking back, or **confirm** first (default: confirm).

If they give names, pass each as `--people "Ana: interviewer, asks the questions"`. This only writes
them into `speaker-worksheet.md` and the provenance record; the engine cannot be told about voices in
advance (vibe-server has no enrollment or speaker-count option). The names are applied after the run,
by you, from the quotes.

Do not ask for a Google transcript. It is optional. Use it if the user mentions or provides one.

Pass `--language CODE` whenever you know the recording's language from what the user said or from
context. If you cannot tell, add that to the same question. Left to auto-detect, whisper has invented
foreign-language text over near-silence (see design notes).

## Step 2: run

```bash
dotnet run --no-cache "${CLAUDE_SKILL_DIR}/scripts/transcribe.cs" -- run "AUDIO" --language en [--google "GOOGLE.txt"] [--people "..."]
```

- Output goes to `<audio name>_transcription/` beside the audio unless `--out DIR`.
- The tool **starts its own `vibe-server`** (headless, free port, model preloaded), and **stops it**
  when the run ends, including on failure. `--server-url URL` attaches to an already-running server
  instead and leaves it running.
- Runs take minutes (roughly real time on CPU, faster on GPU). Run it in the background or with a
  long timeout (up to 600000 ms in the foreground). Do not poll; wait for it to finish.
- To preview the cuts first (no transcription): `plan "AUDIO"`. To see the loudness map: `profile "AUDIO"`.
- `--prompt "names, jargon"` helps whisper spell names. `--param key=value` passes any other field
  vibe-server reads (see design notes).

Exit codes: `0` clean; `2` error (read the message); `4` a clip, or a stretch of one, stayed bad after
automatic re-cuts, or nothing transcribed. On `4` the transcript is still written, and its NOTE lists
what was left out.

**A clean exit does not mean every line is real.** Read the `SUSPECT CUES` notes at the top of
`transcript.vtt`: they list trailing lines and short repeats (`"..." x4`) that were kept but could be
invented. Report them with their times. With a Google transcript, check them against `cross-check.md`.

### When a clip or stretch is excluded (exit 4)

The gate already re-cut it twice. A stretch is left out when it was mostly a loop; the rest of its
clip is kept. Next moves, in order:

1. Re-run just that window into a new folder; times stay on the recording's timeline:
   `run "AUDIO" --from 612 --to 1323 --out "AUDIO_DIR/recut_10-12_to_22-03"`.
   Splitting a long failing window at the second the gate names (two runs: `--from A --to LOOP` and
   `--from LOOP --to B`) has recovered stretches that two automatic retries could not.
2. If a Google transcript exists, its text for that stretch is in `cross-check.md`, and
   `normalize --fill` can carry it into the readable transcript, marked `~google`.
3. Only if the user accepts it: `--allow-degraded`, and say which parts are degraded.

## Step 3: name the speakers

Read `speaker-worksheet.md`. For each clip and each id it lists cue count, words, talk time,
questions asked, and quotes spread across the clip. For each id:

1. Decide who it is **from what they say**: who asks and who answers, who explains, who is addressed
   by name, what each person would plausibly say. Opening lines are the weakest evidence (one person
   usually sets up). Use the `--people` descriptions if given.
2. If an id's quotes mix two people, or it has one or two short cues, say so; do not force a name.
   Leaving it unmapped renders it `[Speaker N]`, which is honest.
3. Do not infer tone or intent from the text to decide who someone is.
4. **Check every named person has an id in each pass.** The diarization model has a fixed number of
   speaker slots (Sortformer 4, Nemotron 8). The worksheet marks a pass that used them all
   (`All N speaker slots used`). If such a pass has two ids for one person and a named person has
   none, that person's lines are filed under the others: look for their content (addressed by name,
   their topics) inside other ids' quotes. Re-run that stretch on its own, with `--from`/`--to` set at
   turn boundaries (a line start in that pass's `transcript.txt` or a cue start in its VTT) so the
   new piece replaces whole lines. Diarization starts fresh and usually gives the person an id. Tell
   the user which stretch you re-ran and why. Re-runs are minutes; offer, do not skip.

Then:
- **Confirm mode (default):** show the user the proposed mapping as a table (id → name → 1–2 short
  quotes), call out uncertain ids, and wait for their answer.
- **Apply mode (user said so up front):** go straight to Step 4, and show the same table in your
  final reply. Still ask about any id you could not place.

## Step 4: the readable transcript

```bash
dotnet run --no-cache "${CLAUDE_SKILL_DIR}/scripts/transcribe.cs" -- normalize "OUT/transcript.vtt" "OUT/transcript.txt" --speakers "0=Ana,1=Ben,100=Ben,101=Ana" [--title "..."] [--note "..."]
```

Output lines are `[MM:SS Name] text`, consecutive lines by one person merged unless a pause of 10 s or
more separates them (`--turn-gap S`), so every line's time is true for its first words. Unmapped ids render
`[Speaker N]`, unlabeled fragments `[?]`. A `SUSPECT CUES` note from the run is copied into the
header. `normalize` re-runs the quality gate and refuses (exit 4) a transcript that looks like a
transcription failure; `--allow-degraded` overrides it. `--strict` fails (exit 3) on unmapped ids.

## Optional: a Google Recorder transcript

Google Recorder's text export has words and paragraphs, usually no timings and no speakers. It is a
second engine on a second microphone, so it is good corroboration. With `run --google FILE` (or later:
`google "FILE" --vtt "OUT/transcript.vtt"`), the tool:

- copies the file into the output folder verbatim;
- aligns its words to Vibe's and writes `transcript_google.vtt`: Google's words with **estimated**
  times and speaker ids **projected** from Vibe (the same namespaced ids, so the same `--speakers` map
  works with `normalize`);
- writes `cross-check.md`: agreement by minute, runs of words only Google has (with whether they fall
  in an excluded clip, a skipped silence, or a stretch dropped as noise), runs only Vibe has (whisper
  loops and inventions show up here), and whether Google heard each "Thank you."-type line.

How to use it: `normalize "OUT/transcript.vtt" "OUT/transcript.txt" --speakers "..." --fill
"OUT/transcript_google.vtt"` fills Vibe's gaps (≥ 10 s, `--fill-min-gap`) with Google's text, marked
`~google`. Report the cross-check's flagged stretches to the user as places to listen, not as errors
in one transcript or the other.

## Other commands

- `worksheet "transcript.vtt" [--out FILE] [--people "..."] [--speaker-slots N]`: rebuild the speaker
  worksheet from any transcript (e.g. a VTT exported by hand from the Vibe app). The slot limit is read
  from `transcript-assembly.json` beside the VTT; pass `--speaker-slots` when there is none.
- `normalize` also reads a `verbose_json` response or Vibe's older JSON array.
- `help`: every option.

## The output folder

| File | What it is |
|---|---|
| `clips/clip_NN_MMmSSs-MMmSSs.json` | Raw engine response per clip (evidence). `_retry_from_MMmSSs` = an automatic re-cut. |
| `transcript.vtt` | Derived: the clips assembled on the recording's timeline, `[Speaker N]` ids namespaced; NOTE blocks say how it was made, what was skipped, excluded, or suspect. |
| `transcript-assembly.json` | Provenance: audio SHA-256, server and model, request fields, the plan, every ffmpeg command, each clip's attempts and gate result. |
| `speaker-worksheet.md` | Per-id stats and quotes for naming speakers. |
| `transcript.txt` | Written by `normalize`: the readable transcript with names. |
| `<google file>`, `transcript_google.vtt`, `cross-check.md` | Only when a Google transcript was given. |

## Reporting back

Lead with the result: where the transcript is, how much of the recording it covers, and anything
excluded or suspect (with times). Then the speaker table (Step 3). Keep it short; the files hold the
detail.
