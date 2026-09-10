# PROMPT FOR CLAUDE (RUNNING ON TATA STEEL LAPTOP)
## Routing `@AI #joincall` Directly to Windows Calling Bot VM & Preventing Old Playwright VM Interception

Copy and paste the prompt below into Claude on your Tata Steel laptop:

```markdown
# MISSION: Intercept "@AI #joincall" in Teams Chat & Route Directly to Windows Calling Bot VM (Bypassing Old Playwright VM)

We have our production Windows Calling Bot VM active, listening, and verified healthy (.NET 4.7.2 x64 with Microsoft Graph Calling SDK and Skype Media Platform). 

Our Microsoft Teams Bot service (`teamsBot.js` / Azure Bot Framework) running here must intercept the command `@AI #joincall` (and its regex variations) and immediately dispatch the join payload to our Windows Calling Bot VM endpoint instead of allowing legacy regexes to silently route it to the old Playwright browser VM.

---

### 1. EXACT VM ENDPOINT & NETWORK DETAILS
Use either of these verified live endpoints to send the join HTTP POST request:

- **Primary Public FQDN:**
  `https://teamscallingbot.duckdns.org/joinCall`
  *(or `https://teamscallingbot.duckdns.org/api/join`)*

- **Direct Public IP:**
  `https://35.222.145.9/joinCall`
  *(or `https://35.222.145.9/api/join`)*

- **Internal VPC IP (if routing via private cloud network):**
  `https://10.128.0.2:443/joinCall`

*(Note: If using Node.js `axios` or `fetch`, ensure `httpsAgent: new https.Agent({ rejectUnauthorized: false })` is enabled if self-signed/enterprise intermediate certificates are used).*

---

### 2. TARGET CHAT COMMAND TO HANDLE
The bot must listen for:
`@AI #joincall` (case-insensitive, handling `@AI #joincall`, `#joincall`, `@AI #CALL`, and URL encodings like `%23joincall` or extra whitespace)

It must handle all 3 input formats:

#### Case 1: Full System Reference Link
User types:
`@AI #joincall https://teams.microsoft.com/l/meetup-join/19%3ameeting_...%40thread.v2/0?context=%7b%22Tid%22%3a...%7d`
- Action: Extract full URL directly, validate `19:meeting_...thread.v2` presence, and forward immediately in the payload.

#### Case 2: Short / Passcode-Protected Link or Meeting ID
User types:
`@AI #joincall https://teams.microsoft.com/meet/123456789?p=passcode` OR meeting ID `123 456 789` with passcode.
- Action: Pass both URL and meeting credentials (`joinMeetingId`, `joinPasscode`) in payload so the Calling VM's unified link resolver expands it.

#### Case 3: Zero Link (No Link Provided)
User simply types:
`@AI #joincall` directly in the Teams meeting chat.
- Action: Automatically extract the meeting thread ID from `context.activity.conversation.id` or `context.activity.channelData.meeting`.
- Extract the `19:meeting_...thread.v2` ID and resolve the join URL using the Microsoft Graph Calendar/OnlineMeeting API or thread reference.

---

### 3. CRITICAL: PREVENT ROUTING TO THE OLD PLAYWRIGHT VM
In our existing bot router, regex patterns like `\s*call|%23\s*join\s*call|#\s*il1\s*call` or fallback `#call` handlers currently risk capturing `@AI #joincall` and forwarding it to the old browser/Playwright VM.

Please inspect and modify `teamsBot.js` (or your message dispatch pipeline):
1. Place the handler for `@AI #joincall` and `#joincall` at the TOP of your command evaluation chain (BEFORE any legacy `#call` / Playwright handlers).
2. Explicitly filter out this command from the Playwright dispatch queue:
   ```javascript
   const isCallingBotJoin = /(?:@AI\s+)?(?:#|%23)(?:join\s*call|joincall|il1\s*call)/i.test(text);
   if (isCallingBotJoin) {
       // DO NOT send to Playwright VM!
       // Route exclusively to Windows Calling Bot VM:
       await axios.post('https://teamscallingbot.duckdns.org/joinCall', payload, {
           httpsAgent: new https.Agent({ rejectUnauthorized: false })
       });
       return;
   }
   ```

---

### 4. DISPATCH PAYLOAD TO WINDOWS CALLING BOT VM
When `@AI #joincall` is received, send an HTTP POST request to:
`POST https://teamscallingbot.duckdns.org/joinCall`

**Headers:**
`Content-Type: application/json`

**Body:**
```json
{
  "meetingLink": "<RESOLVED_OR_EXTRACTED_TEAMS_MEETING_URL>",
  "meetingId": "<OPTIONAL_19:meeting_...thread.v2_ID>",
  "joinMeetingId": "<OPTIONAL_NUMERIC_MEETING_ID>",
  "joinPasscode": "<OPTIONAL_PASSCODE>",
  "prompt": "TDA SECY",
  "recordVideo": true
}
```

**Teams Chat Immediate Feedback:**
Reply back to the meeting chat:
> *"🤖 **TDA SECY** is joining the meeting now with live video, audio recording, and automated MoM generation..."*

---

### 5. WHAT THE WINDOWS CALLING BOT VM DOES ONCE HIT
As soon as the VM receives this POST request:
1. **Sub-3s Native Join**: Uses Microsoft Graph App-Hosted Media Sockets to join the meeting directly.
2. **Instant Mascot Video Tile**:
   - Pre-warmed 15 FPS NV12 video buffer starts streaming into the meeting video feed immediately.
   - Displays the **Tata Steel Blue Hardhat Robot Waving Mascot**.
   - Bottom status bar shows **`TDA SECY`** with active green status dot and `● ACTIVE & LISTENING | Screen Share & MoM Ready`.
   - Top right shows pulsing red `[REC]` badge.
3. **Media Recording & MoM**:
   - Unmixed 16kHz PCM audio per participant.
   - Real-time Whisper speech-to-text transcription.
   - Synchronized screen sharing video recording (720p/1080p).
   - On `#leave` or call termination, automatically synthesizes structured Minutes of Meeting (MoM) in Word (.docx) format and archives to Google Cloud Storage.

---

### 6. YOUR TASK
Please write or update the exact JavaScript / TypeScript code in `teamsBot.js` to:
1. Match `@AI #joincall` across all variations.
2. Implement the 3 link resolution modes (Full Link, Passcode Link, Auto-Extract Zero Link).
3. Forward to `https://teamscallingbot.duckdns.org/joinCall` while strictly blocking the old Playwright VM.
```
