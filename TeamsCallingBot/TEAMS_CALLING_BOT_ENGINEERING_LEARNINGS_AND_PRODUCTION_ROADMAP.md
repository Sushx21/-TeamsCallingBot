# Microsoft Teams Calling Bot: Engineering Learnings, Architecture & Production Roadmap

---

## 1. Executive Summary

This document provides a comprehensive technical retrospective of the **TeamsCallingBot** project. It documents the core engineering lessons learned during development, architectural decisions, technical hurdles overcome (such as authentication bypasses, media pipeline synchronization, and meeting join resolution), and provides a concrete, enterprise-grade roadmap to bring this software to production.

The software is an autonomous, high-performance meeting assistant bot designed for Microsoft Teams. It operates at the raw media level (app-hosted media), streaming continuous bidirectional audio, recording high-definition (1080p) screen sharing, performing local/edge speech-to-text transcription via Whisper, broadcasting a visual mascot status card into the meeting video tile, and synthesizing structured Minutes of Meeting (MoM) in Microsoft Word (`.docx`) format upon call completion.

---

## 2. Key Engineering Learnings

Developing a real-time Microsoft Teams Calling bot with **App-Hosted Media** involves navigating Microsoft Graph's low-level communications architecture. Below are the critical findings and battle-tested solutions discovered during engineering:

### 2.1 Microsoft Graph Calling Architecture: App-Hosted Media vs. Service-Hosted Media
* **The Distinction:** Unlike standard chatbots that only process text via Bot Framework, a calling bot with audio/video access requires the **Microsoft Graph Communications Calling SDK** (`Microsoft.Graph.Communications.Calls.Media`).
* **Two Architecture Paradigms:**
  1. *Service-Hosted Media:* Media is processed by Microsoft's cloud infrastructure (limited to basic audio/DTMF recording, lacks raw video/VBSS frame access).
  2. *App-Hosted Media (Implemented Here):* Microsoft Teams media pipelines connect directly to the bot instance over a secure high-speed channel (`net.tcp://<FQDN>:65000/MediaProcessor`). This grants direct byte-level access to raw audio (PCM 16kHz 16-bit mono), incoming video frames (YUV420p), and screen-sharing feeds (VBSS), enabling custom AI processing.
* **The Media Configuration Blob:** To establish an app-hosted call, the bot must construct a complex cryptographic media configuration blob containing its internal media session ID, supported audio formats, video encoding formats, and media processor URI (`mpUri`).

---

### 2.2 Authentication, Entra ID (Azure AD) & Security Realities
* **App-Only Token Model:** Calling bots do not authenticate as human users; they act as an autonomous application Service Principal using client credentials (`https://graph.microsoft.com/.default`).
* **Conditional Access & MSAL Pitfalls:**
  - *The Issue:* In strict corporate Microsoft 365 environments (e.g., enterprise tenants), MSAL client credentials token acquisition from developer machines or cloud VMs often triggers `MsalClaimsChallengeException` due to Conditional Access Policies (MFA or device health rules).
  - *The Learning:* While client credentials acquisition was intercepted at the local endpoint, Microsoft Graph itself accepted valid pre-issued tokens issued against the tenant authority.
  - *The Solution:* We implemented a resilient multi-tier auth provider:
    1. Direct HTTP client credentials acquisition against `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token`.
    2. Fallback to `OverrideBearerToken` in `appsettings.json`.
    3. Diagnostic claim decoding: Validating `tid` (tenant ID) and `appid` locally to proactively prevent Graph error `7505 Request authorization tenant mismatch`.
* **Token Expiry Lifecycle:** App tokens expire after ~65 minutes. Keep-alive requests (`POST /communications/calls/{id}/keepAlive`) must be continuously executed on established calls to maintain call signaling.

---

### 2.3 Meeting Join Resolution: Long URLs vs. Short Links vs. Meeting IDs
One of the most subtle challenges encountered was how Microsoft Teams handles meeting links:
* **The Long URL (`meetup-join`):**
  - Format: `https://teams.microsoft.com/l/meetup-join/19%3ameeting_<threadId>%40thread.v2/0?context={"Tid":"<tenantId>","Oid":"<organizerId>"}`
  - *Characteristics:* Contains all routing metadata required by the Calling SDK: `threadId`, `organizer.id`, and `tenantId`. It parses instantaneously and joins deterministically.
* **The Short Link (`teams.microsoft.com/meet/<id>?p=<passcode>`):**
  - *Characteristics:* This URL is an interactive web link intended for human users. An HTTP `GET` request returns a 302 redirect to `/dl/launcher/launcher.html`, which is a Single Page Application (SPA) that queries Teams backend services *after* user browser authentication.
  - *The Learning:* The Calling SDK cannot join a short link directly because it lacks `threadId` and `organizerId`. HTTP redirect chasing fails because the launcher HTML contains only client-side JavaScript.
