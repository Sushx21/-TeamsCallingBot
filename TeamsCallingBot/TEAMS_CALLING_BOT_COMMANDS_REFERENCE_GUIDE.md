# Teams Calling Bot — Complete Commands & Syntax Reference Guide

This document provides the definitive reference guide for all manual, automated, and REST API commands supported by the **Teams Calling Bot (TSL AI / TDA Bot)**.

---

## 📑 Table of Contents
1. [Overview & Zero-Link Architecture](#1-overview--zero-link-architecture)
2. [Manual In-Meeting Chat Commands (Zero-Link Auto Join)](#2-manual-in-meeting-chat-commands-zero-link-auto-join)
3. [Interactive In-Meeting Chat Commands (Active Session)](#3-interactive-in-meeting-chat-commands-active-session)
4. [Power Automate & Calendar Forwarding Endpoints](#4-power-automate--calendar-forwarding-endpoints)
5. [Direct Management REST APIs](#5-direct-management-rest-apis)
6. [Command Matrix & Cheat Sheet](#6-command-matrix--cheat-sheet)

---

## 1. Overview & Zero-Link Architecture

### How the Bot Receives In-Chat Commands
The bot listens to incoming Teams chat activities via the Bot Framework Messaging endpoint:
```
POST https://teamscallingbot.duckdns.org/api/messages
```

When a user types a command inside any Teams meeting chat:
1. **Zero-Link Auto Extraction**: Teams sends an activity payload containing:
   - `conversation.id` (e.g., `19:meeting_Y2Q3Nm...thread.v2`)
   - `channelData.meeting.id`
   - `from.aadObjectId` (the caller's Entra ID Object ID)
   - `channelData.tenant.id` (the Microsoft 365 Tenant ID)
2. **Instant Call Construction**: The bot extracts these parameters directly from message metadata—**no URL needs to be copied or pasted**.
3. **Automatic Feature Orchestration**: Upon joining, the bot automatically boots:
   - 🎥 **15 FPS Mascot Broadcast Card** (video stream showing bot status)
   - 🎙️ **16kHz Raw PCM Audio Ingestion** & recording
   - 📝 **Real-Time Whisper Speech-to-Text Transcription**
   - 🔔 **Audible Greeting Chime** (promotes the bot to the Teams active stage gallery)
   - 📄 **Automated Word MoM (.docx)** generation and cloud upload upon meeting conclusion.

---

## 2. Manual In-Meeting Chat Commands (Zero-Link Auto Join)

Type any of these commands into the Teams meeting chat to summon the bot.

### A. `#joincall` (Standard Join)
* **Syntax**: `#joincall` or `@TDA #joincall` or `!join` or `/join`
* **Aliases**:
  - `#joincall`
  - `!join`
  - `/join`
  - `join call`
  - `@TDA #joincall`
  - `@TDA !join`
* **What happens**:
  - Bot auto-extracts meeting thread ID and joins immediately.
  - Activates audio capture, live Whisper transcription, mascot card video broadcast, greeting chime, and MoM generation.
  - Screen share recording is disabled by default to optimize bandwidth.

### B. `#joincall #video` (Join with 1080p Screen Share Recording)
* **Syntax**: `#joincall #video` or `!join --video` or `@TDA #joincall #video`
* **Aliases**:
  - `#joincall #video`
  - `#joincall -v`
  - `!join --video`
  - `!join -v`
  - `/join --video`
  - `@TDA #joincall #video`
  - `@TDA !join --video`
* **What happens**:
  - Activates **ALL** features of `#joincall`.
  - **PLUS**: Automatically subscribes to and records all presenter screen shares (VBSS) in full **1080p high definition** as timestamped `.avi` / `.mp4` video files.

### C. Join via Pasted Link or Forwarded Email Invite
* **Syntax**: `#joincall <MeetingLink>` or `!join <MeetingLink>` or pasting a forwarded invite email containing `#joincall`.
* **Example**:
  ```text
  #joincall https://teams.microsoft.com/l/meetup-join/19%3ameeting_...
  ```
* **What happens**:
  - If a specific link is provided in the message text, the bot prioritizes that link over auto-extraction.
  - Useful when inviting the bot from a 1:1 direct chat to join a different remote meeting.

---

## 3. Interactive In-Meeting Chat Commands (Active Session)

Once the bot has joined the meeting, participants can type interactive commands in the meeting chat. The bot replies **both via rich chat messages and audible TTS voice synthesis**:

| Command | Triggers | Bot Action |
|---|---|---|
| **`status`** | `status`, `recording` | Reports recording status for audio, screen share, and AI assistant. |
| **`help`** | `help`, `tda help` | Displays quick command guide and capabilities. |
| **`tda <query>`** | `tda`, `hi bot`, `hello bot`, `hey bot` | Greets caller by name, acknowledges request, and confirms listening mode. |
| **`kaise ho`** | `kaise ho`, `kaisa ho`, `kaisa hai` | Bilingual Hindi/English greeting: *"Namaste [Name]! Main badhiya hoon. How can I assist you today?"* |

---

## 4. Power Automate & Calendar Forwarding Endpoints

For scheduled meetings and production enterprise automation:

### Endpoint: `POST /api/powerautomate/register-meeting`
* **Alias**: `POST /api/meetings/register`
* **URL**: `https://teamscallingbot.duckdns.org/api/powerautomate/register-meeting`
* **Header**: `Content-Type: application/json`

#### Option 1: Structured JSON Payload (from Power Automate trigger)
```json
{
  "meetingJoinUrl": "https://teams.microsoft.com/l/meetup-join/19%3ameeting_Y2Q3Nm...%40thread.v2/0?context=%7b%22Tid%22%3a%22f35425af-4755-4e0c-b1bb-b3cb9f1c6afd%22%7d",
  "meetingSubject": "Q3 Engineering Review",
  "startTime": "2026-09-09T15:00:00Z",
  "organizerEmail": "organizer@company.com",
  "recordVideo": true
}
```

#### Option 2: Raw Forwarded Email Body (Zero parsing required in Power Automate)
```json
{
  "meetingSubject": "FW: Architecture Sync",
  "rawBody": "<html>...Microsoft Teams Meeting<br/>Join on your computer, mobile app or room device<br/><a href='https://teams.microsoft.com/l/meetup-join/...'>Click here to join the meeting</a><br/>Meeting ID: 449 043 891 123<br/>Passcode: aB3dE...</html>",
  "recordVideo": true
}
```

### Endpoint: `GET /api/powerautomate/registered`
* **URL**: `https://teamscallingbot.duckdns.org/api/powerautomate/registered`
* **Method**: `GET`
* **Response**: Returns list of all pending, joining, and active registered meetings:
  ```json
  {
    "totalRegistered": 2,
    "activeBotCalls": 1,
    "meetings": [
      {
        "id": "meeting-1788957800000",
        "subject": "Q3 Engineering Review",
        "startTime": "2026-09-09T15:00:00Z",
        "status": "Pending",
        "recordVideo": true
      }
    ]
  }
  ```

---

## 5. Direct Management REST APIs

### Immediate Call Join: `POST /joinCall`
Directly forces the bot to join a meeting right now:
```bash
curl -k -X POST https://teamscallingbot.duckdns.org/joinCall \
  -H "Content-Type: application/json" \
  -d '{
    "JoinUrl": "https://teams.microsoft.com/l/meetup-join/...",
    "RecordVideo": true
  }'
```

### Command Bot to Leave: `POST /api/management/calls/{callId}/leave`
```bash
curl -k -X POST https://teamscallingbot.duckdns.org/api/management/calls/{callId}/leave
```

---

## 6. Command Matrix & Cheat Sheet

| Use Case | How to Trigger | Notes |
|---|---|---|
| **Quick Manual Join** | Type `#joincall` in Teams meeting chat | Zero link required. Joins with Audio + Mascot Video + Whisper + MoM. |
| **Manual Join + Screen Share** | Type `#joincall #video` in Teams meeting chat | Zero link required. Joins and records 1080p presenter screen share. |
| **Alternative Syntax** | `!join`, `!join --video`, `/join` | Works identically to `#joincall`. |
| **Bot Tag Variant** | `@TDA #joincall` or `@TDA !join --video` | Bot mention is parsed and stripped automatically. |
| **Calendar Forwarding** | Forward meeting invite email to `tsl.ai@...` | Power Automate flow registers the meeting; bot auto-joins on schedule. |
| **Check Bot Health** | Type `status` in Teams chat | Bot responds in chat and speaks via voice. |
| **Say Hello** | Type `kaise ho` or `hi bot` in Teams chat | Interactive greeting via voice and chat. |
