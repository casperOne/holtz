# holtz design notes

Why the tool does what it does, with the measurements behind each default. Ported from a
Python pipeline that was hardened over months of real recordings (two people at a table, a phone
or laptop microphone, 20–60 minutes, long stretches of silence while one of them works). Every
rule below was paid for by a lost or corrupted transcript.

## The failure this exists to prevent

Whisper conditions each 30-second window on the text it just emitted. Hand it a long true silence
and it emits a filler token ("Thank you.", "you", "."), then conditions the next window on that
filler, and loops. Measured once: fifteen minutes of a recording lost this way, while the speech on
both sides of the dead stretch measured -13 to -25 dBFS, as loud as the parts that transcribed
perfectly. The audio was fine. The fix is mechanical: **never hand the model a window that opens on
silence.** Measure the recording, cut it at its true silences, transcribe each clip on its own, and
put the cues back on the recording's timeline.

## Measuring

- **Never average loudness across a recording.** An average over a recording containing a long
  silence is a number about nothing: averaging 6.5 minutes of true silence with 19 minutes of speech
  read as "audio fine" and cost a day. `profile` reports runs of similar level instead.
- Speech in these recordings: -13 to -25 dBFS. True silence: -62 to -73. `--noise-db -40` sits between.
- `astats`' own reset counts decoder frames, not seconds (1024 samples = 64 ms at 16 kHz). Asking it
  for 5 s windows that way silently gives 64 ms windows. The tool resamples to 16 kHz and re-frames
  with `asetnsamples` so one frame is exactly one window.

## Cutting

- **Coarse pass**: `silencedetect` at -40 dBFS, runs >= 20 s (`--min-silence`). Whisper's window is
  30 s; silence longer than that seeds loops. 20 s cuts before that, leaving ordinary pauses intact.
  A ~50 s thinking pause did seed a loop once, which is why it is not higher.
- **Fine pass** (runs >= 1 s) checks every boundary. A single blip (chair, page) inside a long quiet
  stretch splits it into runs too short for the coarse pass, so the clip opens in dead air. Measured:
  a boundary at 14:32 with silence until 15:02, and that clip hallucinated one sentence 99 times. The
  start is moved to where speech resumes; gaps under 1.5 s between fine runs count as blips.
- **Bridging finds cuts** the coarse pass cannot see: pencil-on-paper every few seconds hid a
  five-minute silence entirely (default plan: one 39-minute pass). Fine runs are joined across any
  sound whose loudest second stays under `--bridge-peak-db` (-38). **Loudness only, never length**: a
  short "No." in a long silence must survive. Measured: pencil -39.6/-39.7 dBFS; a quiet but real
  line -36.0. Stricter than the noise-island floor on purpose: a missed cut is caught later by the
  gate, a real line skipped as noise is lost without trace. Every bridged sound is listed.
- **A clip must contain speech, not merely cross the threshold.** A rustle 4 dB over -40 carved an
  8.9 s island out of a 76 s silence and came back as one fabricated "Thank you.". A candidate whose
  loudest second stays under `--min-peak-db` (-35) is dropped before it is numbered. Peak, never mean.
- Pre-roll 0.35 s, tail 0.75 s, so no word is clipped. Cuts re-encode to 16 kHz mono WAV with input
  seeking, so clip times match the source exactly.
- `--from/--to` restrict the plan to one window and keep recording-timeline timestamps: the
  replacement for cutting a window by hand with `ffmpeg -ss` (whose outputs then start at 0:00).

## The quality gate (runs per clip)

Each check tuned for a failure seen in a real export. Healthy recordings never tripped them.

