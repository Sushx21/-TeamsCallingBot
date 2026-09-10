namespace TeamsCallingBot.Storage
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Text;
    using Newtonsoft.Json;

    /// <summary>
    /// Manages 100% disk persistence for all meeting assets in ONE unified folder per session:
    /// Saves to C:\TeamsBotRecordings\Session_<SessionId>\ (or Downloads\TeamsBotRecordings\Session_<SessionId>\).
    /// All assets for the 7 requirements are saved in this EXACT same path with clear, distinct filenames:
    /// - 01_logs_bot_activity.txt (Full real-time bot and Graph logs)
    /// - 02_audio_meeting_both_ways.wav (Full mixed audio of both sides)
    /// - 02_audio_speaker_<id>.wav (Individual speaker audio)
    /// - 03_photo_screenshare_<timestamp>.jpg (Screen sharing photos)
    /// - 04_transcript.txt (Human-readable transcript)
    /// - 04_transcript.json (Structured JSON transcript)
    /// - 05_bot_video_broadcast.jpg (Bot video broadcast frame)
    /// - 06_auto_leave_event.txt (Auto leave record)
    /// - 07_chat_removal_notification.txt (Group chat removal notification)
    /// - session_metadata.json (Session metadata)
    /// </summary>
    public class RecordingsManager
    {
        private static string resolvedBaseRoot = null;

        public static string BaseRecordingsRoot
        {
            get
            {
                if (resolvedBaseRoot == null)
                {
                    resolvedBaseRoot = ResolveDefaultDirectory();
                }
                return resolvedBaseRoot;
            }
            set
            {
                resolvedBaseRoot = value;
            }
        }

        public static RecordingsManager Current { get; private set; }

        public string SessionId { get; }
        public string SessionDirectory { get; }
        public string LogFilePath { get; }

        private readonly object fileLock = new object();

        public RecordingsManager(string sessionId, string meetingThreadId = null)
        {
            this.SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId;

            // Folder name = the FULL meeting thread id (e.g. 19:meeting_xxxx@thread.v2) so every
            // artifact (audio, video, transcript, MoM) is unambiguously tied to its meeting - critical
            // once several meetings are recorded concurrently. Falls back to the call/session id when a
            // thread id is not available (e.g. a direct 1:1 call). A short timestamp suffix keeps
            // repeated joins of the SAME meeting in separate, non-overwriting folders.
            var folderLabel = string.IsNullOrWhiteSpace(meetingThreadId) ? this.SessionId : meetingThreadId;
            var cleanLabel = SanitizeForFolderName(folderLabel);
            var folderName = $"{cleanLabel}_{DateTime.Now:yyyyMMdd_HHmmss}";

            this.SessionDirectory = Path.Combine(BaseRecordingsRoot, folderName);
            Directory.CreateDirectory(this.SessionDirectory);

            this.LogFilePath = Path.Combine(this.SessionDirectory, "01_logs_bot_activity.txt");

            Current = this;

            this.Log($"=== Teams Calling Bot Session Started: {this.SessionId} ===");
            this.Log($"Session Directory: {this.SessionDirectory}");
        }

        /// <summary>
        /// Appends a log line to 01_logs_bot_activity.txt in real time.
        /// </summary>
        public void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (this.fileLock)
            {
                try
                {
                    File.AppendAllText(this.LogFilePath, line, Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>
        /// Saves a screen capture or video frame to disk as a high-quality JPEG photo in the session directory.
        /// </summary>
        public string SavePhoto(Bitmap bitmap, string prefix = "03_photo_screenshare")
        {
            if (bitmap == null) return null;

            var filename = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
            var fullPath = Path.Combine(this.SessionDirectory, filename);

            lock (this.fileLock)
            {
                try
                {
                    var jpgEncoder = GetEncoder(ImageFormat.Jpeg);
                    if (jpgEncoder != null)
                    {
                        using (var encParams = new EncoderParameters(1))
                        {
                            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                            bitmap.Save(fullPath, jpgEncoder, encParams);
                        }
                    }
                    else
                    {
                        bitmap.Save(fullPath, ImageFormat.Jpeg);
                    }
                }
                catch (Exception ex)
                {
                    this.Log($"Error saving photo: {ex.Message}");
                }
            }

            this.Log($"[Photo Saved] {fullPath}");
            return fullPath;
        }

        /// <summary>
        /// Saves transcripts in both human-readable text and structured JSON format in the session directory.
        /// </summary>
        public void SaveTranscripts(IEnumerable<TranscriptEntry> entries)
        {
            if (entries == null) return;

            var txtPath = Path.Combine(this.SessionDirectory, "04_transcript.txt");
            var jsonPath = Path.Combine(this.SessionDirectory, "04_transcript.json");

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                sb.AppendLine($"[{entry.Timestamp}] {entry.Speaker}: {entry.Transcript}");
            }

            lock (this.fileLock)
            {
                File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(entries, Formatting.Indented), Encoding.UTF8);
            }

            this.Log($"[Transcripts Saved] Saved to {txtPath} and {jsonPath}");
        }

        /// <summary>
        /// Saves a text record of the auto leave event.
        /// </summary>
        public void SaveAutoLeaveRecord(string reason, int activeHumans)
        {
            var path = Path.Combine(this.SessionDirectory, "06_auto_leave_event.txt");
            var content = $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                          $"Reason: {reason}{Environment.NewLine}" +
                          $"Human Participants Remaining: {activeHumans}{Environment.NewLine}";

            lock (this.fileLock)
            {
                File.WriteAllText(path, content, Encoding.UTF8);
            }

            this.Log($"[Auto Leave Record Saved] {path}");
        }

        /// <summary>
        /// Saves a text record of the group chat removal message.
        /// </summary>
        public void SaveRemovalMessageRecord(string chatThreadId, string messageContent, bool success)
        {
            var path = Path.Combine(this.SessionDirectory, "07_chat_removal_notification.txt");
            var content = $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                          $"ThreadId: {chatThreadId}{Environment.NewLine}" +
                          $"Posted To Group Chat: {success}{Environment.NewLine}" +
                          $"Message: {messageContent}{Environment.NewLine}";

            lock (this.fileLock)
            {
                File.WriteAllText(path, content, Encoding.UTF8);
            }

            this.Log($"[Removal Record Saved] {path}");
        }

        /// <summary>
        /// Saves session metadata (start, end, duration, files).
        /// </summary>
        public void SaveMetadata(object metadata)
        {
            var metaPath = Path.Combine(this.SessionDirectory, "session_metadata.json");
            lock (this.fileLock)
            {
                File.WriteAllText(metaPath, JsonConvert.SerializeObject(metadata, Formatting.Indented), Encoding.UTF8);
            }

            this.Log($"[Session Metadata Saved] {metaPath}");
        }

        /// <summary>
        /// Cleans up heavy raw audio and video recording files (.wav, .mp4, .avi, live_chunk_*.wav)
        /// from the VM disk after GCS upload has completed, keeping the VM disk clean and preventing
        /// disk exhaustion when processing 100 concurrent or 1000 daily meetings.
        /// Preserves 08_minutes_of_meeting.docx, session_metadata.json, and 01_logs_bot_activity.txt.
        /// </summary>
        public int CleanupLocalMediaFiles(bool preserveDocsAndLogs = true)
        {
            if (string.IsNullOrWhiteSpace(this.SessionDirectory) || !Directory.Exists(this.SessionDirectory))
            {
                return 0;
            }

            lock (this.fileLock)
            {
                int deletedCount = 0;
                try
                {
                    long bytesFreed = 0;

                    // Delete audio WAV files
                    var audioFiles = Directory.GetFiles(this.SessionDirectory, "*.wav");
                    foreach (var file in audioFiles)
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            bytesFreed += fi.Length;
                            File.Delete(file);
                            deletedCount++;
                        }
                        catch { }
                    }

                    // Delete video files (.mp4, .avi, temp .raw)
                    var videoPatterns = new[] { "*.mp4", "*.avi", "*.mjpg", "*.raw" };
                    foreach (var pat in videoPatterns)
                    {
                        var videoFiles = Directory.GetFiles(this.SessionDirectory, pat);
                        foreach (var file in videoFiles)
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                bytesFreed += fi.Length;
                                File.Delete(file);
                                deletedCount++;
                            }
                            catch { }
                        }
                    }

                    double mbFreed = bytesFreed / (1024.0 * 1024.0);
                    this.Log($"[VM Disk Cleanup] Deleted {deletedCount} raw media files, freed {mbFreed:F1} MB on VM disk.");
                    Console.WriteLine($">>> [VM Disk Cleanup] Cleaned {deletedCount} media files ({mbFreed:F1} MB freed) in '{Path.GetFileName(this.SessionDirectory)}'");
                    return deletedCount;
                }
                catch (Exception ex)
                {
                    this.Log($"[VM Disk Cleanup Warning] Failed cleaning some files: {ex.Message}");
                    return deletedCount;
                }
            }
        }

        private static string ResolveDefaultDirectory()
        {
            // Priority 0: Configured TranscriptOutputFolder in appsettings.json
            var customFolder = Config.BotOptions.Current?.TranscriptOutputFolder;
            if (!string.IsNullOrWhiteSpace(customFolder))
            {
                try
                {
                    Directory.CreateDirectory(customFolder);
                    return customFolder;
                }
                catch { }
            }

            // Priority 1: C:\TeamsBotRecordings
            try
            {
                var dirC = @"C:\TeamsBotRecordings";
                Directory.CreateDirectory(dirC);
                return dirC;
            }
            catch { }

            // Priority 2: User Downloads\TeamsBotRecordings
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dirDownloads = Path.Combine(userProfile, "Downloads", "TeamsBotRecordings");
                Directory.CreateDirectory(dirDownloads);
                return dirDownloads;
            }
            catch { }

            // Priority 3: D:\Teamsbot\Recordings
            var fallback = @"D:\Teamsbot\Recordings";
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>
        /// Makes a meeting thread id safe to use as a Windows folder name. The thread id contains ':'
        /// (e.g. 19:meeting_...@thread.v2) which is illegal in a path segment; that and any other
        /// invalid characters are replaced with '_'. The full id is otherwise preserved so the meeting
        /// stays identifiable, with a length cap to stay within path limits.
        /// </summary>
        private static string SanitizeForFolderName(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return "unknown";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = label.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ':')
                {
                    chars[i] = '_';
                }
            }

            var cleaned = new string(chars);
            const int maxLen = 130; // keep the whole session path comfortably under the Windows limit
            return cleaned.Length > maxLen ? cleaned.Substring(0, maxLen) : cleaned;
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }
    }
}
