# holtz

Transcription for long recordings that have long silences in them.

One pass of [Vibe](https://github.com/thewh1teagle/vibe) (or any whisper-based tool) over a long
recording with quiet stretches can fail badly. Whisper fills the silence with invented lines, then
loops on them, and minutes of real speech go missing. holtz stops that from happening:

1. It **measures** the recording and **cuts** it at its true silences, so whisper never starts on silence.
2. It **transcribes** each clip on your own machine with Vibe's engine. holtz starts the engine for the
   run and stops it afterwards.
3. It **checks** every clip for the signs of a whisper loop. When it finds one, it re-cuts the clip
   from that point and tries again.
4. It **assembles** one timed transcript, with speaker labels, on the recording's own timeline.
5. Claude **names the speakers** from what each one says, and checks the names with you.
6. If you also have a **Google Recorder** transcript of the same conversation (optional), holtz lines
   the two up and tells you where they disagree.

It is a Claude Code skill. You ask Claude to transcribe a recording; the skill runs the C# tool and
does the speaker naming. You can also run the tool yourself.

## What you need

On the machine that has the recording (Windows, macOS or Linux):

- **Vibe**, installed, with a transcription model downloaded. Turn on speaker recognition in Vibe
  once, so it downloads the diarization model.
- **.NET 10 SDK**: `winget install Microsoft.DotNet.SDK.10`
- **ffmpeg**: `winget install Gyan.FFmpeg` (then open a new terminal)

No Python. No cloud: the audio never leaves your machine.

## Install the skill

In Claude Code:

```
/plugin marketplace add casperOne/holtz
/plugin install holtz@holtz
```

The repository is private, so git on that machine must be able to read it. For example, run
`gh auth login` and then `gh auth setup-git`.

To get updates later, run `/plugin marketplace update holtz`.

**Without the plugin system:** copy `plugins/holtz/skills/transcribe` to
`%USERPROFILE%\.claude\skills\transcribe` (Windows) or `~/.claude/skills/transcribe`.

## Use it

Ask Claude, for example: *"Transcribe C:\recordings\interview.m4a. It's Ana interviewing Ben."*

Claude checks the machine, runs the transcription, and shows you who it thinks each speaker is, with
quotes. You confirm, and it writes the final transcript. If you'd rather not confirm, say so up front.

### Run the tool yourself

```bash
cd plugins/holtz/skills/transcribe/scripts
dotnet run --no-cache transcribe.cs -- probe                     # is this machine ready?
dotnet run --no-cache transcribe.cs -- plan  "interview.m4a"     # where it will cut, before transcribing
dotnet run --no-cache transcribe.cs -- run   "interview.m4a"     # transcribe
dotnet run --no-cache transcribe.cs -- normalize "interview_transcription/transcript.vtt" "interview_transcription/transcript.txt" --speakers "0=Ana,1=Ben,100=Ben,101=Ana"
dotnet run --no-cache transcribe.cs -- help                      # every option
```

The results go in an `interview_transcription` folder next to the recording:

| File | What it is |
|---|---|
| `transcript.txt` | The readable transcript: `[MM:SS Name] text` |
| `transcript_full.txt` | Only when you `join` several runs: the readable transcript from all of them |
| `transcript.vtt` | The timed transcript with speaker numbers, plus notes on what was skipped or doubtful |
| `speaker-worksheet.md` | What each speaker number said, used to name them |
| `transcript-assembly.json` | How it was made: audio checksum, settings, every command |
| `clips/` | The engine's raw answers, one per clip |
| `cross-check.md`, `transcript_google.vtt` | Only if you gave a Google transcript |

**Speaker numbers restart in every clip.** Clip 2's numbers start at 100, clip 3's at 200, and so on.
Speaker 1 in clip 1 and speaker 101 in clip 2 may or may not be the same person. That is why each
number is named from its own quotes.

**Speaker recognition can tell apart only so many voices at once** (4 with Vibe's default model).
If a clip uses all of them, `speaker-worksheet.md` says so: one person may have been split in two and
another folded into someone else. Re-running just that stretch usually fixes it.

**When a recording takes more than one run** (a stretch re-run to fix a loop or a lost speaker),
`join` puts the runs back together into one transcript, each named with its own speaker mapping. The
later, narrower runs replace what the first run had for their stretch, and the header says which run
each part of the timeline came from.

## For developers

- `Holtz.slnx`: open it in Visual Studio or Rider.
- `plugins/holtz/skills/transcribe/src/Holtz`: the engine, a class library with no NuGet packages.
- `plugins/holtz/skills/transcribe/scripts/transcribe.cs`: a .NET 10 file-based app that runs it.
- `tests/Holtz.Tests`: run `dotnet test` from the repository root. The end-to-end test needs
  ffmpeg and returns early without it.
- `plugins/holtz/skills/transcribe/reference/design-notes.md`: why every threshold is what it is.

## License

Apache 2.0. See [LICENSE](LICENSE).
