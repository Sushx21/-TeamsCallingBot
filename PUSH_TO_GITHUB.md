# How this branch got pushed to GitHub (Sushx21)

This records exactly how the `poc-video-transcription-join-fixes` branch was pushed to
**https://github.com/Sushx21/-TeamsCallingBot**, and how to repeat it — because Git Credential
Manager (GCM) fought us the whole way on this machine.

## What went wrong (so you recognise it next time)

- This is a **corporate (Tata Steel) laptop**. GCM is the system credential helper and it kept
  serving/negotiating the **wrong / read-only** GitHub identity.
- Every GCM auth path (browser OAuth, device code, and its Token tab) failed with the same error:
  `fatal: Response status code does not indicate success: 403 (Forbidden)`.
  That error is thrown by **GCM's own .NET HTTP client**, not by git — almost certainly a
  system-level proxy intercepting GCM's API calls. (Plain `git` reaches github.com fine; only GCM's
  .NET layer got the 403s.)
- Symptoms that confirmed it was auth, not the repo:
  - `remote: Permission to Sushx21/-TeamsCallingBot.git denied to Sushx21` = authenticated but the
    credential **lacked `repo` (write) scope**.
  - First token created had **no scopes ticked** → could read, could not push.

## The fix that worked

1. **Create a classic PAT with `repo` scope** (Sushx21 account):
   - https://github.com/settings/tokens → *Generate new token (classic)*
   - Note: `teamsbot`, Expiration: 30 days
   - **Tick the top `repo` checkbox** ("Full control of private repositories") → *Generate token* → copy it.
2. **Bypass GCM for this repo** — use git's plain `store` helper instead:
   ```bash
   git -C "<repo>" config --local credential.helper ""      # clear inherited GCM
   git -C "<repo>" config --local --add credential.helper store
   ```
3. **Store the token and push** (token supplied to git's credential store, not GCM):
   ```bash
   printf "protocol=https\nhost=github.com\nusername=Sushx21\npassword=<PAT>\n\n" | git credential approve
   git push -u mine poc-video-transcription-join-fixes
   ```
   Result:
   ```
   * [new branch]  poc-video-transcription-join-fixes -> poc-video-transcription-join-fixes
   ```

### Equivalent one-liner (if you prefer doing it yourself in the terminal)
Run this in the **VS Code integrated terminal** (token stays in your terminal, never in chat):
```bash
git -C "c:/Users/813037/Documents/TDA R AND D/TeamsCallingBot" \
  push https://Sushx21:<PAT>@github.com/Sushx21/-TeamsCallingBot.git \
  poc-video-transcription-join-fixes
```

## Remotes configured
```
origin  https://github.com/jaicool619/TeamsCallingBot.git   (upstream; needs collaborator invite ACCEPTED to push)
mine    https://github.com/Sushx21/-TeamsCallingBot.git      (your own repo; push works)
```

## Security notes
- `appsettings.json` is git-ignored (`TeamsCallingBot/.gitignore`) and was **verified absent** from
  the pushed tree — the client secret never left the machine.
- If a PAT is ever pasted into a chat/log, treat it as **compromised** and revoke it at
  https://github.com/settings/tokens. The locally stored copy lives in `~/.git-credentials`
  (plaintext) when the `store` helper is used — revoking the token on GitHub makes that copy dead.

## To push to jaicool619 later
1. Accept the collaborator invite: https://github.com/jaicool619/TeamsCallingBot/invitations
2. Then: `git push -u origin poc-video-transcription-join-fixes` (same stored token works, since it's
   scoped to the Sushx21 identity that now has access).
