# Video Recording POC — Work Log

This file tracks every change made while addressing the three reported issues.

## Reported issues (2026-09-09)

1. **Video not recorded** — bot only saves screenshots (still images), needs to save actual video.
2. **Transcription quality** — currently Whisper; needs a better model. Optimize code, no errors.
3. **Meeting join (biggest blocker)** — bot did not join via "system reference" (blue face card never appeared); only joined after using the short link + passcode. Need to know where join info comes from when we only fetch calendar details, and how to join concurrent meetings.

## Investigation status

- [x] Video pipeline understood
- [x] Transcription pipeline understood
- [x] Join flow understood

## Findings

### 1. Video (the "only screenshots" issue)
- The branch DOES capture continuous video: MJPEG frames written to `.avi` segments
  (`Video/MjpegAviWriter.cs`, `Video/VideoRecorder.cs`). Frames flow socket →
  `OnFrame` → worker → `MjpegAviWriter.WriteFrame`. Real multi-frame AVI, not stills.
- Periodic JPEG snapshots (`03_snapshot_*`) are ADDITIONAL. The `03_photo_*` path is a
  fallback used only when recording is disabled.
- Root cause of "only screenshots play": `FfmpegPath` defaults to empty
  (`Config/BotOptions.cs:109`), so `ConvertSegmentsToMp4Async` (`VideoRecorder.cs:481`)
  no-ops and output stays `.avi` (MJPEG), which Windows 11 default players can't decode.
  The `.jpg` snapshots open fine → impression that only screenshots were saved.
- Fix = configure/auto-detect ffmpeg so segments transcode to playable H.264 MP4.

### 2. Transcription
- Engine = LOCAL whisper.cpp CLI (`C:\whisper\whisper-cli.exe`, `ggml-small.bin`) —
  the "small" model. Fallbacks: Windows System.Speech, then placeholder text.
  (`Audio/WhisperTranscriber.cs`). No cloud STT anywhere; header comment states an
  explicit "no external STT" constraint.
- Quality levers: bigger model (medium / large-v3) is a drop-in, still local.
- Bugs to fix regardless: (a) pipe-deadlock — stdout/stderr never drained in
  `RunAndWaitAsync` (`WhisperTranscriber.cs:166`) can hang the process; (b) live path
  re-transcribes the whole meeting every 10s (buffers never cleared, O(n^2));
  (c) all lines share one timestamp (`FirstSeenAt`); (d) swallowed exceptions.
- No abstraction: `WhisperTranscriber` is a static class, 2 call sites in CallHandler.

### 3. Join (biggest blocker)
- Bot joins ONLY from a join URL parsed in `Common/JoinInfo.cs`. It needs a
  LONG-format URL containing `?context={tid,oid,messageId}` — that blob is where
  threadId / organizer oid / tenantId come from (`JoinInfo.cs:94-140`, used in
  `Bot.cs:148-162`).
- Short `/meet/<id>?p=<passcode>` links CANNOT be resolved by the bare HttpClient
  (`ResolveShortUrlAsync`) — confirmed non-functional in code comments; that numeric
  id + passcode only resolves inside the authenticated Teams client.
- THERE IS NO CALENDAR FETCH IN THE REPO. Joins are triggered by manually POSTing a
  URL to `api/testjoin` (`Http/JoinCallController.cs`).
- ANSWER to "where do we get this info": Microsoft Graph calendar events expose
  `event.onlineMeeting.joinUrl` — this IS the long-format meetup-join URL WITH the
  context blob. Feed THAT to `JoinInfo.ParseJoinURLAsync`, not the short link.
- Concurrency already works: `ConcurrentDictionary<callId, CallHandler>` + a
  `SemaphoreSlim(MaxConcurrentCalls)` cap in `Bot.cs`.

## Decisions (from user, 2026-09-09)

- Transcription: keep local whisper "small"; **fix bugs only** for now.
- Join: **harden URL parsing only** (manual POST stays); focus on the long-format
  thread-id URL (`.../19:meeting_...@thread.v2/0?context={tid,oid}`).
- Video: **screen share only** + auto-detect ffmpeg → playable MP4.
- Add failure/exception + lifecycle logging so a silent "joined but no face card" is visible.

## Changes made (2026-09-09)

### Video (screen-share only + playable MP4)
- `Config/BotOptions.cs`: `RecordParticipantVideo` default `true` → **`false`** (screen share only).
  Expanded `FfmpegPath` doc to describe auto-detect + why MP4 matters.
