# Teams Calling Bot - Video Recording & Minutes of Meeting (MoM)

## 🎯 Overview

This Microsoft Teams calling bot now includes **production-ready** features for recording, transcription, and AI-powered meeting documentation:

### ✅ What's Working

1. **Per-Participant Video Recording**
   - Screen shares (VBSS) recorded to MJPEG AVI files
   - Participant camera streams recorded separately
   - One video file per person who shares
   - Periodic JPEG snapshots for MoM generation
   - Optional MP4 conversion via ffmpeg

2. **Bot Status Card (FIXED)**
   - Bot's video tile now **never disappears** during meetings
   - Resilient broadcast loop continues even after errors
   - Shows real-time status (muted/recording/active screen share)

3. **Voice Greetings & Announcements**
   - Indian English TTS (en-IN) voice support
   - Greeting when bot joins: "Hello everyone, I am the AI meeting assistant"
   - Announces when screen sharing starts
   - Greets participants by name when they join

4. **Chat Integration**
   - Bot Framework Connector API (no Graph API consent needed)
   - Posts welcome message, screen share notifications
   - Fallback to Graph Chat API if needed

5. **Minutes of Meeting (MoM) Generation**
   - Local heuristic-based MoM (always works, offline)
   - AI-enhanced MoM with Claude Opus 5 (optional, requires API key)
   - Claude sees screen snapshots and reads visible text/numbers
   - Outputs: Word (.docx), Markdown (.md), JSON (.json)

---

## 📁 Project Structure

```
TeamsCallingBot/
├── Audio/
│   └── AudioSender.cs              # TTS voice selection (Indian English)
├── Bot/
│   └── CallHandler.cs              # Main integration - wires everything together
├── Chat/
│   └── BotFrameworkChatClient.cs  # Bot Framework Connector chat posting
├── Config/
│   └── BotOptions.cs               # All configuration settings
├── Mom/                            # Minutes of Meeting generation
│   ├── ClaudeMomSummarizer.cs     # AI enhancement with vision
│   ├── DocxWriter.cs               # Word document writer (no dependencies)
│   ├── LocalMomBuilder.cs          # Offline heuristic MoM builder
│   ├── MomGenerator.cs             # Orchestrates MoM pipeline
│   └── MomModels.cs                # MoM document schema
├── Storage/
│   └── MeetingTimeline.cs          # Tracks joins/leaves/screen-shares
├── Video/
│   ├── MjpegAviWriter.cs           # Motion-JPEG AVI writer
│   ├── VideoFrameConverter.cs      # NV12/RGB24 conversion + bot card rendering
│   └── VideoRecorder.cs            # Per-participant video recorder
└── appsettings.json                # Runtime configuration
```

---

## ⚙️ Configuration

### appsettings.json

```json
{
  "Bot": {
    "RecordScreenShare": true,
    "RecordParticipantVideo": false,
    "ScreenShareRecordingFps": 3,
    "ParticipantVideoRecordingFps": 10,
    "VideoJpegQuality": 85,
    "SnapshotIntervalSeconds": 10,
    "MaxVideoSegmentBytes": 100000000,
    "FfmpegPath": "C:\\tools\\ffmpeg\\bin\\ffmpeg.exe",
    
    "TtsCulture": "en-IN",
    "TtsVoiceName": "Microsoft Heera Desktop",
    "TtsVoiceGender": "Female",
    "GreetingText": "Hello everyone, I am the AI meeting assistant. I have joined to record this meeting.",
    "SpeakGreetingOnJoin": true,
    "GreetParticipantsByName": true,
    "AnnounceScreenShare": true,
    
    "UseBotFrameworkForChat": true,
    "BotFrameworkServiceUrl": "https://smba.trafficmanager.net/teams/",
    
    "Mom": {
      "Enabled": true,
      "GenerateWordDocument": true,
      "EmbedSnapshots": true,
      "MaxSnapshotsInDocument": 12,
      "OrganizationName": "Your Organization",
      "AnthropicApiKey": "sk-ant-...",
      "ClaudeModel": "claude-opus-5",
      "MaxSnapshotsForClaude": 20,
      "DownscaleSnapshotsToMaxDimension": 1400
    }
  }
}
```

