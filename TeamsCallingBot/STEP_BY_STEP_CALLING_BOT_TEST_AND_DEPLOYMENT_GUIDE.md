# 🚀 Native Teams Calling Bot — Step-by-Step Test & Deployment Guide

> **Production Branch:** `production-code`  
> **Status:** Production-Ready & Tested (0 Build Errors)  
> **Target Architecture:** Microsoft Graph Calling SDK + Real-Time Media Platform (`Calls.Media`) on .NET Framework 4.7.2 x64  

---

## 📑 Table of Contents
1. [System Architecture & Flow](#-system-architecture--flow)
2. [The Two Operating Modes](#-the-two-operating-modes)
3. [Step 1: Start the Calling Bot on the VM](#-step-1-start-the-calling-bot-on-the-vm)
4. [Step 2: Quick 1-Line PowerShell Direct Test](#-step-2-quick-1-line-powershell-direct-test)
5. [Step 3: Microsoft Teams Integration (@TDA #joinv2)](#-step-3-microsoft-teams-integration-tda-joinv2)
6. [Step 4: Real-Time Call Verification](#-step-4-real-time-call-verification)
7. [Step 5: In-Meeting Chat Commands](#-step-5-in-meeting-chat-commands)
8. [Step 6: Output Files & Storage Structure](#-step-6-output-files--storage-structure)
9. [Step 7: Seamless Decommissioning of the Old Bot](#-step-7-seamless-decommissioning-of-the-old-bot)

---

## 🏗️ System Architecture & Flow

```
+-----------------------------------------------------------------------------------+
|                            MICROSOFT TEAMS MEETING                                |
+-----------------------------------------------------------------------------------+
        |                                                           ▲
        | User types:                                               | Direct Media Sockets
        | "@TDA #joinv2"                                            | - Blue Mascot Card (0ms)
        ▼                                                           | - Unmixed PCM16K Audio
+-------------------------------+                                   | - 720p/1080p Screen Rec
|   Node.js Azure Bot Service   |                                   | - Whisper Transcription
|         (teamsBot.js)         |                                   |
+-------------------------------+                                   |
        |                                                           |
        | HTTP POST /api/join                                       |
        | { userAdid, meetingUrl, transcriptfilename, prompt }      |
        ▼                                                           |
+-------------------------------------------------------------------+---------------+
|                     LOCAL C# TEAMS CALLING BOT (VM)                               |
|                  https://<VM_IP_OR_HOSTNAME>:8611/api/join                        |
+-----------------------------------------------------------------------------------+
|  1. Distributed Firestore Lock  -> Prevents dual joins across instances           |
|  2. Graph Calling SDK (AddAsync)-> Places instant UDP media join                  |
|  3. Real-Time Media Engine      -> Streams NV12 card, records audio/video         |
|  4. In-Call Chat Command Engine -> Handles #status, #photo, #speak, #leave        |
|  5. Lifecycle Watchdog          -> Stays until meeting is empty or kicked out     |
|  6. Post-Call Cleanup           -> Uploads MoM & transcript to GCS, cleans disk   |
+-----------------------------------------------------------------------------------+
```

---

## 🎛️ The Two Operating Modes

| Mode | Trigger | Behavior | Deduplication Safeguard |
| :--- | :--- | :--- | :--- |
| **Option 1: Scheduled Auto-Join** | Calendar start time reached (Cron / MeetingRegistry) | Automatically connects to `/api/join` with staggered 1.5s delay | Firestore Lock + In-Memory Thread Lock |
| **Option 2: Manual Fallback Command** | `@TDA #joinv2` in Teams meeting or chat | Instant ad-hoc join within 2–3 seconds | Rejects if already joined; updates video |

---

## 🖥️ Step 1: Start the Calling Bot on the VM

1. Connect to your Windows VM (Local or Cloud).
2. Open PowerShell as Administrator and run:

```powershell
cd c:\Users\jaidevlalgame\Downloads\TeamsCallingBot\TeamsCallingBot\TeamsCallingBot
.\bin\x64\Release\net472\TeamsCallingBot.exe
```

> [!NOTE]
> **Expected Console Output on Startup:**
> ```text
> >>> [Startup] TeamsCallingBot initialized successfully.
> >>> [MediaPlatform] Real-Time Media Platform initialized on port 8445/8611.
> >>> [Listener] Now listening on: http://0.0.0.0:8611 and https://0.0.0.0:8445
> ```

---

## ⚡ Step 2: Quick 1-Line PowerShell Direct Test

*(Fastest verification — test right now without modifying `teamsBot.js`!)*

Open a PowerShell window on the VM (or any machine that can reach the VM) and paste your actual Teams meeting link:

```powershell
$body = @{
    meetingUrl = "PASTE_YOUR_TEAMS_MEETING_LINK_HERE"
    recordVideo = $true
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:8611/api/join" -Method Post -ContentType "application/json" -Body $body
```

> [!TIP]
> **Expected Response:**
> ```json
> {
>   "success": true,
>   "status": "CallCreated",
>   "callId": "37000000-0000-0000-0000-000000000000",
>   "message": "AI Meeting Assistant joined the call successfully."
> }
> ```
> *Check your Teams meeting window — the bot will appear with the **Blue Face Mascot Card**!*

---

## 💬 Step 3: Microsoft Teams Integration (`@TDA #joinv2`)

To trigger the bot directly from Microsoft Teams, add this code block into `teamsBot.js` **right above** your existing `@TDA #joincall` handler:

```javascript
// ============================================================================
// DIFFERENTIATOR COMMAND: @TDA #joinv2 -> Routes to Native C# Calling Bot VM
// ============================================================================
if ((context.activity.text.includes('<at>TDA</at>') || context.activity.text.includes('<at>AIMeetingAssistant</at>')) 
    && (/#\s*(joinv2|callvm|nativecall)\b/i.test(context.activity.text)))
{
  // 1. Point to your Local VM IP or Domain
  const LOCAL_CALLING_VM_URL = process.env.CALLING_BOT_URL || "http://YOUR_LOCAL_VM_IP:8611/api/join";

  let graphAccessToken = await getAccessToken();
  let graphUserProfile = await getUserProfile(graphAccessToken, context.activity.from.aadObjectId);
  let userAdid = graphUserProfile.userPrincipalName.split('@')[0];

  let groupId = context._activity.conversation.id;
  let userObjectId = context._activity.from.aadObjectId;

  const rawTextJoin = context.activity.text || '';
  const adidOverrideMatch = rawTextJoin.match(/adid:([a-zA-Z0-9._-]+)/i);
  const adidOverride = adidOverrideMatch ? adidOverrideMatch[1].toLowerCase() : null;
  if (adidOverride) console.log(`[JOINV2] adid override: ${adidOverride}`);

  let meetingUrlFromText = extractMeetingUrlFromText(rawTextJoin)
      || rawTextJoin.split(/\s+/).find(p => p.startsWith('http'))
      || null;
  if (meetingUrlFromText) meetingUrlFromText = meetingUrlFromText.replace(/\.\.\.+$/, '');

  var meetingUrl = meetingUrlFromText && meetingUrlFromText.startsWith('http')
      ? meetingUrlFromText
      : "https://teams.microsoft.com/l/meetup-join/" + groupId.replace(':','%3a').replace('@','%40') + "/0?context=%7b%22Tid%22%3a%22f35425af-4755-4e0c-b1bb-b3cb9f1c6afd%22%2c%22Oid%22%3a%22" + userObjectId + "%22%7d";

  const vmJoinUrl = meetingUrl;
  const resolvedUrl = await resolveShortUrl(meetingUrl, userAdid, 'joinv2_group');
  let resolvedGroupId = extractAndDecodeMeetingId(resolvedUrl);

  if (!resolvedGroupId) {
      const bqFallback = await fallbackMeetingLinkFromBQ(userAdid, 'JOINV2');
      if (bqFallback) { resolvedGroupId = bqFallback.groupId; meetingUrl = bqFallback.meetingLink; }
  }

  if (!resolvedGroupId) {
      let joincallGraphRescue = await resolveAdhocMeetingViaGraph(vmJoinUrl);
      if (joincallGraphRescue) { resolvedGroupId = joincallGraphRescue.groupId; meetingUrl = joincallGraphRescue.meetingUrl; }
  }

  if (!resolvedGroupId) {
      await context.sendActivity("Could not resolve meeting link. Please type: **@TDA #joinv2 <paste meeting link>**");
      return;
  }

  groupId = resolvedGroupId;
  if (meetingUrl === resolvedUrl || !meetingUrl) meetingUrl = resolvedUrl;

  const occGroupId = occurrenceKeyByDate(groupId);
  const transcriptfilename = occGroupId + ".json";
  const organiserAdid = adidOverride || await resolveOrganiserAdid(meetingUrl, userAdid);
  const joincallMeetingTitle = await tryGetTeamsMeetingTitle(context) || null;

  // Instant confirmation in chat
  await context.sendActivity("Got it — connecting to the Native Calling Bot VM now...");

  try {
      console.log(`[JOINV2] Posting to Local VM at: ${LOCAL_CALLING_VM_URL}`);
      const response = await fetch(LOCAL_CALLING_VM_URL, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
              userAdid: organiserAdid,
              meetingUrl: vmJoinUrl,
              transcriptfilename: transcriptfilename,
              prompt: joincallMeetingTitle ? `Meeting name: ${joincallMeetingTitle}\n\n${summaryPrompt}` : summaryPrompt
          }),
          agent: httpsAgent,
          timeout: 45000
      });

      const data = await response.json();
      console.log('[JOINV2] VM Response:', data);

      if (response.ok && (data.success || data.status === 'CallCreated')) {
          await context.sendActivity(buildJoinSuccessMessage(joincallMeetingTitle));
      } else {
          await context.sendActivity(`Calling Bot response: ${data.message || 'Already in meeting or busy'}`);
      }
  } catch (err) {
      console.error('[JOINV2] Local VM error:', err.message);
      await context.sendActivity(`Failed to reach local VM at ${LOCAL_CALLING_VM_URL}: ${err.message}. Make sure the VM is running.`);
  }

  return;
}
```

---

## 👁️ Step 4: Real-Time Call Verification

Once triggered, observe the following in your Teams meeting:

1. **Blue Face Mascot Card**:
   - The bot joins as an active participant.
   - The bot's video tile displays the **Blue Mascot Face Card** immediately with **0ms latency** (powered by pre-warmed NV12 memory buffers).
2. **Persistence**:
   - The bot will stay in the call until meeting participants leave or an organizer kicks it out.
3. **Audio Capture**:
   - Unmixed 16kHz PCM audio is captured and fed to the Whisper transcription pipeline.
4. **Video Capture**:
   - Any screen share or participant video is recorded directly to disk in `.avi` and `.mp4`.

---

## 🎮 Step 5: In-Meeting Chat Commands

While the bot is inside your meeting, anyone in the meeting chat can interact with it using these commands:

| Command | Action | Example Output in Chat |
| :--- | :--- | :--- |
| `#status` | Displays call duration, participant count, and recording health | `📊 Status: Call ID: 370... \| Participants: 5 \| Audio: Active \| Screen: Ready` |
| `#photo` | Captures an immediate high-res screenshot of screen share | `📸 Snapshot Saved: 03_photo_chat_20260910_083000.jpg` |
| `#speak <msg>` | Makes the bot speak out loud via Text-to-Speech | `🗣️ Spoken: "Welcome everyone to the quarterly review."` |
| `#mute` | Mutes the bot's outgoing audio (continues recording) | `🔇 Bot Audio Muted: Audio output is now muted.` |
| `#unmute` | Restores the bot's audio output | `🔊 Bot Audio Unmuted: Audio output is now enabled.` |
| `#leave` | Gracefully finalizes recordings, generates MoM, and exits | `👋 Leaving Meeting: Finalizing session recordings, transcripts, and minutes...` |

---

## 📂 Step 6: Output Files & Storage Structure

Every meeting is recorded into an isolated session directory under `recordings/<folder_name>/`:

```text
recordings/
└── 19_meeting_YmI0NWE3ZmEtOTBhOC..._20260910_081500/
    ├── 01_logs_bot_activity.txt          # Real-time execution logs & timestamps
    ├── 02_audio_meeting_both_ways.wav    # Master unmixed 16kHz meeting audio
    ├── 03_screenshare_recording.mp4      # High-definition screen share recording
    ├── 03_photo_*.jpg                    # High-res screen share snapshots
    ├── 04_transcript.txt                 # Full plain text meeting transcript
    ├── 04_transcript.json                # Speaker-attributed transcript with timecodes
    ├── Minutes_of_Meeting.docx           # Auto-generated executive summary MoM
    ├── user_adid.txt                     # ADID of meeting organizer
    └── prompt.txt                        # Meeting title & custom summarization prompt
```

> [!IMPORTANT]
> **Automatic GCS Upload & Disk Cleanup:**
> When the call ends, `GcsUploader` automatically uploads the Word document, transcripts, and recordings to Google Cloud Storage, then securely cleans up local video files to keep VM disk usage near zero.

---

## 🔄 Step 7: Seamless Decommissioning of the Old Bot

When you are ready to permanently decommission the legacy Playwright VM:
1. In `teamsBot.js`, change the endpoint URL inside your old `#joincall` function to point to your new VM:
   ```javascript
   let url = "http://YOUR_LOCAL_VM_IP:8611/api/join";
   ```
2. Both `@TDA #joincall` and `@TDA #joinv2` will now hit your native C# calling bot!
3. Power down the old Playwright VM instance — zero downtime, zero user impact.