- `Video/VideoRecorder.cs`: added `ResolveFfmpegPath()` (checks configured path → PATH →
  common install dirs). `ConvertSegmentsToMp4Async` now auto-detects ffmpeg and logs a clear
  message when it can't be found (explaining .avi won't play in the default Windows player).
- `Bot/CallHandler.cs`: MP4 conversion at call end now ALWAYS attempts (auto-detect handles the
  blank-path case) instead of being gated on a non-empty `FfmpegPath`.

### Transcription (bug fixes, model unchanged)
- `Audio/WhisperTranscriber.cs`: fixed the **pipe deadlock** — `RunAndWaitAsync` now drains
  stdout/stderr (BeginOutput/ErrorReadLine) so whisper-cli can't block on a full pipe, plus a
  10-minute hard timeout that kills a wedged process. This was the main "errors/hangs" risk.
- `Audio/AudioAggregator.cs`: added `SnapshotNewAudioForTranscription()` — returns only the NEW
  audio per speaker since last call (tracked by consumed-byte offset), with the chunk's real
  start timestamp. Stops the O(n^2) whole-meeting re-transcription and the single-timestamp bug.
- `Bot/CallHandler.cs`: live loop now transcribes incremental chunks and APPENDS to the
  persistent transcript (no re-transcribe); `UpdateLiveTranscriptsAsync` rewritten to append +
  log failures instead of swallowing them. Final path transcribes only the remaining tail (no
  duplicate lines, no giant shutdown whisper pass); recording WAVs + talk-time preserved.

### Join hardening + failure logging
- `Common/JoinInfo.cs`: parse failures now throw **diagnostic** messages — short link + passcode
  gets an explicit "cannot be joined, use event.onlineMeeting.joinUrl" message; a non-matching
  URL explains the expected `?context={tid,oid}` long form.
- `Bot/Bot.cs`: `JoinCallAsync` logs the parsed coordinates (threadId / organizerOid / tenantId,
  flagging any MISSING) before joining; wraps parse errors; "Join call requested" reworded to
  make clear the call is only CREATED, not yet joined.
- `Bot/CallHandler.cs`: added a **join watchdog** — if the call doesn't reach `Established`
  within 45s, logs a loud, actionable warning (lobby / bad coordinates / media) so a silent
  join failure is visible. `OnCallUpdated` now logs every state transition + `ResultInfo` codes
  through the structured logger, and logs terminated-with-failure-code as an error.
- `Http/JoinCallController.cs`: now returns clear HTTP responses — 400 (bad/unparseable URL),
  429 (concurrency cap reached), 500 (unexpected), 200 with callId + "created not joined" note.

## Concurrent meetings — how to drive it
- Bot already supports concurrency (`ConcurrentDictionary<callId, CallHandler>` +
  `SemaphoreSlim(MaxConcurrentCalls)`, default **3**).
- To join N meetings: `POST /api/testjoin` **once per meeting**, each with that meeting's
  long-format `onlineMeeting.joinUrl`. Each response returns a distinct `callId`; manage each via
  the callId endpoints. Raise `BotOptions.MaxConcurrentCalls` to allow more simultaneously.

### Per-meeting output folders (named by meeting thread id)
- `Storage/RecordingsManager.cs`: constructor now takes an optional `meetingThreadId`. The output
  folder is named after the FULL thread id (`19:meeting_...@thread.v2`) instead of a generic
  `Session_<timestamp>_<callId>`. Added `SanitizeForFolderName()` (replaces `:` and any
  `Path.GetInvalidFileNameChars()` with `_`, caps length at 130). Falls back to the call/session id
  when no thread id (1:1 calls). A `_yyyyMMdd_HHmmss` suffix keeps repeat joins of the same meeting
  in separate folders.
- `Bot/CallHandler.cs`: passes `this.chatThreadId` into `new RecordingsManager(...)`.
- Effect: every artifact (audio WAVs, per-speaker WAVs, video segments + MP4, transcript .txt/.json,
  timeline, metadata) is written under that one per-meeting folder — because everything routes
  through `RecordingsManager.SessionDirectory`. With N concurrent meetings there are N distinct
  `CallHandler`s → N `RecordingsManager`s → N thread-id-named folders, each self-contained.

## Build
- `dotnet build TeamsCallingBot.sln` → **0 errors**. 3 warnings, all pre-existing/unrelated
  (unused `isKeyFrameNeeded` field; two obsolete GCS credential APIs in `Storage/GcsUploader.cs`).