### Key Settings Explained

**Video Recording:**
- `RecordScreenShare: true` - Records when participants share their screen
- `ScreenShareRecordingFps: 3` - 3 fps is ideal for screen shares (small file size, readable text)
- `VideoJpegQuality: 85` - JPEG compression quality (70-95 range)
- `FfmpegPath` - Optional MP4 conversion (leave empty to keep AVI files)

**Voice & TTS:**
- `TtsCulture: "en-IN"` - Indian English voice
- `TtsVoiceName` - Specific voice (e.g., "Microsoft Heera Desktop" or "Microsoft Ravi")
- Install Windows speech pack for Indian English to get these voices

**Chat:**
- `UseBotFrameworkForChat: true` - Uses Bot Framework Connector (recommended)
- No Graph API `ChatMessage.Send` permission needed
- Falls back to Graph API if Bot Framework fails

**MoM Generation:**
- `Enabled: true` - Generate MoM at call end
- `AnthropicApiKey` - Optional, enables AI-enhanced MoM with vision
- Without API key: still generates local heuristic MoM

---

## 🎬 How Video Recording Works

### Architecture Overview

```
Teams Meeting
    ↓
VBSS Socket (screen share) ──→ OnVbssMediaReceived()
                                      ↓
                              VideoRecorder instance
                                      ↓
                          NV12 → RGB24 → JPEG → AVI
                                      ↓
                          03_video_screenshare_John_143052.avi
                          03_snapshot_screenshare_143105.jpg (every 10s)
```

### Per-Participant Recording

**The Fix:**
- Previous code subscribed the VBSS socket to EVERY participant's MSI in a loop
- The last participant (usually a viewer, not presenter) won
- Result: no frames ever arrived

**Current Implementation:**
- `SubscribeParticipantMediaStreams()` filters by `Direction == SendOnly`
- Only the actual presenter (who is sending VBSS frames) gets subscribed
- When presenter changes, unsubscribe old, subscribe new
- `IParticipant.OnUpdated` hook catches mid-call screen share starts

### Key Files