| Check | Threshold | Names a window? |
|---|---|---|
| Silence token repeated in a row | >= 5 | yes |
| Every line is a silence token | any length | no (nothing to recover) |
| One sentence of >= 4 words repeated | >= 5 times and >= 5% of lines (healthy: <= 3, ~1%; a loop: 361, 38.5%) | yes |
| Short-line cycle (each line repeats the one 1–4 back) | >= 12 lines (real loops 17–223; healthy <= 8) | yes |
| Duplicate share | >= 50% (healthy 19–26%; a loop 86%) | no |
| Word rate over >= 5 min | < 30/min (real 100–140; a dead stretch 6.1) | no |
| Segments at one duration >= 25 s | >= 50% of them (whisper's synthetic fallback) | no |

`no_speech_prob` is useless as a filter: whisper reported 3e-7 on a rustle, 1.5e-10 on a cue
describing audio that did not exist, 8.9e-6 on a trailing silence token. Every check above is tuned
for a dominant failure; **a single fabricated cue passes all of them**, which is why the two rules
below exist.

**Automatic recovery.** When a finding names a second, everything before it is kept, the clip is
re-cut from there and transcribed again (default twice, `--max-retries`), and the combined result is
re-gated. Restarting inside the loop breaks it because the model no longer has its own repetitions
to condition on. Every attempt's raw response is kept. A clip still bad after that is excluded
unless `--allow-degraded`. Splitting a stubborn window into two runs at the loop point has worked
where two retries did not.

**Each stretch kept across a re-cut is judged alone.** The phrase check is a share of the clip, so a
long clip dilutes a bad minute: 60 s of invented Norwegian over near-silence (5 identical lines of 6)
passed once it was joined to 275 good lines, at under 2%. Only a second engine exposed it. A stretch is
now left out (raw response kept, listed in the VTT's NOTE and `rejected_stretches`, exit 4) when it is
all silence tokens, or holds a loop and is at least half repeats. Replaying that recording's saved
responses rejects those 6 cues and keeps the 196-cue stretch after them. A small repeat beside good
lines does not cost the good lines; the next rule flags it.

**Short loops are flagged, not rejected.** A line of >= 3 words recurring >= 3 times, each within 4
lines of the last, is listed under `SUSPECT CUES`. These are too short for the gate (the cycle check
needs 12 lines; real ones here ran 3–11 repeats, and healthy speech reaches 8). Over 30 real
transcripts (8,704 lines) the rule hit 10 places, including every short loop Google Recorder's text
contradicted in one recording. It cannot prove a line was not said, so it keeps the line. It flags
loops, so a single fabricated cue does not trip it; the two rules below cover that case.

**Set the language when it is known.** On auto-detect, whisper wrote Norwegian and Korean over
near-silence in an English recording. Two full runs of that 30-minute recording, auto-detect vs.
`language=en`: clips left out 3 vs. 1, short loops flagged 5 vs. 0, foreign-language text gone, and the
share of Vibe's words Google corroborated rose from 73% to 82%. One recording, and whisper varies
between runs, so treat it as strong evidence and not a guarantee. Sound effects in asterisks and
stray silence tokens still appear over near-silence.

**A cue cannot describe audio the clip never held.** A clip of 241.3 s returned a final cue ending
28.1 s past it (exactly whisper's 30 s window), text "Thank you.". It is dropped whole, not clamped.
0.5 s of overrun is tolerated as resampling.

**What cannot be proven is flagged, not deleted.** A clip's last cue, unlabeled, whose text is a
known silence token, is kept and listed under `SUSPECT CUES` (copied into `transcript.txt`'s header).
A fabrication left in is visible to a reader; a real line deleted is invisible. With a Google
transcript, `cross-check.md` says whether Google heard it.

## Speakers

- Diarization needs `diarize_model=<path to the model file>`. **It is a path, not a flag.**
  `diarize=true` and other boolean spellings are silently ignored, and the response then simply has
  no `speaker`. The server's own VTT output never carries speakers, so the tool asks for
  `verbose_json` and builds the VTT itself.
- Ids are unstable across recordings, sparse (0, 2, 3, never 1), sometimes absent, and one person
  can be split across ids or two merged into one. Two cuts of one recording came back with the
  ids reversed. So ids are namespaced per clip (+100) and per re-cut (+10), never merged, and every
  one is mapped from its own quotes. The worksheet samples quotes from across the clip because
  openings are all one person setting up.
- There is no way to tell the engine about voices in advance: vibe-server has no enrollment and no
  speaker-count option (Sortformer handles up to 4 speakers, Vibe's newer Nemotron diarizer up to 8).

## The Vibe engine

Facts from the Vibe source (github.com/thewh1teagle/vibe, read at main `da8c2ca`, 2026-10-30;
release v3.3.0 ships `server-v0.6.10`). Re-check when Vibe changes.

- The engine is `vibe-server` (`sona` before Vibe 3.1.11), beside `vibe.exe`
  (`%LOCALAPPDATA%\vibe\` by default). Standalone builds: GitHub releases tagged `server-v*`.
- `vibe-server serve [MODEL] --host 127.0.0.1 --port 0 --exit-with-parent true --unload-timeout 0`.
  Port 0 lets the OS pick. The positional model is preloaded. It prints
  `{"status":"ready","port":N,"version":...}` on stdout when listening; logs go to stderr.
  `--exit-with-parent` (default on) means it dies with the process that started it.
  There is no HTTP shutdown; Vibe stops its own by killing the child, and so does holtz.
- Routes: `GET /health`, `GET /ready` (503 + reason with no model), `POST /v1/models/load`
  `{"path": ...}`, `GET /v1/models`, `DELETE /v1/models`, `POST /v1/audio/transcriptions`,
  `/openapi.json` (declares responses only: an absent request field there means undocumented, not
  unsupported).
- Transcription fields read: `file`, `response_format` (`verbose_json` for speakers), `diarize_model`,
  `vad_model`, `enhance_audio`, `stable_timestamps` (needs `vad_model`), `stream`, `language`,
  `detect_language`, `translate`, `n_threads`, `prompt`, `temperature`, `max_text_ctx`,
  `word_timestamps`, `max_segment_len`, `sampling_strategy`, `best_of`, `beam_size`. Not read:
  `model`, `num_speakers`, `timestamp_granularities`. One transcription at a time; 429 to a second.
- `verbose_json` segments: `{start, end, text, speaker?, no_speech_prob}`; no word array.
- Config: `%APPDATA%\github.com.thewh1teagle.vibe\app_config.json`, flat dotted keys: `model.path`
  (selected model), `models_folder` (override), `api.baseUrl` (only while the app's Local API toggle
  is on). Models: `%LOCALAPPDATA%\github.com.thewh1teagle.vibe\`. Diarization file:
  `diar_streaming_sortformer_4spk-v2.q8_0.gguf` (v3.3) or `nemotron-3-diarization-Q8_0.gguf` (later).
  Parakeet/Nemotron transcription models need `vad_model` (`ggml-silero-*.bin`); holtz passes it
  automatically for them.
- Multipart field names are sent quoted (`name="file"`). .NET's default is unquoted, which RFC 7578
  allows and not every parser accepts.

## The Google cross-check

- Google Recorder exports plain text: paragraphs, no timings, speaker labels only in some versions
  and locales. Its own timestamps, if present, are ignored: its clock started on another device.
- Alignment: words normalized the same way on both sides (case, punctuation, digit grouping,
  small number words, a few spellings), anchored on words unique to both stretches (patience-diff
  style), exact LCS on small stretches, then **only matches inside runs of >= 3 consecutive words
  count**. Without that last rule LCS pairs a few words with "okay"/"all right" scattered across
  minutes of the other side; measured on a real recording, it collapsed 689 words' estimated times
  into one minute.
- Projected times: matched words take their Vibe cue's time (interpolated within the cue); others
  are interpolated between matched neighbours, so a long Google-only stretch gets rough times
  (flagged when the span exceeds 30 s). Projected speakers: matched words take the Vibe id;
  unmatched words inherit only when both matched neighbours agree and share the paragraph.
- On a real 30-minute recording the cross-check found both things it exists for: the stretches
  the gate had excluded (present in Google's text), and an invented Vibe passage over silence that
  Google did not have.