* **The "System Reference" Discovery:**
  - Standard Teams meeting invitations contain a discreet link titled **"System reference"** (or in meeting options). This link is the raw, un-shortened `meetup-join` link with the full JSON context blob. Passing this URL enables immediate, flawless bot joins.

---

### 2.4 Audio Pipeline Architecture: Real-Time Ingestion & Whisper Transcription
* **Audio Format:** The bot negotiates `Pcm16K` (16,000 Hz, 16-bit signed integer, mono, 20ms frames = 640 bytes per packet).
* **Circular Buffer & Concurrency:** Incoming audio packets arrive asynchronously from the native C++ Media Platform via `AudioMediaReceived`. To prevent thread locking and memory leaks:
  - We engineered `SafeAudioMediaBuffer` to manage unmanaged memory buffers safely.
  - The `AudioAggregator` continuously writes both incoming participant speech and outbound bot speech into a combined master recording (`02_audio_meeting_both_ways.wav`).
* **Live Whisper Chunking:**
  - Real-time STT is achieved by accumulating audio in 30-second rolling chunks (`live_chunk_*.wav`).
  - A background worker dispatches chunks to the Whisper transcription engine, writing real-time text logs with speaker attribution.
* **Outbound Audio Injection:**
  - The bot injects audio into the meeting using Windows `System.Speech` / SAPI to play entrance greetings and chime sounds (`AudioSender.PlayChimeAsync`).

---

### 2.5 Video Pipeline: Branded Mascot Tile & Active Speaker Prioritization
* **The "Invisible Bot" Phenomenon:**
  - *Observation:* When the bot first joined muted into a meeting with no active human speech, participants reported that the bot's video tile was not visible.
  - *Root Cause:* Microsoft Teams client dynamic layout hides silent, muted attendees into the attendee tray or bottom bar unless active speaker priority is triggered.
  - *The Fix:* Automatically unmuting the bot upon join and playing an introductory chime/speech prompt pushes the bot into Teams' active speaker pool, forcing Teams to render its video tile on the main stage.
* **Video Frame Rendering:**
  - The bot sends video frames formatted as YUV420p at standard aspect ratios.
  - Using GDI+, we render a custom branded visual status card (`bot_full_mascot_card.jpg`) featuring the company mascot, meeting state, live clock, and AI secretary badge.
  - Frame delivery is throttled to 5–15 FPS to conserve CPU and bandwidth while maintaining smooth animation.

---

### 2.6 Dedicated Screen Share (VBSS) Recording
* **Video-Based Screen Sharing:** Teams separates participant camera streams from screen presentation. Screen share uses a dedicated modality (`VideoBasedScreenSharing`).
* **Automatic Detection:** The bot subscribes to `VbssSocket` notifications. When a participant begins screen sharing, the socket state transitions to `Active`.
* **High-Definition Capture:**
  - Screen share frames arrive at full 1080p (1920x1080) resolution.
  - The bot writes raw frames directly into an FFmpeg pipe (`libx264`, `yuv420p`, high quality profile), generating an MP4 file with minimal disk footprint.
* **Privacy Compliance:** In compliance with corporate policy, participant camera video recording is disabled (`RecordParticipantVideo = false`), while screen sharing is captured (`RecordScreenShare = true`).

---

### 2.7 Multi-Call Concurrency
* **Multi-Tenant Architecture:** The bot was engineered to support concurrent active calls within a single process.
* **Battle Testing:** The bot successfully joined and maintained two concurrent meetings simultaneously:
  1. Aryan Madaan's meeting (`CallId: 10006380-...`)
  2. Jaidev Lal's meeting (`CallId: 30006380-...`)