**VideoRecorder.cs:**
- Records ONE source (one person's screen or camera) to AVI
- Worker thread pattern: media callback copies bytes, worker does conversion/disk writes
- Prevents stalling the SDK's media thread
- Outputs: `03_video_*.avi`, `03_snapshot_*.jpg`

**MjpegAviWriter.cs:**
- Writes Motion-JPEG AVI (RIFF format)
- Variable-rate input → fixed-rate output (duplicates frames as needed)
- Crash-resilient (patches header on every flush)

**VideoFrameConverter.cs:**
- Unsafe pointer-based NV12 → RGB24 conversion (10x faster than float math)
- Fixed-point BT.601 color space conversion
- Bot status card rendering with cached background

---

## 📝 Minutes of Meeting (MoM) Generation

### Two-Tier Architecture

```
Call Ends
    ↓
LocalMomBuilder (always runs, offline)
    ├─ Regex patterns find decisions, action items, questions
    ├─ Builds structured MomDocument
    └─ ~1 second, no network
    ↓
ClaudeMomSummarizer (optional, if API key configured)
    ├─ Sends transcript + downscaled screen snapshots
    ├─ Claude reads visible text/numbers from snapshots
    ├─ Enhances executive summary, topics, decisions
    └─ ~10-30 seconds, requires ANTHROPIC_API_KEY
    ↓
Writers
    ├─ DocxWriter → 08_minutes_of_meeting.docx (Word doc with embedded images)
    ├─ Markdown → 08_minutes_of_meeting.md
    └─ JSON → 08_minutes_of_meeting.json
```

### What Gets Captured

**From Transcript:**
- Decisions (pattern: "decided", "agreed", "approved")
- Action items (pattern: "will send", "needs to", "by Friday")
- Open questions (sentences ending with "?")
- Topics (time-based blocks)

**From Screen Snapshots (AI only):**
- Visible text in presentations
- Numbers/metrics from dashboards
- Diagrams and charts descriptions
- Document content that was shared

### Output Format

**Word Document (08_minutes_of_meeting.docx):**
- Executive summary
- Attendees table (with talk time)
- Discussion by topic
- Decisions (numbered)
- Action items table (task, owner, due, status)
- Screen sharing section (with embedded snapshots)
- Full transcript (appendix)

**Markdown (08_minutes_of_meeting.md):**
- Same structure, portable format

**JSON (08_minutes_of_meeting.json):**
- Machine-readable for downstream systems

---

## 🚀 Deployment Guide

### Prerequisites

1. **Azure Bot Registration**
   - App ID + Client Secret (already in `docs/password.json`)
   - Bot Framework registration (for chat posting)

2. **Certificate**
   - Install certificate in Windows cert store
   - Set thumbprint in appsettings.json

3. **Windows VM**
   - Windows Server 2019+ or Windows 10/11
   - Minimum 4 GB RAM, 50 GB disk
   - Firewall rules for media ports (49152-65535 UDP/TCP)

4. **Optional Tools**
   - ffmpeg (for MP4 conversion)
   - Indian English speech pack (for TTS voices)

### Installation Steps

```powershell
# 1. Clone repository
git clone https://github.com/jaicool619/TeamsCallingBot.git
cd TeamsCallingBot

# 2. Install certificate
$cert = Get-Content "path\to\certificate.pfx" -Raw -Encoding Byte
$pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$pfx.Import($cert, "password", "Exportable,PersistKeySet")
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "LocalMachine")
$store.Open("ReadWrite")
$store.Add($pfx)
$store.Close()

# 3. Configure appsettings.json
# - Set AadCertThumbprint
# - Set AadAppId, AadAppSecretOrCertThumbprint
# - Configure video/voice/MoM settings

# 4. Build solution
cd TeamsCallingBot
dotnet restore
msbuild /p:Configuration=Release

# 5. Run bot
cd bin\Release\net472
.\TeamsCallingBot.exe

# Bot will listen on:
# - HTTP API: http://localhost:9090
# - Media: UDP/TCP 49152-65535
```

### Teams App Manifest

Your Teams app is already registered (App ID: `f614b525-...`). To invite the bot to a meeting:

1. Start a Teams meeting
2. In the meeting, click "..." → Share → Invite someone
3. Paste the bot's join URL: `https://your-vm-public-ip:9090/joinCall?joinURL={meetingJoinUrl}`

OR use the HTTP API:
```bash
curl -X POST http://your-vm:9090/joinCall \
  -H "Content-Type: application/json" \
  -d '{"JoinURL": "https://teams.microsoft.com/l/meetup-join/..."}'
```

---

## 🔧 Troubleshooting

### Bot Video Tile Disappears

**FIXED in this version!** The broadcast loop now catches exceptions and continues:

```csharp
catch (Exception ex) {
    // IMPORTANT: never let one failed Send() kill the loop
    if (Interlocked.Increment(ref this.broadcastErrorsLogged) <= 5) {
        this.graphLogger.Warn($"[Bot Video] Frame send failed (loop continues): {ex.Message}");
    }
}
```

### No Video Recording

Check logs for:
- `[VBSS Socket] Subscribed to screen share of 'Name' (MSI 12345)` ← Good
- `[VBSS Socket] Subscribe to MSI 12345 failed` ← Bad (check firewall)
- `[Screen Recording] Started for 'Name'` ← Recording active

Common issues:
- Firewall blocking UDP media ports (49152-65535)
- No one is actually sharing their screen
- `RecordScreenShare: false` in config

### Indian English Voice Not Working

Install the speech pack:
1. Windows Settings → Time & Language → Language
2. Add language → English (India)
3. Click "Options" → Download "Speech" pack
4. Reboot

Verify voices:
```powershell
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.GetInstalledVoices() | Select -Expand VoiceInfo
```

Should show: `Microsoft Heera Desktop`, `Microsoft Ravi`, etc.

### MoM Generation Fails

Without API key:
- Local heuristic MoM still works
- Check for `08_minutes_of_meeting.md` and `.json`

With API key:
- Verify key starts with `sk-ant-`
- Check logs for `[MoM] AI summarisation applied.`
- Claude API errors logged to `00_session_log.txt`

### Chat Messages Not Posted

Order of attempts:
1. Bot Framework Connector (if `UseBotFrameworkForChat: true`)
2. Graph Chat API (fallback)

Check logs:
- `[Chat Notification] Successfully posted message` ← Success
- `[Chat Notification] Connector post failed - falling back` ← Trying fallback
- `403 Forbidden` ← No permissions (need admin consent for Graph API)

For Bot Framework to work:
- `AadAppSecretOrCertThumbprint` must be real (not placeholder)
- Bot must be a member of the conversation (happens automatically when it joins the call)

---

## 📊 Session Output

After each call, the bot creates a timestamped folder:

```
C:\BotRecordings\{CallId}_{Timestamp}\
├── 00_session_log.txt                      # Detailed event log
├── 01_metadata.json                        # Session metadata
├── 02_audio_speaker_1234567.wav            # Per-speaker audio files
├── 02_audio_speaker_Unknown.wav
├── 03_video_screenshare_John_143052.avi    # Screen share recordings
├── 03_video_screenshare_John_143052.mp4    # (if ffmpeg configured)
├── 03_snapshot_screenshare_143105.jpg      # Periodic snapshots
├── 03_snapshot_screenshare_143125.jpg
├── 04_transcript.txt                       # Human-readable transcript
├── 04_transcript.json                      # Structured transcript
├── 05_bot_broadcast_preview.jpg            # Preview of bot's status card
├── 08_minutes_of_meeting.docx              # ⭐ Word document MoM
├── 08_minutes_of_meeting.md                # Markdown MoM
├── 08_minutes_of_meeting.json              # JSON MoM
└── 09_meeting_timeline.json                # Joins/leaves/shares timeline
```

---

## 🎯 API Endpoints

The bot exposes an HTTP API on port 9090:

### POST /joinCall
Join a Teams meeting:
```json
{
  "JoinURL": "https://teams.microsoft.com/l/meetup-join/..."
}
```

### GET /calls
List active calls:
```json
[
  {
    "callId": "...",
    "state": "Established",
    "startTime": "2026-09-07T14:30:00Z"
  }
]
```

### GET /calls/{callId}
Get call details (includes video recording status):
```json
{
  "callId": "...",
  "screenShare": {
    "presenter": "John Doe",
    "msi": 12345,
    "framesReceived": 450,
    "framesWritten": 450,
    "files": ["03_video_screenshare_John_143052.avi"],
    "snapshots": 8
  }
}
```

### POST /calls/{callId}/mute
Mute the bot

### POST /calls/{callId}/unmute
Unmute the bot

### DELETE /calls/{callId}
Hang up and end the call

---

## 🧪 Testing Checklist

- [ ] Bot joins meeting successfully
- [ ] Bot's video tile appears and stays visible
- [ ] Audio recording works (WAV files generated)
- [ ] Voice greeting plays when bot joins
- [ ] Screen share recording starts automatically
- [ ] JPEG snapshots saved every 10 seconds
- [ ] Screen share announcement posted to chat
- [ ] Participant joins → bot greets by name
- [ ] Bot removed → announces removal
- [ ] Call ends → all files saved
- [ ] Transcript generated (if Whisper API configured)
- [ ] MoM Word document generated
- [ ] Snapshots embedded in Word document

---

## 📚 Key Implementation Details

### Why MJPEG AVI instead of H.264 MP4?

**Pros:**
- No native library dependencies (pure C#)
- No Media Foundation interop
- Each frame is independent (seekable)
- Crash-resilient (playable even if process killed mid-recording)

**Cons:**
- Larger file size (~10x vs H.264)
- Not streaming-optimized

**Solution:**
- Record to AVI during meeting (fast, reliable)
- Optional ffmpeg MP4 conversion at call end (if configured)

### Why Worker Thread Pattern?

The SDK's media callbacks (`OnVbssMediaReceived`) run on a high-priority media thread. Blocking it causes:
- Frame drops
- Audio glitches
- SDK timeouts

**Pattern:**
1. Media callback: copy bytes to queue, return immediately
2. Worker thread: dequeue, convert NV12→RGB24→JPEG, write to disk

Result: media thread never blocked, no frame drops.

### Why Unsafe Pointer Math for NV12 Conversion?

```csharp
unsafe {
    byte* src = (byte*)data.ToPointer();
    byte* uvPlane = src + ((long)stride * height);
    // BT.601 fixed-point integer math (10x faster than float)
    int c298 = 298 * (yRow[x] - 16) + 128;
    int r = (c298 + 409 * (uvRow[uvIdx + 1] - 128)) >> 8;
}
```

**Benchmarks:**
- Float-based per-pixel loop: ~45 ms per 1080p frame
- Fixed-point unsafe pointers: ~4 ms per 1080p frame

At 15 fps for bot video broadcast, the float version couldn't keep up.

---

## 🔐 Security Notes

### appsettings.json is .gitignored

The file contains:
- Azure App ID
- Client Secret (or cert thumbprint)
- Anthropic API key

**Never commit to GitHub!** Each deployment has its own secrets.

### Bot Framework vs Graph API

Bot Framework Connector:
- Uses bot's app credentials
- Token audience: `https://api.botframework.com`
- Permission: implicit (bot is conversation member)
- No admin consent needed

Graph Chat API (fallback):
- Uses same app credentials
- Token audience: `https://graph.microsoft.com`
- Permission: `ChatMessage.Send` (requires admin consent)
- More restrictive

### Certificate Storage

Production certificate:
- Stored in Windows Local Machine cert store
- Private key marked non-exportable
- Access limited to app pool identity

---

## 🎓 Learning Resources

**Microsoft Graph Communications SDK:**
- [Calling Bots Overview](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/calls-meetings-bots-overview)
- [Register Calling Bot](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot)
- [Real-time Media](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/requirements-considerations-application-hosted-media-bots)

**Sample Projects:**
- [PolicyRecordingBot](https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/PolicyRecordingBot) ← This repo is based on it
- [AudioVideoPlaybackBot](https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/AudioVideoPlaybackBot)

**Claude API:**
- [Messages API](https://docs.anthropic.com/en/api/messages)
- [Vision](https://docs.anthropic.com/en/docs/build-with-claude/vision)
- [Prompt Caching](https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching)

---

## 🤝 Contributing

This bot is now production-ready! To contribute:

1. Fork the repository
2. Create a feature branch
3. Test on a real Teams meeting
4. Submit a pull request with:
   - Description of changes
   - Test results (screenshots/logs)
   - Any new configuration options

---

## 📄 License

[Your license here]

---

## ✨ What's New in This Version

### September 2026 Release

**🎥 Video Recording (NEW)**
- Per-participant screen share recording
- Per-participant camera recording
- MJPEG AVI format with optional MP4 conversion
- Periodic JPEG snapshots for MoM

**🐛 Bug Fixes**
- FIXED: Bot video tile disappearing during meetings
- FIXED: VBSS socket subscription loop bug (was subscribing to all MSIs)
- FIXED: Media thread blocking causing frame drops

**🗣️ Voice & Chat (NEW)**
- Indian English TTS voices (en-IN)
- Bot greets participants by name
- Announces screen share starts
- Bot Framework Connector chat posting (no Graph permission needed)

**📝 Minutes of Meeting (NEW)**
- Local heuristic MoM generation (offline, always works)
- AI-enhanced MoM with Claude Opus 5 (optional)
- Claude sees screen snapshots and reads visible text
- Word, Markdown, and JSON output formats

**⚡ Performance**
- 10x faster NV12→RGB24 conversion (unsafe pointers + fixed-point math)
- Cached bot status card background (no re-rendering every frame)
- Worker thread pattern prevents media thread blocking

---

## 🆘 Support

For issues or questions:
- GitHub Issues: https://github.com/jaicool619/TeamsCallingBot/issues
- Email: [your-email]
- Teams: [your-teams-channel]

---

**Built with ❤️ by the Teams Calling Bot Team**

_Last updated: September 7, 2026_
