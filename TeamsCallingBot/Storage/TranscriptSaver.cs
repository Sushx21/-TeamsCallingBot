namespace TeamsCallingBot.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using TeamsCallingBot.Config;

    /// <summary>
    /// Matches the JSON shape PRODUCTION/src/index.js's decryptMOMTranscript() consumers read:
    /// e.timestamp / e.speaker / e.transcript (confirmed by reading those call sites directly).
    /// Property names are lower-case on purpose - JSON.stringify on the JS side produces lower-case
    /// keys, matching this if it's ever fed back into that pipeline later.
    /// </summary>
    public class TranscriptEntry
    {
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("speaker")]
        public string Speaker { get; set; }

        [JsonProperty("transcript")]
        public string Transcript { get; set; }
    }

    /// <summary>
    /// POC MODE (2026-09-03): writes the finished transcript to a LOCAL FOLDER on the VM, PLAIN - no
    /// encryption. Production's AES-256-GCM encryption exists to protect transcripts sitting in a
    /// shared GCS bucket; this POC never touches GCS at all, so that requirement doesn't apply here
    /// and was removed (also drops the BouncyCastle.Cryptography dependency entirely).
    ///
    /// "Saves to Google Drive" is achieved simply by pointing TranscriptOutputFolder AT wherever
    /// Google Drive for Desktop's synced folder lives on the VM - Drive's own desktop client
    /// (installed + signed in interactively over RDP, which works fine since the VM has a full GUI)
    /// uploads anything written there automatically. No API, no service account, no folder-sharing
    /// setup needed. If that setting is left blank, defaults to the VM's own Downloads folder.
    ///
    /// Writes TWO plain files per call: "&lt;callId&gt;.txt" (human-readable - speaker + timestamp per
    /// line, open this one directly) and "&lt;callId&gt;.json" (same {timestamp, speaker, transcript}[]
    /// shape as production's decrypted transcripts, just not encrypted - useful if this ever needs
    /// to feed back into that pipeline later).
    /// </summary>
    public static class TranscriptSaver
    {
        // Downloads is the most discoverable default location on a fresh VM - no config needed to
        // find transcripts even before Drive-sync is set up. Resolved at runtime (not a hardcoded
        // "C:\Users\<name>\..." path) so this works under whatever Windows account actually runs it.
        private static readonly string DefaultLocalOutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TeamsCallingBotTranscripts");

        public static Task UploadAsync(string meetingId, List<TranscriptEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return Task.CompletedTask;
            }

            var outputFolder = BotOptions.Current?.TranscriptOutputFolder;
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                outputFolder = DefaultLocalOutputFolder;
            }

            Directory.CreateDirectory(outputFolder);

            var readableText = new StringBuilder();
            foreach (var entry in entries)
            {
                readableText.AppendLine($"[{entry.Timestamp}] {entry.Speaker}: {entry.Transcript}");
            }

            var txtPath = Path.Combine(outputFolder, $"{meetingId}.txt");
            File.WriteAllText(txtPath, readableText.ToString());

            var jsonPath = Path.Combine(outputFolder, $"{meetingId}.json");
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(entries, Formatting.Indented));

            return Task.CompletedTask;
        }
    }
}
