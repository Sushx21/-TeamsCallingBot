namespace TeamsCallingBot.Bot
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Timers;
    using Microsoft.Graph;
    using Microsoft.Graph.Communications.Calls;
    using Microsoft.Graph.Communications.Calls.Media;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Graph.Communications.Resources;
    using Microsoft.Skype.Bots.Media;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using TeamsCallingBot.Audio;
    using TeamsCallingBot.Chat;
    using TeamsCallingBot.Common;
    using TeamsCallingBot.Config;
    using TeamsCallingBot.Mom;
    using TeamsCallingBot.Storage;
    using TeamsCallingBot.Video;

    /// <summary>
    /// Complete call lifecycle handler supporting:
    /// 1. Bidirectional Audio (Listen + Speak via AudioSender, muted by default)
    /// 2. Video Screen Share Recording (MJPEG AVI + MP4 + periodic snapshots via VideoRecorder)
    /// 3. Participant Camera Video Recording (MJPEG AVI + MP4 via VideoRecorder)
    /// 4. Disk Storage & Live Transcripts (Audio WAVs, AVI/MP4 videos, Live Transcripts, MoM, Metadata)
    /// 5. Bot Video Broadcast (Streaming status card + visualizations into meeting)
    /// 6. Auto Leave (Leaves when human participants leave)
    /// 7. Group Chat Monitoring & Removal Notification
    /// 8. TDA Assistant Integration & Real-Time Speech Recognition
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        private int endHandled;
        private readonly IGraphLogger graphLogger;
        private readonly DateTime sessionStartTime;

        // Media Sockets
        private readonly IAudioSocket audioSocket;
        private readonly IVideoSocket vbssSocket;
        private readonly IVideoSocket videoSocket;

        // Controllers & Senders
        public AudioSender AudioSender { get; }
        public AudioAggregator AudioAggregator { get; }
        public RecordingsManager RecordingsManager { get; }
        public MeetingTimeline Timeline { get; }

        // State Flags
        public bool IsMuted { get; private set; } = true;
        private volatile bool isVideoSendActive = false;
        private volatile bool isKeyFrameNeeded = true;
        private int joinWelcomeSent = 0;
        private volatile bool callEstablished = false;

        // Options
        private readonly BotOptions options;

        // TDA integration
        private readonly TeamsCallingBot.Tda.TdaClient tdaClient;

        // Visualization
        private readonly object visualizationLock = new object();
        private Bitmap currentVisualization;
        private string currentVisualizationTitle;
        private DateTime visualizationExpiresAt;

        // Fallback photo throttling (only used when video recording is disabled)
        private DateTime lastPhotoTime = DateTime.MinValue;
        private readonly TimeSpan photoInterval = TimeSpan.FromSeconds(5);
        private readonly object photoLock = new object();
        private Bitmap latestScreenBitmap = null;

        // Video recording state (screen share + camera)
        private readonly object videoLock = new object();
        private VideoRecorder vbssRecorder;
        private MediaSessionRecord vbssSession;
        private uint currentVbssMsi;
        private VideoRecorder cameraRecorder;
        private MediaSessionRecord cameraSession;
        private uint currentCameraMsi;
        private int vbssMsiMismatchLogged;
        private readonly HashSet<string> participantsWithUpdateHook = new HashSet<string>();

        // Meeting Chat Polling & Reply Loop
        private CancellationTokenSource chatMonitorCts;
        private readonly HashSet<string> processedChatMsgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bot Video Streaming
        private CancellationTokenSource videoBroadcastCts;
        private readonly object sendLock = new object();
        private volatile VideoFormat preferredSendFormat = VideoFormat.NV12_1280x720_15Fps;
        private volatile string activityLine;
        private int broadcastErrorsLogged;

        // Periodic Audio & Transcript Flush
        private CancellationTokenSource periodicFlushCts;

        // Auto-Leave Timer
        private CancellationTokenSource autoLeaveCts;
        private const int AutoLeaveDebounceSeconds = 120;
        private const int InitialGracePeriodSeconds = 600;

        // Chat Info
        private readonly string chatThreadId;
        private readonly string botAccessToken;
        private readonly BotFrameworkChatClient chatClient;

        // Real-Time Voice Speech Recognition & Live Transcripts
        private readonly RealtimeSpeechRecognizer voiceRecognizer;
        private readonly List<TranscriptEntry> liveTranscriptEntries = new List<TranscriptEntry>();
        private readonly object liveTranscriptLock = new object();

        public ICall Call { get; }

        public CallHandler(ICall call, IGraphLogger logger, string chatThreadId = null, string accessToken = null)
            : base(TimeSpan.FromMinutes(1), logger)
        {
            this.Call = call ?? throw new ArgumentNullException(nameof(call));
            this.graphLogger = logger;
            this.sessionStartTime = DateTime.Now;
            this.chatThreadId = chatThreadId;
            this.options = BotOptions.Current ?? new BotOptions();
            this.botAccessToken = accessToken ?? this.options.OverrideBearerToken;

            // 1. Initialize Disk Storage & Timeline Manager
            this.RecordingsManager = new RecordingsManager(this.Call.Id);
            this.AudioAggregator = new AudioAggregator();
            this.Timeline = new MeetingTimeline { CallId = this.Call.Id, ChatThreadId = chatThreadId, StartedAt = this.sessionStartTime };
            this.chatClient = new BotFrameworkChatClient(this.options.AadAppId, this.options.AadAppSecretOrCertThumbprint, this.options.BotFrameworkServiceUrl, this.graphLogger);

            var tdaTokenProvider = new TeamsCallingBot.Tda.TdaTokenProvider(this.options.Tda, this.options.AadAppId, this.options.AadAppSecretOrCertThumbprint, this.graphLogger);
            this.tdaClient = new TeamsCallingBot.Tda.TdaClient(this.options.Tda, tdaTokenProvider, this.graphLogger);

            // 2. Wire Call Events
            this.Call.OnUpdated += this.OnCallUpdated;
            this.Call.Participants.OnUpdated += this.OnParticipantsUpdated;

            // 3. Resolve Media Sockets
            var localMediaSession = this.Call.GetLocalMediaSession();
            if (localMediaSession != null)
            {
                // Audio Socket
                this.audioSocket = localMediaSession.AudioSocket;
                if (this.audioSocket != null)
                {
                    this.audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;
                    this.AudioSender = new AudioSender(this.audioSocket, this.graphLogger);
                    this.AudioSender.IsMuted = this.IsMuted;

                    // Play greeting only if unmuted
                    if (!this.IsMuted && this.options.SpeakGreetingOnJoin)
                    {
                        _ = Task.Run(async () =>
                        {
                            for (int i = 0; i < 30; i++)
                            {
                                if (this.AudioSender != null && this.AudioSender.IsAudioSendActive)
                                {
                                    break;
                                }
                                await Task.Delay(200).ConfigureAwait(false);
                            }

                            if (this.AudioSender != null && this.AudioSender.IsAudioSendActive && !this.IsMuted)
                            {
                                await this.AudioSender.SpeakAsync("Hello, Teams AI Assistant has joined this call. Audio and screen sharing will be recorded.").ConfigureAwait(false);
                            }
                        });
                    }
                }

                // VBSS Socket (Screen Sharing Video Recording)
                this.vbssSocket = localMediaSession.VideoSockets?.FirstOrDefault(s => s.MediaType == MediaType.Vbss);
                if (this.vbssSocket != null)
                {
                    this.vbssSocket.VideoReceiveStatusChanged += this.OnVbssReceiveStatusChanged;
                    this.vbssSocket.VideoMediaReceived += this.OnVbssMediaReceived;
                    this.graphLogger.Info("[VBSS Socket Initialized] Screen sharing video recording is active.");
                    Console.WriteLine(">>> [VBSS Socket Initialized] Screen sharing video recording is active.");
                }

                // Video Socket (Bot status broadcast + Camera Video Recording)
                this.videoSocket = localMediaSession.VideoSockets?.FirstOrDefault(s => s.MediaType != MediaType.Vbss);
                if (this.videoSocket != null)
                {
                    this.videoSocket.VideoSendStatusChanged += this.OnVideoSendStatusChanged;
                    this.videoSocket.VideoKeyFrameNeeded += this.OnVideoKeyFrameNeeded;
                    this.videoSocket.VideoReceiveStatusChanged += this.OnVideoReceiveStatusChanged;
                    this.videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
                    this.StartBotVideoBroadcast();
                }
            }

            // 4. Start Periodic Audio Flush & Live Transcripts (Every 10 seconds)
            this.periodicFlushCts = new CancellationTokenSource();
            var flushToken = this.periodicFlushCts.Token;
            _ = Task.Run(async () =>
            {
                while (!flushToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), flushToken).ConfigureAwait(false);
                        var snapshotAudios = this.AudioAggregator.SnapshotToWavFiles(this.RecordingsManager.SessionDirectory);
                        await this.UpdateLiveTranscriptsAsync(snapshotAudios).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"Periodic snapshot/transcribe: {ex.Message}");
                    }
                }
            }, flushToken);

            // 5. Start Active Meeting Chat Monitor
            this.StartMeetingChatMonitor();

            // 6. Start Real-Time Voice Speech Recognizer
            this.voiceRecognizer = new RealtimeSpeechRecognizer(
                this.graphLogger,
                () => this.AudioSender != null && this.AudioSender.IsSpeaking);
            this.voiceRecognizer.OnTriggerDetected += this.OnVoiceTriggerDetected;
            this.voiceRecognizer.OnSpeechRecognized += this.OnVoiceSpeechRecognized;

            // Diagnostic: log available auth credentials
            string diagAppId = this.options.AadAppId ?? "(null)";
            string diagSecret = string.IsNullOrWhiteSpace(this.options.AadAppSecretOrCertThumbprint) ? "(empty)" : $"({this.options.AadAppSecretOrCertThumbprint.Length} chars)";
            string diagOverride = string.IsNullOrWhiteSpace(this.options.OverrideBearerToken) ? "(empty)" : $"({this.options.OverrideBearerToken.Length} chars)";
            Console.WriteLine($">>> [Config Diagnostic] AppId={diagAppId}, Secret={diagSecret}, OverrideToken={diagOverride}");

            this.graphLogger.Info($"CallHandler initialized for {this.Call.Id}. Output folder: {this.RecordingsManager.SessionDirectory}");
            Console.WriteLine($">>> [CallHandler] Output folder: {this.RecordingsManager.SessionDirectory}");
        }

        private void Log(string message)
        {
            this.RecordingsManager.Log(message);
        }

        // ===================================================================
        // 1. Audio (Both Ways)
        // ===================================================================
        private void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            try
            {
                var unmixed = e.Buffer.UnmixedAudioBuffers;
                if (unmixed != null && unmixed.Length > 0)
                {
                    foreach (var speakerBuffer in unmixed)
                    {
                        this.AudioAggregator.Append(
                            speakerBuffer.ActiveSpeakerId,
                            speakerBuffer.Data,
                            speakerBuffer.Length,
                            speakerBuffer.OriginalSenderTimestamp);

                        this.voiceRecognizer?.AppendAudio(speakerBuffer.Data, speakerBuffer.Length);
                    }
                }
                else
                {
                    this.AudioAggregator.Append(AudioAggregator.UnknownSpeakerId, e.Buffer.Data, e.Buffer.Length, e.Buffer.Timestamp);
                    this.voiceRecognizer?.AppendAudio(e.Buffer.Data, e.Buffer.Length);
                }
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        // ===================================================================
        // 2. Mute / Unmute & Audio Playback
        // ===================================================================
        public void Mute()
        {
            this.IsMuted = true;
            if (this.AudioSender != null)
            {
                this.AudioSender.IsMuted = true;
            }
            this.graphLogger.Info("Bot muted.");
            Console.WriteLine(">>> [Bot Audio] Muted.");
            this.Timeline.AddEvent("muted", "Bot audio output muted.");
        }

        public async Task MuteAsync()
        {
            this.Mute();
            try
            {
                await this.Call.MuteAsync().ConfigureAwait(false);
                this.graphLogger.Info("[Mute] Bot server-muted successfully.");
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Mute] Server mute returned: {ex.Message}");
            }
        }

        public void Unmute()
        {
            this.IsMuted = false;
            if (this.AudioSender != null)
            {
                this.AudioSender.IsMuted = false;
            }
            this.graphLogger.Info("Bot unmuted.");
            Console.WriteLine(">>> [Bot Audio] Unmuted.");
            this.Timeline.AddEvent("unmuted", "Bot audio output unmuted.");
        }

        public async Task UnmuteAsync()
        {
            this.Unmute();
            try
            {
                await this.Call.UnmuteAsync().ConfigureAwait(false);
                this.graphLogger.Info("[Unmute] Bot server-unmuted successfully.");
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Unmute] Server unmute returned: {ex.Message}");
            }
        }

        public async Task PlayAudioFileAsync(string wavFilePath)
        {
            if (this.AudioSender == null) return;
            this.graphLogger.Info($"[Audio Playback] Playing {wavFilePath} into meeting.");
            await this.AudioSender.PlayWavFileAsync(wavFilePath).ConfigureAwait(false);
        }

        public string CapturePhotoNow(string tag = "manual")
        {
            var recorder = this.vbssRecorder ?? this.cameraRecorder;
            if (recorder != null)
            {
                string path = Path.Combine(this.RecordingsManager.SessionDirectory, $"03_photo_{tag}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
                var saved = recorder.SaveLatestFrame(path);
                if (saved != null)
                {
                    this.RecordingsManager.Log($"[Photo Saved] {saved}");
                    return saved;
                }
            }

            lock (this.photoLock)
            {
                if (this.latestScreenBitmap != null)
                {
                    return this.RecordingsManager.SavePhoto(this.latestScreenBitmap, $"03_photo_{tag}");
                }
            }

            return null;
        }

        // ===================================================================
        // 3. Screen Sharing (VBSS) & Participant Camera Recording
        // ===================================================================
        private VideoRecorderSettings ScreenShareRecorderSettings() => new VideoRecorderSettings
        {
            Fps = Math.Max(1, this.options.ScreenShareRecordingFps),
            JpegQuality = this.options.VideoJpegQuality,
            SnapshotIntervalSeconds = Math.Max(1, this.options.SnapshotIntervalSeconds),
            MaxSegmentBytes = Math.Max(50_000_000, this.options.MaxVideoSegmentBytes),
            SnapshotPrefix = "03_snapshot_screenshare",
        };

        private VideoRecorderSettings CameraRecorderSettings() => new VideoRecorderSettings
        {
            Fps = Math.Max(1, this.options.ParticipantVideoRecordingFps),
            JpegQuality = this.options.VideoJpegQuality,
            SnapshotIntervalSeconds = Math.Max(5, this.options.SnapshotIntervalSeconds * 3),
            MaxSegmentBytes = Math.Max(50_000_000, this.options.MaxVideoSegmentBytes),
            SnapshotPrefix = "03_snapshot_camera",
        };

        private void StartVbssRecorder(uint msi, string presenterName, string participantId)
        {
            lock (this.videoLock)
            {
                if (this.vbssRecorder != null && this.vbssRecorder.MediaSourceId == msi)
                {
                    return;
                }

                this.StopVbssRecorderCore("new presenter");

                string stem = $"03_video_screenshare_{VideoRecorder.SanitizeFileName(presenterName)}_{DateTime.Now:HHmmss}";
                var recorder = new VideoRecorder(this.RecordingsManager.SessionDirectory, stem, msi, presenterName, this.ScreenShareRecorderSettings(), this.RecordingsManager.Log);
                recorder.SnapshotSaved += (r, snap) => this.graphLogger.Info($"[Screen Snapshot Saved] {snap.Path}");
                recorder.Start();

                this.vbssRecorder = recorder;
                this.vbssSession = this.Timeline.StartScreenShare(msi, presenterName, participantId);
                this.activityLine = $"Recording {presenterName}'s screen share";
                Interlocked.Exchange(ref this.vbssMsiMismatchLogged, 0);
            }

            this.Log($"[Screen Recording] Started for '{presenterName}' (MSI {msi}).");
            Console.WriteLine($">>> [Screen Recording] Started recording screen share of '{presenterName}' (MSI {msi}) to AVI video.");

            if (this.options.AnnounceScreenShare)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _ = this.PostTextMessageToChatAsync($"🎥 <b>Recording started:</b> {System.Net.WebUtility.HtmlEncode(presenterName)}'s screen share is now being recorded.");
                        if (this.AudioSender != null && !this.IsMuted)
                        {
                            await this.AudioSender.SpeakAsync($"I have started recording {presenterName}'s screen share.").ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"[Screen Recording] Announcement failed: {ex.Message}");
                    }
                });
            }
        }

        private void StopVbssRecorder(string reason)
        {
            lock (this.videoLock)
            {
                this.StopVbssRecorderCore(reason);
            }
        }

        private void StopVbssRecorderCore(string reason)
        {
            var recorder = this.vbssRecorder;
            if (recorder == null)
            {
                return;
            }

            this.vbssRecorder = null;
            this.activityLine = null;
            try
            {
                recorder.Stop();
                this.Timeline.EndMediaSession(this.vbssSession, recorder.SegmentPaths, recorder.Snapshots.Select(s => s.Path), recorder.FramesWritten);
                this.Log($"[Screen Recording] Stopped for '{recorder.SourceLabel}' ({reason}): {recorder.FramesWritten} frames written from {recorder.FramesReceived} received -> {string.Join(", ", recorder.SegmentPaths.Select(Path.GetFileName))}");
                Console.WriteLine($">>> [Screen Recording] Video saved: {recorder.FramesWritten} frames -> {string.Join(", ", recorder.SegmentPaths.Select(Path.GetFileName))}");
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "[Screen Recording] Error while stopping recorder.");
            }
            finally
            {
                recorder.Dispose();
                this.vbssSession = null;
            }
        }

        private void StartCameraRecorder(uint msi, string participantName, string participantId)
        {
            lock (this.videoLock)
            {
                if (this.cameraRecorder != null && this.cameraRecorder.MediaSourceId == msi)
                {
                    return;
                }

                this.StopCameraRecorderCore("new camera source");

                string stem = $"03_video_camera_{VideoRecorder.SanitizeFileName(participantName)}_{DateTime.Now:HHmmss}";
                var recorder = new VideoRecorder(this.RecordingsManager.SessionDirectory, stem, msi, participantName, this.CameraRecorderSettings(), this.RecordingsManager.Log);
                recorder.Start();
                this.cameraRecorder = recorder;
                this.cameraSession = this.Timeline.StartCameraSession(msi, participantName, participantId);
            }

            this.Log($"[Camera Recording] Started for '{participantName}' (MSI {msi}).");
            Console.WriteLine($">>> [Camera Recording] Started for '{participantName}' (MSI {msi}).");
        }

        private void StopCameraRecorder(string reason)
        {
            lock (this.videoLock)
            {
                this.StopCameraRecorderCore(reason);
            }
        }

        private void StopCameraRecorderCore(string reason)
        {
            var recorder = this.cameraRecorder;
            if (recorder == null)
            {
                return;
            }

            this.cameraRecorder = null;
            try
            {
                recorder.Stop();
                this.Timeline.EndMediaSession(this.cameraSession, recorder.SegmentPaths, recorder.Snapshots.Select(s => s.Path), recorder.FramesWritten);
                this.Log($"[Camera Recording] Stopped for '{recorder.SourceLabel}' ({reason}): {recorder.FramesWritten} frames -> {string.Join(", ", recorder.SegmentPaths.Select(Path.GetFileName))}");
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "[Camera Recording] Error while stopping recorder.");
            }
            finally
            {
                recorder.Dispose();
                this.cameraSession = null;
            }
        }

        private void OnVbssReceiveStatusChanged(object sender, VideoReceiveStatusChangedEventArgs e)
        {
            this.Log($"[VBSS Socket] VideoReceiveStatusChanged: {e.MediaReceiveStatus}");
            Console.WriteLine($">>> [VBSS Socket] VideoReceiveStatus: {e.MediaReceiveStatus}");
            if (e.MediaReceiveStatus == MediaReceiveStatus.Active && this.vbssSocket != null)
            {
                try
                {
                    this.vbssSocket.Subscribe(VideoResolution.HD1080p);
                    this.Log("[VBSS Socket] Subscribed to HD1080p screen share.");
                    Console.WriteLine(">>> [VBSS Socket] Subscribed to HD1080p screen share.");
                }
                catch (Exception ex)
                {
                    this.Log($"[VBSS Socket] Subscribe returned: {ex.Message}");
                }
            }
            else
            {
                this.StopVbssRecorder("receive status Inactive");
            }
        }

        private void OnVbssMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            try
            {
                if (e.Buffer == null || e.Buffer.Data == IntPtr.Zero)
                {
                    return;
                }

                var recorder = this.vbssRecorder;
                if (recorder != null && this.options.RecordScreenShare)
                {
                    if (e.Buffer.MediaSourceId != 0 && recorder.MediaSourceId != 0 && e.Buffer.MediaSourceId != recorder.MediaSourceId
                        && Interlocked.Exchange(ref this.vbssMsiMismatchLogged, 1) == 0)
                    {
                        this.Log($"[VBSS] Frames arrive with MSI {e.Buffer.MediaSourceId} but recorder was started for MSI {recorder.MediaSourceId} - recording them anyway.");
                    }

                    recorder.OnFrame(e.Buffer); // copies bytes into AVI writer, returns immediately
                    return;
                }

                if (recorder == null && this.options.RecordScreenShare)
                {
                    var who = this.ResolveParticipantByMsi(e.Buffer.MediaSourceId);
                    this.StartVbssRecorder(e.Buffer.MediaSourceId, who.DisplayName, who.ParticipantId);
                    this.vbssRecorder?.OnFrame(e.Buffer);
                    return;
                }

                this.SavePhotoThrottled(e.Buffer, "03_photo_screenshare");
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "Error handling screen share frame.");
            }
            finally
            {
                e.Buffer?.Dispose();
            }
        }

        private void OnVideoReceiveStatusChanged(object sender, VideoReceiveStatusChangedEventArgs e)
        {
            this.Log($"[Video Socket] VideoReceiveStatusChanged: {e.MediaReceiveStatus}");
            Console.WriteLine($">>> [Video Socket] VideoReceiveStatus: {e.MediaReceiveStatus}");
            if (e.MediaReceiveStatus == MediaReceiveStatus.Active && this.videoSocket != null)
            {
                try
                {
                    this.videoSocket.Subscribe(VideoResolution.HD720p);
                    this.Log("[Video Socket] Subscribed to HD720p meeting video.");
                    Console.WriteLine(">>> [Video Socket] Subscribed to HD720p meeting video.");
                }
                catch (Exception ex)
                {
                    this.Log($"[Video Socket] Subscribe returned: {ex.Message}");
                }

                this.SubscribeParticipantMediaStreams();
            }
            else
            {
                this.StopCameraRecorder("receive status Inactive");
            }
        }

        private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            try
            {
                if (e.Buffer == null || e.Buffer.Data == IntPtr.Zero)
                {
                    return;
                }

                var recorder = this.cameraRecorder;
                if (recorder != null && this.options.RecordParticipantVideo)
                {
                    recorder.OnFrame(e.Buffer);
                    return;
                }

                if (recorder == null && this.options.RecordParticipantVideo)
                {
                    var who = this.ResolveParticipantByMsi(e.Buffer.MediaSourceId);
                    this.StartCameraRecorder(e.Buffer.MediaSourceId, who.DisplayName, who.ParticipantId);
                    this.cameraRecorder?.OnFrame(e.Buffer);
                    return;
                }

                this.SavePhotoThrottled(e.Buffer, "03_photo_video");
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "Error handling meeting video frame.");
            }
            finally
            {
                e.Buffer?.Dispose();
            }
        }

        private void SavePhotoThrottled(VideoMediaBuffer buffer, string prefix)
        {
            DateTime now = DateTime.Now;
            lock (this.photoLock)
            {
                if (now - this.lastPhotoTime < this.photoInterval)
                {
                    return;
                }

                this.lastPhotoTime = now;
            }

            Bitmap bitmap = null;
            if (buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.NV12)
            {
                bitmap = VideoFrameConverter.ConvertNV12ToBitmap(buffer.Data, buffer.VideoFormat.Width, buffer.VideoFormat.Height, buffer.Stride);
            }
            else if (buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.Rgb24)
            {
                bitmap = VideoFrameConverter.ConvertRGB24ToBitmap(buffer.Data, buffer.VideoFormat.Width, buffer.VideoFormat.Height, buffer.Stride);
            }

            if (bitmap == null)
            {
                return;
            }

            lock (this.photoLock)
            {
                this.latestScreenBitmap?.Dispose();
                this.latestScreenBitmap = (Bitmap)bitmap.Clone();
            }

            var savedPath = this.RecordingsManager.SavePhoto(bitmap, prefix);
            this.graphLogger.Info($"[Photo Saved] {savedPath}");
            bitmap.Dispose();
        }

        private void SubscribeParticipantMediaStreams()
        {
            try
            {
                string botAppId = this.options.AadAppId;
                uint sharerMsi = 0;
                IParticipant sharer = null;
                uint cameraMsi = 0;
                IParticipant cameraOwner = null;

                foreach (var participant in this.Call.Participants)
                {
                    var resource = participant.Resource;
                    if (resource == null || resource.IsInLobby == true)
                    {
                        continue;
                    }

                    if (resource.Info?.Identity?.Application?.Id == botAppId)
                    {
                        continue;
                    }

                    var streams = resource.MediaStreams;
                    if (streams == null)
                    {
                        continue;
                    }

                    foreach (var stream in streams)
                    {
                        if (!uint.TryParse(stream.SourceId, out uint msi) || msi == 0)
                        {
                            continue;
                        }

                        bool sendCapable = stream.Direction == MediaDirection.SendOnly || stream.Direction == MediaDirection.SendReceive;
                        if (!sendCapable)
                        {
                            continue;
                        }

                        if (stream.MediaType == Modality.VideoBasedScreenSharing && sharer == null)
                        {
                            sharer = participant;
                            sharerMsi = msi;
                        }
                        else if (stream.MediaType == Modality.Video && cameraOwner == null)
                        {
                            cameraOwner = participant;
                            cameraMsi = msi;
                        }
                    }
                }

                // ---- Screen share -------------------------------------------------------------
                if (this.vbssSocket != null)
                {
                    if (sharerMsi != 0 && sharerMsi != this.currentVbssMsi)
                    {
                        string name = GetDisplayName(sharer);
                        try
                        {
                            this.vbssSocket.Subscribe(VideoResolution.HD1080p, sharerMsi);
                            this.currentVbssMsi = sharerMsi;
                            this.Log($"[VBSS Socket] Subscribed to screen share of '{name}' (MSI {sharerMsi}).");
                            Console.WriteLine($">>> [VBSS Socket] Subscribed to screen share of '{name}' (MSI {sharerMsi}).");
                        }
                        catch (Exception ex)
                        {
                            this.Log($"[VBSS Socket] Subscribe to MSI {sharerMsi} failed: {ex.Message}");
                        }

                        if (this.options.RecordScreenShare)
                        {
                            this.StartVbssRecorder(sharerMsi, name, sharer?.Id);
                        }
                    }
                    else if (sharerMsi == 0 && this.currentVbssMsi != 0)
                    {
                        this.Log($"[VBSS Socket] Presenter (MSI {this.currentVbssMsi}) stopped sharing - unsubscribing.");
                        try
                        {
                            this.vbssSocket.Unsubscribe();
                        }
                        catch (Exception ex)
                        {
                            this.graphLogger.Warn($"[VBSS Socket] Unsubscribe returned: {ex.Message}");
                        }

                        this.currentVbssMsi = 0;
                        this.StopVbssRecorder("presenter stopped sharing");
                    }
                }

                // ---- Camera video ---------------------------------------------------------------
                if (this.videoSocket != null)
                {
                    if (cameraMsi != 0 && cameraMsi != this.currentCameraMsi)
                    {
                        string name = GetDisplayName(cameraOwner);
                        try
                        {
                            this.videoSocket.Subscribe(VideoResolution.HD720p, cameraMsi);
                            this.currentCameraMsi = cameraMsi;
                            this.Log($"[Video Socket] Subscribed to camera of '{name}' (MSI {cameraMsi}).");
                        }
                        catch (Exception ex)
                        {
                            this.Log($"[Video Socket] Subscribe to MSI {cameraMsi} failed: {ex.Message}");
                        }

                        if (this.options.RecordParticipantVideo)
                        {
                            this.StartCameraRecorder(cameraMsi, name, cameraOwner?.Id);
                        }
                    }
                    else if (cameraMsi == 0 && this.currentCameraMsi != 0)
                    {
                        this.Log($"[Video Socket] Camera (MSI {this.currentCameraMsi}) switched off - unsubscribing.");
                        try
                        {
                            this.videoSocket.Unsubscribe();
                        }
                        catch (Exception ex)
                        {
                            this.graphLogger.Warn($"[Video Socket] Unsubscribe returned: {ex.Message}");
                        }

                        this.currentCameraMsi = 0;
                        this.StopCameraRecorder("camera switched off");
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"Error subscribing participant media streams: {ex.Message}");
            }
        }

        // ===================================================================
        // 4. Live Transcripts & Periodic Updates
        // ===================================================================
        private async Task UpdateLiveTranscriptsAsync(IReadOnlyList<SpeakerAudio> snapshotAudios)
        {
            var combinedEntries = new List<TranscriptEntry>();
            lock (this.liveTranscriptLock)
            {
                combinedEntries.AddRange(this.liveTranscriptEntries);
            }

            if (snapshotAudios != null && snapshotAudios.Count > 0)
            {
                foreach (var audio in snapshotAudios)
                {
                    try
                    {
                        var text = await WhisperTranscriber.TranscribeAsync(audio.WavPath).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var name = this.ResolveSpeakerName(audio.SpeakerId);
                            combinedEntries.Add(new TranscriptEntry
                            {
                                Timestamp = audio.FirstSeenAt.ToString("yyyy-MM-dd HH:mm:ss"),
                                Speaker = name,
                                Transcript = text.Trim()
                            });
                        }
                    }
                    catch { }
                }
            }

            if (combinedEntries.Count > 0)
            {
                this.RecordingsManager.SaveTranscripts(combinedEntries);
            }
        }

        private void OnVoiceSpeechRecognized(string text, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (this.liveTranscriptLock)
            {
                this.liveTranscriptEntries.Add(new TranscriptEntry
                {
                    Timestamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    Speaker = "Speaker (Real-time)",
                    Transcript = text.Trim()
                });

                this.RecordingsManager.SaveTranscripts(new List<TranscriptEntry>(this.liveTranscriptEntries));
            }
        }

        // ===================================================================
        // 5. Bot Video Streaming (Status Card + Visualizations)
        // ===================================================================
        private void OnVideoSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            this.graphLogger.Info($"[Bot Video Streaming] Send status changed: {e.MediaSendStatus}");
            Console.WriteLine($">>> [Bot Video Streaming] SendStatus: {e.MediaSendStatus}");
            this.isVideoSendActive = (e.MediaSendStatus == MediaSendStatus.Active);
            if (this.isVideoSendActive)
            {
                this.preferredSendFormat = VideoFormat.NV12_1280x720_15Fps;
                this.isKeyFrameNeeded = true;
            }
        }

        private void OnVideoKeyFrameNeeded(object sender, VideoKeyFrameNeededEventArgs e)
        {
            this.isKeyFrameNeeded = true;
        }

        private void StartBotVideoBroadcast()
        {
            this.videoBroadcastCts = new CancellationTokenSource();
            var token = this.videoBroadcastCts.Token;

            Task.Run(async () =>
            {
                const int fps = 15;
                const int frameDelayMs = 1000 / fps;
                int tick = 0;
                var pace = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    using (var previewCard = VideoFrameConverter.CreateBotStatusCard(
                        "Teams AI Assistant",
                        this.IsMuted ? "Audio output muted - still recording" : "Recording audio and video",
                        this.Call.Id,
                        this.IsMuted,
                        0,
                        "Starting video feed"))
                    {
                        var previewPath = this.RecordingsManager.SavePhoto(previewCard, "05_bot_broadcast_preview");
                        this.graphLogger.Info($"[Bot Video Streaming] Saved broadcast preview: {previewPath}");
                    }
                }
                catch (Exception ex)
                {
                    this.graphLogger.Warn($"Failed to save preview: {ex.Message}");
                }

                while (!token.IsCancellationRequested)
                {
                    if (this.isVideoSendActive)
                    {
                        this.SendCardFrame(tick++);
                    }

                    long elapsed = pace.ElapsedMilliseconds;
                    int wait = (int)Math.Max(1, frameDelayMs - elapsed);
                    pace.Restart();
                    try
                    {
                        await Task.Delay(wait, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                this.graphLogger.Info("[Bot Video Streaming] Broadcast loop stopped.");
            }, token);

            this.graphLogger.Info("[Bot Video Streaming] Broadcast loop initialized at 15 FPS.");
        }

        private void SendCardFrame(int tick)
        {
            if (!this.isVideoSendActive || this.videoSocket == null)
            {
                return;
            }

            var format = this.preferredSendFormat ?? VideoFormat.NV12_1280x720_15Fps;

            lock (this.sendLock)
            {
                if (!this.isVideoSendActive)
                {
                    return;
                }

                try
                {
                    Bitmap customVis = null;
                    string customTitle = null;
                    lock (this.visualizationLock)
                    {
                        if (this.currentVisualization != null && DateTime.Now < this.visualizationExpiresAt)
                        {
                            customVis = (Bitmap)this.currentVisualization.Clone();
                            customTitle = this.currentVisualizationTitle;
                        }
                    }

                    using (var card = customVis != null
                        ? VideoFrameConverter.CreateVisualizationFrame(
                            customVis,
                            customTitle ?? "TDA Analysis")
                        : VideoFrameConverter.CreateBotStatusCard(
                            "Teams AI Assistant",
                            this.IsMuted ? "Audio output muted - still recording" : "Recording audio and video",
                            this.Call.Id,
                            this.IsMuted,
                            tick,
                            this.activityLine))
                    {
                        customVis?.Dispose();
                        byte[] nv12 = VideoFrameConverter.ConvertBitmapToNV12(card, format.Width, format.Height);

                        using (var videoBuffer = new VideoSendBuffer(nv12, (uint)nv12.Length, format, MediaPlatform.GetCurrentTimestamp()))
                        {
                            this.videoSocket.Send(videoBuffer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref this.broadcastErrorsLogged) <= 5)
                    {
                        this.graphLogger.Warn($"[Bot Video] Send failed: {ex.Message}");
                    }
                }
            }
        }

        // ===================================================================
        // 6. Participants: Roster Tracking & Auto-Leave
        // ===================================================================
        private void OnParticipantsUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            string botAppId = this.options.AadAppId;

            foreach (var added in args.AddedResources)
            {
                bool isBot = added.Resource?.Info?.Identity?.Application?.Id == botAppId;
                string name = GetDisplayName(added);
                this.Timeline.ParticipantJoined(added.Id, name, added.Resource?.Info?.Identity?.User?.Id, isBot);

                if (!isBot && added.Resource?.IsInLobby != true && this.options.GreetParticipantsByName && this.callEstablished
                    && (DateTime.Now - this.sessionStartTime).TotalSeconds > 20)
                {
                    var greetName = name;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(2500).ConfigureAwait(false);
                            if (this.AudioSender != null && !this.IsMuted)
                            {
                                await this.AudioSender.SpeakAsync($"Hi {FirstName(greetName)}, welcome to the meeting.").ConfigureAwait(false);
                            }
                        }
                        catch { }
                    });
                }
            }

            foreach (var removed in args.RemovedResources)
            {
                this.Timeline.ParticipantLeft(removed.Id);
                lock (this.participantsWithUpdateHook)
                {
                    if (this.participantsWithUpdateHook.Remove(removed.Id))
                    {
                        try { removed.OnUpdated -= this.OnParticipantUpdated; } catch { }
                    }
                }
            }

            this.HookParticipantUpdates(args.AddedResources);

            int humanCount = this.Call.Participants.Count(p =>
                p.Resource.IsInLobby == false &&
                (p.Resource.Info?.Identity?.Application == null || p.Resource.Info.Identity.Application.Id != botAppId));

            this.graphLogger.Info($"[Participants Updated] Active non-bot participant count: {humanCount}");
            Console.WriteLine($">>> [Participants Updated] Active non-bot participant count: {humanCount}");

            this.SubscribeParticipantMediaStreams();

            bool inGracePeriod = (DateTime.Now - this.sessionStartTime).TotalSeconds < InitialGracePeriodSeconds;

            if (humanCount == 0 && !inGracePeriod)
            {
                if (this.autoLeaveCts == null || this.autoLeaveCts.IsCancellationRequested)
                {
                    this.autoLeaveCts = new CancellationTokenSource();
                    var token = this.autoLeaveCts.Token;

                    this.graphLogger.Warn($"[Auto Leave] All participants left. Leaving in {AutoLeaveDebounceSeconds}s...");
                    Console.WriteLine($">>> [Auto Leave] All participants left. Bot leaving in {AutoLeaveDebounceSeconds} seconds...");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(AutoLeaveDebounceSeconds), token).ConfigureAwait(false);
                            if (!token.IsCancellationRequested)
                            {
                                Console.WriteLine(">>> [Auto Leave] Hanging up call now.");
                                this.RecordingsManager.SaveAutoLeaveRecord("All human participants left the meeting", 0);
                                await this.Call.DeleteAsync().ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.graphLogger.Error(ex, "[Auto Leave] Failed to hang up.");
                        }
                    }, token);
                }
            }
            else
            {
                if (this.autoLeaveCts != null && !this.autoLeaveCts.IsCancellationRequested)
                {
                    this.graphLogger.Info("[Auto Leave] Cancelled - participants returned or call in grace period.");
                    Console.WriteLine(">>> [Auto Leave] Cancelled - participants active.");
                    this.autoLeaveCts.Cancel();
                    this.autoLeaveCts = null;
                }
            }
        }

        private void HookParticipantUpdates(IEnumerable<IParticipant> participants)
        {
            lock (this.participantsWithUpdateHook)
            {
                foreach (var p in participants)
                {
                    if (p?.Resource?.Info?.Identity?.Application?.Id == this.options.AadAppId) continue;
                    if (this.participantsWithUpdateHook.Add(p.Id))
                    {
                        p.OnUpdated += this.OnParticipantUpdated;
                    }
                }
            }
        }

        private void OnParticipantUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            try
            {
                var streams = args.NewResource?.MediaStreams;
                if (streams != null)
                {
                    var summary = string.Join(", ", streams.Select(s => $"{s.MediaType}:{s.Direction}:{s.SourceId}"));
                    this.graphLogger.Info($"[Participant Updated] {GetDisplayName(sender)} streams -> {summary}");
                }
            }
            catch { }

            this.SubscribeParticipantMediaStreams();
        }

        private static string GetDisplayName(IParticipant participant)
        {
            var identity = participant?.Resource?.Info?.Identity;
            var name = identity?.User?.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = identity?.Application?.DisplayName;
            }

            if (string.IsNullOrWhiteSpace(name) && identity?.AdditionalData != null)
            {
                foreach (var kvp in identity.AdditionalData)
                {
                    if (kvp.Value is Newtonsoft.Json.Linq.JObject obj && obj["displayName"] != null)
                    {
                        name = obj["displayName"]?.ToString();
                        break;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(name) ? "Participant" : name;
        }

        private static string FirstName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "there";
            var parts = displayName.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : displayName;
        }

        private (string ParticipantId, string DisplayName) ResolveParticipantByMsi(uint msi)
        {
            try
            {
                var participant = this.Call.Participants.FirstOrDefault(p =>
                    p.Resource?.MediaStreams != null &&
                    p.Resource.MediaStreams.Any(m => m.SourceId == msi.ToString()));

                if (participant != null)
                {
                    return (participant.Id, GetDisplayName(participant));
                }
            }
            catch { }

            return (null, msi == 0 ? "Presenter" : $"Presenter_MSI{msi}");
        }

        // ===================================================================
        // 7. Call State, Removal Detection & Chat Message Posting
        // ===================================================================
        private void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
        {
            Console.WriteLine($">>> CALL STATE CHANGED: {args.NewResource.State} (call {this.Call.Id})");
            var resultInfo = args.NewResource.ResultInfo;
            if (resultInfo != null)
            {
                Console.WriteLine($">>> CALL RESULT INFO: code={resultInfo.Code}, subcode={resultInfo.Subcode}, message={resultInfo.Message}");
            }

            if (args.NewResource.State == CallState.Established)
            {
                this.callEstablished = true;
                if (Interlocked.Exchange(ref this.joinWelcomeSent, 1) == 0)
                {
                    this.Timeline.AddEvent("call_established", this.Call.Id);
                    _ = Task.Run(() => this.PostTextMessageToChatAsync(
                        "🤖 <b>Teams AI Assistant</b> has joined the meeting.<br/>• Audio recording &amp; per-speaker capture: <b>Active</b><br/>• Screen share video recording: <b>Ready</b> (starts automatically when someone shares)<br/>• Bot video status tile: <b>Active</b>"));
                }

                try
                {
                    this.vbssSocket?.Subscribe(VideoResolution.HD1080p);
                    this.videoSocket?.Subscribe(VideoResolution.HD720p);
                }
                catch { }

                this.SubscribeParticipantMediaStreams();
            }

            if (args.NewResource.State != CallState.Terminated)
            {
                return;
            }

            if (Interlocked.Exchange(ref this.endHandled, 1) == 1)
            {
                return;
            }

            bool wasRemovedByUser = false;
            if (resultInfo != null)
            {
                string msg = resultInfo.Message?.ToLowerInvariant() ?? "";
                if (resultInfo.Subcode == 7002 || resultInfo.Subcode == 702 || resultInfo.Subcode == 5000 || resultInfo.Code == 403 ||
                    msg.Contains("removed") || msg.Contains("kicked") || msg.Contains("ejected") || msg.Contains("organizer"))
                {
                    wasRemovedByUser = true;
                }
            }

            if (wasRemovedByUser)
            {
                Console.WriteLine(">>> BOT REMOVAL DETECTED: Announcing verbally and posting notification to group chat...");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (this.AudioSender != null)
                        {
                            await this.AudioSender.SpeakAsync("Teams AI Assistant has been removed from this meeting.").ConfigureAwait(false);
                        }
                    }
                    catch { }
                });

                _ = Task.Run(() => this.PostTextMessageToChatAsync(
                    "⚠️ <b>Teams Calling Bot Notification:</b> The bot was removed from this meeting by a participant or organizer.",
                    isRemovalNotification: true));
            }
            else
            {
                Console.WriteLine(">>> CALL TERMINATION / BOT LEAVING: Posting group chat departure notification...");
                _ = Task.Run(() => this.PostTextMessageToChatAsync(
                    "👋 <b>Teams Calling Bot Notification:</b> The bot has left the meeting. All session audio, screen recordings, and transcripts have been archived.",
                    isRemovalNotification: true));
            }

            _ = this.HandleCallEndedAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    this.graphLogger.Error(t.Exception, $"Failed during wind-down for call {this.Call.Id}.");
                }
            });
        }

        public string GetEffectiveChatThreadId()
        {
            if (!string.IsNullOrWhiteSpace(this.chatThreadId))
            {
                return this.chatThreadId;
            }

            try
            {
                var threadId = this.Call.Resource?.ChatInfo?.ThreadId;
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    return threadId;
                }
            }
            catch { }

            return null;
        }

        public string GetEffectiveAccessToken()
        {
            if (!string.IsNullOrWhiteSpace(this.botAccessToken))
            {
                return this.botAccessToken;
            }

            return BotOptions.Current?.OverrideBearerToken;
        }

        public async Task<bool> PostTextMessageToChatAsync(string htmlContent, bool isRemovalNotification = false)
        {
            string threadId = this.GetEffectiveChatThreadId();
            string token = this.GetEffectiveAccessToken();

            if (string.IsNullOrWhiteSpace(threadId))
            {
                this.graphLogger.Warn("[Chat Notification] No ChatThreadId found on call. Cannot post to chat.");
                Console.WriteLine(">>> [Chat Notification] No ChatThreadId found. Cannot post to chat.");
                return false;
            }

            try
            {
                if (this.options.UseBotFrameworkForChat && this.chatClient != null)
                {
                    try
                    {
                        bool connectorPosted = await this.chatClient.SendMessageAsync(threadId, htmlContent).ConfigureAwait(false);
                        if (connectorPosted)
                        {
                            this.graphLogger.Info($"[Chat Notification] Successfully posted message to chat via Bot Framework Connector.");
                            Console.WriteLine($">>> [Chat Notification] Posted message to meeting chat via Connector.");
                            if (isRemovalNotification)
                            {
                                this.RecordingsManager.SaveRemovalMessageRecord(threadId, htmlContent, true);
                            }
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"[Chat Notification] Connector post failed: {ex.Message} - falling back to Graph API");
                    }
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    this.graphLogger.Warn("[Chat Notification] No access token available to call Graph chat API.");
                    return false;
                }

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var payload = new
                    {
                        body = new
                        {
                            contentType = "html",
                            content = htmlContent
                        }
                    };

                    var json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(
                        $"https://graph.microsoft.com/v1.0/chats/{threadId}/messages",
                        content).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        this.graphLogger.Info($"[Chat Notification] Successfully posted message to chat thread {threadId}");
                        Console.WriteLine($">>> [Chat Notification] Posted message to meeting chat ({threadId}).");
                        if (isRemovalNotification)
                        {
                            this.RecordingsManager.SaveRemovalMessageRecord(threadId, htmlContent, true);
                        }
                        return true;
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        this.graphLogger.Warn($"[Chat Notification] Failed to post chat message ({response.StatusCode}): {error}");
                        Console.WriteLine($">>> [Chat Notification] Failed to post message ({response.StatusCode}): {error}");
                        if (isRemovalNotification)
                        {
                            this.RecordingsManager.SaveRemovalMessageRecord(threadId, htmlContent, false);
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "[Chat Notification] Exception posting to group chat.");
                Console.WriteLine($">>> [Chat Notification] Exception: {ex.Message}");
                if (isRemovalNotification)
                {
                    this.RecordingsManager.SaveRemovalMessageRecord(threadId, $"Exception: {ex.Message}", false);
                }
                return false;
            }
        }

        // ===================================================================
        // 8. Meeting Chat Monitor & Trigger Responses
        // ===================================================================
        private void StartMeetingChatMonitor()
        {
            this.chatMonitorCts = new CancellationTokenSource();
            var token = this.chatMonitorCts.Token;

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await this.PollAndProcessNewChatMessagesAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"[Chat Monitor] Polling error: {ex.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
                }
            }, token);
        }

        private async Task PollAndProcessNewChatMessagesAsync()
        {
            string threadId = this.GetEffectiveChatThreadId();
            string token = this.GetEffectiveAccessToken();
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(token)) return;

            string botAppId = BotOptions.Current?.AadAppId;

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await http.GetAsync($"https://graph.microsoft.com/v1.0/chats/{threadId}/messages?$top=10&$orderby=createdDateTime desc").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jObj = JObject.Parse(body);
                var messages = jObj["value"] as JArray;
                if (messages == null) return;

                foreach (var msg in messages)
                {
                    string msgId = msg["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(msgId) || this.processedChatMsgIds.Contains(msgId)) continue;

                    this.processedChatMsgIds.Add(msgId);

                    string senderAppId = msg["from"]?["application"]?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(senderAppId) && string.Equals(senderAppId, botAppId, StringComparison.OrdinalIgnoreCase)) continue;

                    string content = msg["body"]?["content"]?.ToString() ?? "";
                    string senderName = msg["from"]?["user"]?["displayName"]?.ToString() ?? "Participant";

                    await this.HandleIncomingChatMessageAsync(senderName, content).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleIncomingChatMessageAsync(string senderName, string htmlContent)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(htmlContent, "<.*?>", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            string lower = text.ToLowerInvariant();
            this.graphLogger.Info($"[Chat Monitor] Message from '{senderName}': {text}");
            Console.WriteLine($">>> [Chat Monitor] '{senderName}': {text}");

            bool isTrigger = lower.Contains("tda bot") || lower.Contains("tdabot") || lower.Contains("tda") ||
                             lower.Contains("kaise ho") || lower.Contains("kaisa ho") || lower.Contains("kaisa hai") ||
                             lower.Contains("hi bot") || lower.Contains("hello bot") || lower.Contains("hey bot") ||
                             lower.Contains("status") || lower.Contains("help") || lower.Contains("recording");

            if (!isTrigger) return;

            string replyHtml;
            string spokenReply;

            if (lower.Contains("kaise ho") || lower.Contains("kaisa ho") || lower.Contains("kaisa hai"))
            {
                replyHtml = $"🙏 <b>Namaste {System.Net.WebUtility.HtmlEncode(senderName)}!</b> Main badhiya hoon (I am doing well!). Main TDA Bot hoon, aapka AI meeting assistant. Aaj main aapki kya madad kar sakta hoon?";
                spokenReply = $"Namaste {FirstName(senderName)}! Main badhiya hoon. How can I help you today?";
            }
            else if (lower.Contains("status") || lower.Contains("recording"))
            {
                replyHtml = $"📊 <b>Teams Calling Bot Status:</b><br/>• 🎙️ Audio Recording: <b>Active</b><br/>• 📺 Screen Share Video Recording: <b>Active</b><br/>• 🤖 AI Assistant: <b>Online</b>";
                spokenReply = "TDA Bot is active and recording audio and screen share.";
            }
            else if (lower.Contains("help"))
            {
                replyHtml = $"🤖 <b>TDA Bot Help:</b><br/>• Type <code>kaise ho</code> or <code>status</code> for greetings and status.<br/>• Ask TDA any question in chat or voice: <code>tda &lt;query&gt;</code>.<br/>• Audio, screen share video, and transcripts are automatically saved.";
                spokenReply = "I am your AI meeting assistant. You can ask me questions or check recording status.";
            }
            else
            {
                replyHtml = $"👋 <b>Hello {System.Net.WebUtility.HtmlEncode(senderName)}!</b> TDA Bot here. How can I assist you in this meeting?";
                spokenReply = $"Hello {FirstName(senderName)}, I am listening. How can I help you?";
            }

            _ = this.PostTextMessageToChatAsync(replyHtml);

            if (this.AudioSender != null && !this.IsMuted)
            {
                await this.AudioSender.SpeakAsync(spokenReply).ConfigureAwait(false);
            }
        }

        public async Task HandleIncomingChatActivity(string fromName, string fromId, string plainText, string serviceUrl)
        {
            await this.HandleIncomingChatMessageAsync(fromName, plainText).ConfigureAwait(false);
        }

        private void OnVoiceTriggerDetected(string recognizedText, string voiceReply, string chatReply)
        {
            this.graphLogger.Info($"[Voice Trigger] Detected: '{recognizedText}'");
            Console.WriteLine($">>> [Voice Trigger] Trigger activated by heard phrase: '{recognizedText}'");

            _ = this.PostTextMessageToChatAsync(chatReply);

            if (this.AudioSender != null && !this.IsMuted && !string.IsNullOrWhiteSpace(voiceReply))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await this.AudioSender.SpeakAsync(voiceReply).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"[Voice Trigger] TTS speech error: {ex.Message}");
                    }
                });
            }
        }

        // ===================================================================
        // 9. Wind-down: Video Finalization, Audio Flush, Transcripts, MoM, Metadata
        // ===================================================================
        private async Task HandleCallEndedAsync()
        {
            this.graphLogger.Info($"Call {this.Call.Id} terminated - saving all audio, video, transcripts and metadata.");

            // 0. Finalise video files first (fast; makes the AVIs playable even if a later step fails)
            this.StopVbssRecorder("call ended");
            this.StopCameraRecorder("call ended");
            this.Timeline.EndedAt = DateTime.Now;

            // 1. Flush Audio to the session folder
            var speakerAudios = this.AudioAggregator.FlushToWavFiles(this.RecordingsManager.SessionDirectory);

            // 2. Transcribe
            var entries = new List<TranscriptEntry>();
            lock (this.liveTranscriptLock)
            {
                entries.AddRange(this.liveTranscriptEntries);
            }

            if (speakerAudios.Count > 0)
            {
                foreach (var speakerAudio in speakerAudios)
                {
                    var speakerName = this.ResolveSpeakerName(speakerAudio.SpeakerId);
                    var text = await WhisperTranscriber.TranscribeAsync(speakerAudio.WavPath).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    entries.Add(new TranscriptEntry
                    {
                        Timestamp = speakerAudio.FirstSeenAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        Speaker = speakerName,
                        Transcript = text.Trim(),
                    });
                }
            }

            // 3. Save Transcripts (.txt and .json)
            if (entries.Count > 0)
            {
                this.RecordingsManager.SaveTranscripts(entries);
            }

            // 4. Optional MP4 conversion of the recorded video segments
            var allVideoSessions = this.Timeline.ScreenShareSessions.Concat(this.Timeline.CameraSessions).ToList();
            if (!string.IsNullOrWhiteSpace(this.options.FfmpegPath))
            {
                foreach (var session in allVideoSessions)
                {
                    try
                    {
                        session.Mp4Files = await VideoRecorder.ConvertSegmentsToMp4Async(session.VideoFiles, this.options.FfmpegPath, this.RecordingsManager.Log).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"[Video] MP4 conversion failed: {ex.Message}");
                    }
                }
            }

            // 5. Save timeline
            string timelinePath = null;
            try
            {
                timelinePath = this.Timeline.Save(this.RecordingsManager.SessionDirectory);
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Timeline] Save failed: {ex.Message}");
            }

            // 6. Generate Minutes of Meeting (MoM)
            if (this.options.Mom?.Enabled == true)
            {
                try
                {
                    var talkTimeByName = speakerAudios.ToDictionary(
                        s => this.ResolveSpeakerName(s.SpeakerId),
                        s => s.TotalDurationSeconds,
                        StringComparer.OrdinalIgnoreCase);

                    var momResult = await TeamsCallingBot.Mom.MomGenerator.GenerateAsync(
                        this.RecordingsManager.SessionDirectory,
                        this.Timeline,
                        entries,
                        talkTimeByName,
                        this.options.Mom,
                        msg => this.RecordingsManager.Log(msg)).ConfigureAwait(false);

                    this.graphLogger.Info($"[MoM] Generated {momResult.DocxPath ?? momResult.MarkdownPath ?? momResult.JsonPath} (AI: {momResult.UsedAi})");
                    Console.WriteLine($">>> [MoM] Generated {(momResult.UsedAi ? "AI-enhanced" : "local")} Minutes of Meeting -> {Path.GetFileName(momResult.DocxPath ?? momResult.MarkdownPath ?? momResult.JsonPath)}");
                }
                catch (Exception ex)
                {
                    this.graphLogger.Error(ex, "[MoM] Generation failed.");
                }
            }

            // 7. Save Session Metadata
            var metadata = new
            {
                SessionId = this.Call.Id,
                StartTime = this.sessionStartTime,
                EndTime = DateTime.Now,
                DurationSeconds = (DateTime.Now - this.sessionStartTime).TotalSeconds,
                SpeakerAudioFilesCount = speakerAudios.Count,
                TranscriptsCount = entries.Count,
                ScreenShareSessions = this.Timeline.ScreenShareSessions.Select(s => new { s.PresenterName, s.StartedAt, s.EndedAt, s.FramesWritten, s.VideoFiles, s.Mp4Files, SnapshotCount = s.SnapshotFiles.Count }),
                CameraSessions = this.Timeline.CameraSessions.Select(s => new { s.PresenterName, s.StartedAt, s.EndedAt, s.FramesWritten, s.VideoFiles, s.Mp4Files }),
                Participants = this.Timeline.Participants.Select(p => new { p.DisplayName, p.IsBot, p.JoinedAt, p.LeftAt }),
                TimelineFile = timelinePath,
                SessionDirectory = this.RecordingsManager.SessionDirectory
            };
            this.RecordingsManager.SaveMetadata(metadata);

            this.graphLogger.Info($"[Session Complete] All assets saved to {this.RecordingsManager.SessionDirectory}");
            Console.WriteLine($">>> [Session Complete] All assets saved to {this.RecordingsManager.SessionDirectory}");
        }

        private string ResolveSpeakerName(uint speakerId)
        {
            if (speakerId == AudioAggregator.UnknownSpeakerId)
            {
                return "Unknown speaker";
            }

            var participant = this.Call.Participants.FirstOrDefault(p =>
                p.Resource.IsInLobby == false &&
                p.Resource.MediaStreams != null &&
                p.Resource.MediaStreams.Any(m => m.SourceId == speakerId.ToString()));

            var displayName = participant?.Resource?.Info?.Identity?.User?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = this.Timeline.ScreenShareSessions.Concat(this.Timeline.CameraSessions)
                    .FirstOrDefault(s => s.MediaSourceId == speakerId)?.PresenterName;
            }

            return string.IsNullOrWhiteSpace(displayName) ? $"Speaker {speakerId}" : displayName;
        }

        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        public void ShowVisualization(Bitmap contentImage, string title, int durationSeconds)
        {
            if (contentImage == null) return;
            var clone = (Bitmap)contentImage.Clone();
            lock (this.visualizationLock)
            {
                this.currentVisualization?.Dispose();
                this.currentVisualization = clone;
                this.currentVisualizationTitle = title;
                this.visualizationExpiresAt = durationSeconds > 0
                    ? DateTime.Now.AddSeconds(durationSeconds)
                    : DateTime.MaxValue;
            }
        }

        public void ClearVisualization()
        {
            lock (this.visualizationLock)
            {
                this.currentVisualization?.Dispose();
                this.currentVisualization = null;
                this.currentVisualizationTitle = null;
            }
        }

        public async Task<bool> SpeakTdaAnswerAsync(string query)
        {
            if (this.tdaClient == null || !this.tdaClient.IsConfigured)
            {
                this.graphLogger.Warn("[TDA] SpeakTdaAnswerAsync called but TDA is not configured - no-op.");
                return false;
            }

            try
            {
                string answer = await this.tdaClient.AskAsync(query).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(answer)) return false;

                if (this.AudioSender != null && !this.IsMuted)
                {
                    await this.AudioSender.SpeakAsync(answer).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[TDA] SpeakTdaAnswerAsync failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendTdaMessageAsync(string message)
        {
            if (this.tdaClient == null || !this.tdaClient.IsConfigured)
            {
                this.graphLogger.Warn("[TDA] SendTdaMessageAsync called but TDA is not configured - no-op.");
                return false;
            }

            try
            {
                return await this.tdaClient.SendMessageAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[TDA] SendTdaMessageAsync failed: {ex.Message}");
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            // Safety net: ensure call wind-down completes if handler disposed before Terminated webhook
            if (Interlocked.Exchange(ref this.endHandled, 1) == 0)
            {
                try
                {
                    this.HandleCallEndedAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    this.graphLogger.Warn($"Wind-down during Dispose: {ex.Message}");
                }
            }

            this.videoBroadcastCts?.Cancel();
            this.periodicFlushCts?.Cancel();
            this.autoLeaveCts?.Cancel();
            this.chatMonitorCts?.Cancel();
            this.voiceRecognizer?.Dispose();
            this.AudioSender?.Dispose();

            this.Call.OnUpdated -= this.OnCallUpdated;
            this.Call.Participants.OnUpdated -= this.OnParticipantsUpdated;

            lock (this.participantsWithUpdateHook)
            {
                foreach (var participant in this.Call.Participants)
                {
                    if (this.participantsWithUpdateHook.Contains(participant.Id))
                    {
                        try { participant.OnUpdated -= this.OnParticipantUpdated; } catch { }
                    }
                }

                this.participantsWithUpdateHook.Clear();
            }

            if (this.audioSocket != null)
            {
                this.audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;
            }

            if (this.vbssSocket != null)
            {
                this.vbssSocket.VideoReceiveStatusChanged -= this.OnVbssReceiveStatusChanged;
                this.vbssSocket.VideoMediaReceived -= this.OnVbssMediaReceived;
            }

            if (this.videoSocket != null)
            {
                this.videoSocket.VideoSendStatusChanged -= this.OnVideoSendStatusChanged;
                this.videoSocket.VideoKeyFrameNeeded -= this.OnVideoKeyFrameNeeded;
                this.videoSocket.VideoReceiveStatusChanged -= this.OnVideoReceiveStatusChanged;
                this.videoSocket.VideoMediaReceived -= this.OnVideoMediaReceived;
            }

            this.StopVbssRecorder("handler disposed");
            this.StopCameraRecorder("handler disposed");

            this.chatClient?.Dispose();
            this.latestScreenBitmap?.Dispose();
        }
    }
}
