# Executive Summary: Why the Teams Bot Failed to Join & How It Was Resolved

## Quick 30-Second Summary

When attempting to have the AI Calling Bot join a Microsoft Teams meeting, three distinct issues caused the bot to either fail authentication, join an empty "ghost meeting", or retain expired credentials:

1. **Token Expiration (60-Minute TTL)**: The Azure AD Client Secret had expired/mismatched (`AADSTS7000215`), forcing fallback to an `OverrideBearerToken`. Entra ID tokens expire after 1 hour. Once expired, Graph rejected call creation.
2. **Base64 Case Sensitivity ("Ghost Room" Trap)**: The Meeting Thread ID in the Teams join URL contains a case-sensitive Base64 string. Modifying or lowercasing any letter causes Microsoft Graph to accept the join (`201 Created`) but place the bot into an isolated, empty synthetic meeting.
3. **In-Memory Config Caching**: Modifying `appsettings.json` while `TeamsCallingBot.exe` is running does *not* update the bot's in-memory singleton. The process had to be terminated and restarted.

---

## Detailed Root Cause Analysis (RCA)

### 1. Azure AD Secret Failure & Bearer Token Expiration
* **The Error**:
  ```text
  AADSTS7000215: Invalid client secret provided.
  Ensure the secret being sent in the request is the client secret value, not the client secret ID.
  ```
* **The Mechanism**:
  The bot uses client credentials to acquire Graph tokens. Because the configured secret was invalid in Azure Portal, the bot relied on `OverrideBearerToken` in `appsettings.json`.
* **The Failure**:
  Microsoft Entra ID Bearer tokens have a strict lifespan of **3,600 seconds (1 hour)**. When the old token expired at `12:48 UTC`, all calls to `https://graph.microsoft.com/v1.0/communications/calls` returned `401 Unauthorized`.
* **The Fix**:
  Acquired a fresh Entra ID token with roles `Calls.JoinGroupCall.All` and `Calls.AccessMedia.All` (valid until `14:00:49 UTC`) and updated `appsettings.json`.

---

### 2. The Meeting URL "Ghost Room" Trap (Base64 Casing)
* **The Error**:
  The bot successfully joined (`201 Created` -> `Established`), but nobody was in the meeting with it (`Active non-bot participant count: 0`).
* **The Mechanism**:
  A Teams meeting URL contains a thread ID formatted like:
  ```text
  19:meeting_ODJmMjlhYzAtYTk2Yy00YzAzLThjM2EtMTQwN2M3MGVhZTQ5@thread.v2
  ```
  The string `ODJmMjlhYzAtYTk2Yy00YzAzLThjM2EtMTQwN2M3MGVhZTQ5` is a **Base64-encoded UTF-8 representation of the meeting GUID**:
  - Encoded: `ODJmMjlh...`
  - Decoded: `82f29ac0-a96c-4c03-8c3a-1407c70eae49` (Valid 128-bit GUID)
* **The Failure**:
  If this thread ID is lowercased (e.g. `odjmmj...`), the Base64 bytes change completely. Teams/Graph does **not** validate whether the thread already exists—it dynamically provisions a brand-new, empty meeting room. The bot was sitting in a parallel ghost room alone.
* **The Fix**:
  Extracted the exact case-sensitive string directly from the Teams client tooltip and validated that Base64 decoding produces the exact canonical hex GUID.

---

### 3. In-Memory Configuration Caching in ASP.NET Core
* **The Error**:
  Updating `appsettings.json` on disk did not change the bot's behavior; it continued trying to join old meetings with the old token.
* **The Mechanism**:
  ASP.NET Core dependency injection binds `IOptions<BotOptions>` once during `Program.Main()` as a singleton. File updates on disk do not mutate existing objects in memory.
* **The Fix**:
  Terminated the running process (`manage_task kill`), synchronized `appsettings.json` into the build output directory (`bin\x64\Release\net472\`), and launched a fresh process instance.

---

### 4. Stage Gallery Suppression ("Waiting for others to join...")
* **The Error**:
  Even after joining, the Teams desktop client displayed "Waiting for others to join..." and hid the bot's video broadcast card.
* **The Mechanism**:
  Teams optimizes bandwidth by suppressing muted participants with no active audio/video energy.
* **The Fix**:
  - Bot automatically starts video broadcasting at 15 FPS (`SendStatus: Active`).
  - Bot initializes its audio sender and real-time speech recognizer.
  - The moment a user speaks one word or unmutes, Teams promotes the bot card to the active stage gallery.

---

## Verification & Confirmation of Success

Once the fixes were applied, the bot confirmed:
1. **Graph API**: `POST /communications/calls` -> `201 Created`
2. **Call Lifecycle**: `CALL STATE CHANGED: Established`
3. **Roster**: `[Participants Updated] Active non-bot participant count: 1` (Jaidev Lal)
4. **Live Audio & STT**: Captured voice audio chunks and transcribed live speech:
   ```text
   [RealtimeSpeechRecognizer] Recognized: "10:00 AM"
   [RealtimeSpeechRecognizer] Recognized: "and Indian the"
   [RealtimeSpeechRecognizer] Recognized: "at its Latin and"
   ```
5. **Screen Share (VBSS)**: Automatically detected screen sharing and began recording high-framerate AVI.

---

## Checklist for Future Meeting Joins

| Step | Action | Verification |
| :--- | :--- | :--- |
| **1. Fresh Token** | Obtain token from Entra ID (`exp` must be in the future). | Check `AUTH DIAGNOSTIC: expires=...` in console log. |
| **2. Exact URL** | Copy Teams join link without lowercasing thread ID. | Decode Base64 part to verify valid 36-char GUID. |
| **3. Kill Old Process** | Terminate old `TeamsCallingBot.exe` before restarting. | `manage_task kill <old-task>` |
| **4. Start Clean** | Launch `TeamsCallingBot.exe` with new config. | Watch for `CALL STATE CHANGED: Established`. |
| **5. Unmute & Speak** | Say one word into the microphone to trigger stage promotion. | Bot mascot card pops onto Teams main stage. |