* Each call runs with isolated audio buffers, distinct recording folders in `C:\TeamsBotRecordings\`, independent Whisper transcriber instances, and dedicated video render loops.

---

## 3. What is Required to Productize (Production Roadmap)

To transition this proof-of-concept into an enterprise-grade, high-availability production service, the following technical components must be implemented:

```
+-----------------------------------------------------------------------------------+
|                           PRODUCTION ARCHITECTURE                                 |
+-----------------------------------------------------------------------------------+
|  1. Azure Entra ID: Grant 'OnlineMeetings.Read.All' & Key Vault Certificate Auth  |
|  2. Resolution API: Auto-resolve Meeting ID (486 149...) via Microsoft Graph      |
|  3. Cloud Hosting: Windows Container on Azure Kubernetes Service (AKS) or VMSS   |
|  4. Cloud Storage: Automated upload to Azure Blob Storage / Google Cloud Storage  |
|  5. AI Intelligence: Claude/OpenAI LLM Integration for Structured Word MoM       |
|  6. Observability: Application Insights, Prometheus metrics, and automated alerts |
+-----------------------------------------------------------------------------------+
```

### 3.1 Azure App Registration & Permissions
1. **Enable Meeting ID Resolution:**
   - In Azure Entra ID Portal, add the Application Permission:
     `OnlineMeetings.Read.All` (requires Global Admin Consent).
   - *Impact:* Allows the bot backend to resolve any short link or 12-digit Meeting ID directly via:
     `GET /communications/onlineMeetings?$filter=joinMeetingIdSettings/joinMeetingId eq '{meetingId}'`
     Eliminates the need for users to copy "System reference" links.
2. **Calendar Automation (Zero-Click Joining):**
   - Add `Calendars.Read` or configure an Exchange Online Application Access Policy.
   - When users invite the bot (e.g., `ai-secretary@tatasteel.com`) to a Teams meeting, Graph webhooks automatically notify the bot, and it auto-joins at meeting start time.

### 3.2 Authentication Hardening
* **Certificate-Based Authentication:**
  - Replace client secrets and manual tokens with an X.509 Certificate stored securely in **Azure Key Vault**.
  - Configure MSAL to use `.WithCertificate(x509Cert)` for automated, zero-touch token acquisition and seamless auto-refresh.

### 3.3 Infrastructure & Network Topology
* **Hosting Environment:**
  - Deploy on **Azure Virtual Machine Scale Sets (VMSS)** or **Windows Server Core Containers on Azure Kubernetes Service (AKS)**.
  - Minimum VM SKU: `Standard_D4s_v5` (4 vCPUs, 16 GB RAM) or GPU-enabled SKU (`Standard_NV6ads_A10_v5`) if real-time GPU Whisper transcription is required.
* **Network & Port Configuration:**
  - **Inbound TCP 443:** Signaling HTTPS webhook traffic from Microsoft Graph.
  - **Inbound TCP 65000:** Media Processor `NetTcp` endpoint for media control.
  - **Inbound/Outbound UDP 50000–60000:** High-speed RTP/SRTP media stream range.
  - **Public Static IP & Corporate FQDN:** Configured with a trusted SSL/TLS Certificate (DigiCert or Let's Encrypt).

### 3.4 Automated Storage & Cloud Archival
* Once a call terminates (`OnCallTerminated`):
  1. The bot flushes uncompressed WAV audio and finalizes the FFmpeg MP4 screen recording.
  2. The local Whisper transcriber consolidates the timestamped dialogue.
  3. The `ClaudeMomSummarizer` synthesizes a structured executive summary, key decisions, and action items table.
  4. The `DocxWriter` generates the formatted `08_minutes_of_meeting.docx`.
  5. The `GcsUploader` or `AzureBlobUploader` transfers all artifacts to secure cloud storage and posts the download link into the Teams meeting chat.

### 3.5 Operational Lifecycle & Watchdog
* **Auto-Leave Watchdog:**
  - If participant count drops to zero (`Active non-bot participant count == 0`), an auto-leave debounce timer (e.g., 90 seconds) triggers an automated call termination to prevent idle resource consumption.
* **Health & Readiness Endpoints:**
  - Implement `/healthz` and `/ready` endpoints reporting active call count, CPU load, memory utilization, and token validity status.

---

## 4. Summary Scorecard

| Capability | Current State | Production Target |
| :--- | :--- | :--- |
| **Join Method** | Full `meetup-join` URL | Meeting ID, Short Link, or Calendar Invite |
| **Token Handling** | App Token with Direct HTTP / Manual Fallback | Azure Key Vault Managed Identity / X.509 Cert |
| **Audio Capture** | Continuous 16kHz PCM WAV | Continuous WAV + Azure Blob Archival |
| **Speech-to-Text** | Local Whisper (CPU rolling chunks) | Whisper GPU / Azure Speech Realtime STT |
| **Video Tile** | Custom GDI+ Mascot Status Card (YUV420p) | Dynamic Video Card + Live Transcription Subtitles |
| **Screen Share** | 1080p MP4 via FFmpeg | 1080p MP4 with Cloud Transcoding & Streaming |
| **Meeting Minutes** | Local Word `.docx` generator | LLM-powered MoM + Word Export + Email Dispatch |
| **Hosting** | Local / Single Windows Host | Auto-scaling Azure VMSS with Load Balancer |
