namespace TeamsCallingBot.Bot
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
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
    using TeamsCallingBot.Audio;
    using TeamsCallingBot.Chat;
    using TeamsCallingBot.Common;
    using TeamsCallingBot.Config;
    using TeamsCallingBot.Storage;
    using TeamsCallingBot.Tda;
    using TeamsCallingBot.Video;

    /// <summary>
    /// Complete call lifecycle handler supporting:
    /// 1. Bidirectional Audio (Listen + Speak via AudioSender) - UNCHANGED, stable
    /// 2. Mute / Unmute
    /// 3. Screen share (VBSS) + participant camera VIDEO RECORDING, per person, to MJPEG AVI files
    ///    with periodic JPEG snapshots (see Video/VideoRecorder.cs and docs/VIDEO_RECORDING_AND_MOM.md)
    /// 4. Disk Storage (Audio, Video, Snapshots, Transcripts, Timeline, Metadata) in one session folder
    /// 5. Bot Video Broadcast (status card streamed into the meeting - resilient, never stops on error)
    /// 6. Auto Leave (Leaves after 30s when all human participants leave)
    /// 7. Chat notifications via Bot Framework Connector (fallback: Graph chat API)
    /// 8. Spoken greetings (configurable voice/culture, e.g. Indian English) + screen-share announcements
    ///
    /// VIDEO FIX (2026-09-04): the previous code subscribed the single VBSS socket to EVERY
    /// participant's screen-share MSI in a loop, so the last participant in the roster won - usually
    /// a viewer, not the presenter - and no frames ever arrived. Microsoft's PolicyRecordingBot sample
    /// filters on stream Direction == SendOnly for VBSS (SendOnly/SendReceive for camera video); that
    /// filter is now applied, per-participant OnUpdated events are wired so a share that starts
    /// mid-call is noticed, and the socket is unsubscribed when the presenter stops sharing.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        private int endHandled;
        private readonly IGraphLogger graphLogger;
        private readonly DateTime sessionStartTime;
        private readonly BotOptions options;

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
        public bool IsMuted { get; private set; } = false;
        private volatile bool isVideoSendActive = false;
        private int joinWelcomeSent = 0;
        private volatile bool callEstablished = false;

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

        // Bot Video Streaming
        private CancellationTokenSource videoBroadcastCts;
        private readonly object sendLock = new object();
        private volatile VideoFormat preferredSendFormat = VideoFormat.NV12_1280x720_15Fps;
        private volatile string activityLine;
        private int broadcastErrorsLogged;

        // Periodic Audio Flush
        private CancellationTokenSource periodicFlushCts;

        // Auto-Leave Timer
        private CancellationTokenSource autoLeaveCts;
        private const int AutoLeaveDebounceSeconds = 30;
        private const int InitialGracePeriodSeconds = 45;

        // Chat Info
        private readonly string chatThreadId;
        private readonly string botAccessToken;
        private readonly BotFrameworkChatClient chatClient;

        // TDA (Tata Steel Digital Assistant) integration - inert unless Bot:Tda:Enabled is true
        // and all placeholder config values have been filled in. See Tda/TdaClient.cs.
        private readonly TdaClient tdaClient;

        // Active-speaker tracking (drives camera-follow, see SubscribeParticipantMediaStreams) -
        // updated from OnAudioMediaReceived, which already gives us the speaking participant's MSI.
        private volatile uint lastActiveSpeakerMsi;

        // Bot video "visualization" mode (chart / TDA answer / image shown on the bot's own video
        // tile instead of the status card) - see Video/VideoFrameConverter.CreateVisualizationFrame
        // and BOT_CAPABILITY_EXPECTATIONS.md section 4. Off by default (currentVisualization null).
        private readonly object visualizationLock = new object();
        private Bitmap currentVisualization;
        private string currentVisualizationTitle;
        private DateTime visualizationExpiresAt = DateTime.MinValue;

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

            // 1. Initialize Disk Storage Manager
            this.RecordingsManager = new RecordingsManager(this.Call.Id);
            this.AudioAggregator = new AudioAggregator();
            this.Timeline = new MeetingTimeline { CallId = this.Call.Id, ChatThreadId = chatThreadId, StartedAt = this.sessionStartTime };
            this.chatClient = new BotFrameworkChatClient(this.options.AadAppId, this.options.AadAppSecretOrCertThumbprint, this.options.BotFrameworkServiceUrl, this.graphLogger);

            var tdaTokenProvider = new TdaTokenProvider(this.options.Tda, this.options.AadAppId, this.options.AadAppSecretOrCertThumbprint, this.graphLogger);
            this.tdaClient = new TdaClient(this.options.Tda, tdaTokenProvider, this.graphLogger);

            // 2. Wire Call Events
            this.Call.OnUpdated += this.OnCallUpdated;
            this.Call.Participants.OnUpdated += this.OnParticipantsUpdated;

            // 3. Resolve Media Sockets
            var localMediaSession = this.Call.GetLocalMediaSession();
            if (localMediaSession != null)
            {
                // Audio Socket (unchanged - stable)
                this.audioSocket = localMediaSession.AudioSocket;
                if (this.audioSocket != null)
                {
                    this.audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;
                    this.AudioSender = new AudioSender(this.audioSocket, this.graphLogger);

                    // Play pleasant greeting chime + verbal announcement when audio send is active
                    if (this.options.SpeakGreetingOnJoin)
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

                            if (this.AudioSender != null && this.AudioSender.IsAudioSendActive)
                            {
                                await Task.Delay(500).ConfigureAwait(false);
                                await this.AudioSender.PlayGreetingAsync().ConfigureAwait(false);
                                this.Timeline.AddEvent("bot_greeting_spoken", this.options.GreetingText);
                            }
                        });
                    }
                }

                // VBSS (Screen Share Receive) Socket
                this.vbssSocket = localMediaSession.VbssSocket;
                if (this.vbssSocket != null)
                {
                    this.vbssSocket.VideoReceiveStatusChanged += this.OnVbssReceiveStatusChanged;
                    this.vbssSocket.VideoMediaReceived += this.OnVbssMediaReceived;
                    this.vbssSocket.MediaStreamFailure += this.OnMediaStreamFailure;
                    this.Log("[VBSS Socket] Initialised - waiting for a participant to start sharing (subscription happens per presenter MSI).");
                }
                else
                {
                    this.Log("[VBSS Socket] NOT available on this media session - screen share cannot be recorded.");
                }

                // Video Socket (Bot Video Streaming & participant camera receiving)
                if (localMediaSession.VideoSockets != null && localMediaSession.VideoSockets.Any())
                {
                    this.videoSocket = localMediaSession.VideoSockets.First();
                    this.videoSocket.VideoSendStatusChanged += this.OnVideoSendStatusChanged;
                    this.videoSocket.VideoKeyFrameNeeded += this.OnVideoKeyFrameNeeded;
                    this.videoSocket.VideoReceiveStatusChanged += this.OnVideoReceiveStatusChanged;
                    this.videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
                    this.videoSocket.MediaStreamFailure += this.OnMediaStreamFailure;
                    this.StartBotVideoBroadcast();
                }
            }

            // 4. Start Periodic Audio Disk Flush (every 10s so WAV files are continuously present on disk) - unchanged
            this.periodicFlushCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                var token = this.periodicFlushCts.Token;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                        this.AudioAggregator.SnapshotToWavFiles(this.RecordingsManager.SessionDirectory);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"Periodic audio snapshot: {ex.Message}");
                    }
                }
            }, this.periodicFlushCts.Token);

            // 5. Hook participants that are already in the roster
            this.HookParticipantUpdates(this.Call.Participants);

            this.graphLogger.Info($"CallHandler initialized for {this.Call.Id}. Output folder: {this.RecordingsManager.SessionDirectory}");
            Console.WriteLine($">>> [CallHandler] Output folder: {this.RecordingsManager.SessionDirectory}");
        }

        private void Log(string message)
        {
            this.graphLogger.Info(message);
            Console.WriteLine(">>> " + message);
            this.RecordingsManager.Log(message);
        }

        // ===================================================================
        // 1. Audio (Both Ways) - UNCHANGED
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

                        // Camera-follow-active-speaker (see SubscribeParticipantMediaStreams): the audio
                        // MSI belongs to the same participant resource as their camera MSI, so tracking
                        // "who is speaking right now" here is enough to prefer their camera stream below -
                        // no separate dominant-speaker notification needed.
                        if (speakerBuffer.ActiveSpeakerId != 0 && speakerBuffer.ActiveSpeakerId != AudioAggregator.UnknownSpeakerId
                            && speakerBuffer.ActiveSpeakerId != this.lastActiveSpeakerMsi)
                        {
                            this.lastActiveSpeakerMsi = speakerBuffer.ActiveSpeakerId;
                            if (this.options.RecordParticipantVideo)
                            {
                                // Off the audio media thread - SubscribeParticipantMediaStreams does Graph
                                // resource lookups + socket (un)subscribe calls, must never block audio delivery.
                                _ = Task.Run(() => this.SubscribeParticipantMediaStreams());
                            }
                        }
                    }
                }
                else
                {
                    this.AudioAggregator.Append(AudioAggregator.UnknownSpeakerId, e.Buffer.Data, e.Buffer.Length, e.Buffer.Timestamp);
                }
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        /// <summary>
        /// Plays an audio file into the Teams meeting so participants can hear the bot.
        /// </summary>
        public async Task PlayAudioFileAsync(string wavFilePath)
        {
            if (this.AudioSender == null) return;
            this.graphLogger.Info($"[Audio Playback] Playing {wavFilePath} into meeting.");
            await this.AudioSender.PlayWavFileAsync(wavFilePath).ConfigureAwait(false);
        }

        // ===================================================================
        // 2. Mute / Unmute
        // ===================================================================
        public async Task MuteAsync()
        {
            this.IsMuted = true;
            if (this.AudioSender != null)
            {
                this.AudioSender.IsMuted = true;
            }

            try
            {
                await this.Call.MuteAsync().ConfigureAwait(false);
                this.graphLogger.Info($"[Mute] Bot muted successfully.");
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Mute] Server mute returned: {ex.Message}");
            }
        }

        public async Task UnmuteAsync()
        {
            this.IsMuted = false;
            if (this.AudioSender != null)
            {
                this.AudioSender.IsMuted = false;
            }

            try
            {
                await this.Call.UnmuteAsync().ConfigureAwait(false);
                this.graphLogger.Info($"[Unmute] Bot unmuted successfully.");
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Unmute] Server unmute returned: {ex.Message}");
            }
        }

        // ===================================================================
        // 3. Screen share (VBSS) + camera video: subscription and recording
        // ===================================================================
        private void OnMediaStreamFailure(object sender, MediaStreamFailureEventArgs e)
        {
            this.Log($"[Video] MediaStreamFailure on socket {(sender as IVideoSocket)?.SocketId}: {e}");
        }

        private void OnVbssReceiveStatusChanged(object sender, VideoReceiveStatusChangedEventArgs e)
        {
            this.Log($"[VBSS Socket] VideoReceiveStatusChanged: {e.MediaReceiveStatus}");

            if (e.MediaReceiveStatus == MediaReceiveStatus.Active)
            {
                // Teams is ready to deliver frames - make sure we are subscribed to the current presenter.
                this.SubscribeParticipantMediaStreams();
            }
            else
            {
                // The share ended (or the socket was torn down) - finalise the video file.
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

                    recorder.OnFrame(e.Buffer); // copies bytes, returns immediately
                    return;
                }

                if (recorder == null && this.options.RecordScreenShare)
                {
                    // Frames without a known presenter (e.g. subscription happened via ReceiveStatus before the
                    // roster told us who shares) - start a recorder keyed by the MSI on the frame.
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
            if (e.MediaReceiveStatus == MediaReceiveStatus.Active)
            {
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

        /// <summary>Legacy behaviour (one JPEG every 5 s) - only used when video recording is switched off.</summary>
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
            this.Log($"[Photo Saved] {savedPath}");
            bitmap.Dispose();
        }

        /// <summary>
        /// Looks at every participant's media streams and (un)subscribes the VBSS and video sockets so
        /// they follow the CURRENT presenter / camera. Filter mirrors Microsoft's PolicyRecordingBot
        /// sample: VBSS -> Modality.VideoBasedScreenSharing with Direction SendOnly (the presenter);
        /// camera -> Modality.Video with Direction SendOnly or SendReceive.
        /// </summary>
        private void SubscribeParticipantMediaStreams()
        {
            try
            {
                if (this.Call?.Participants == null)
                {
                    return;
                }

                string botAppId = this.options.AadAppId;
                uint sharerMsi = 0;
                IParticipant sharer = null;
                uint cameraMsi = 0;
                IParticipant cameraOwner = null;

                // Camera-follow-active-speaker: only one camera socket exists (SDK limitation - see
                // BOT_CAPABILITY_EXPECTATIONS.md section 2/7), so when several participants are sending
                // camera video, prefer whoever most recently spoke rather than an arbitrary "first found"
                // participant. Falls back to first-found if no active speaker's camera is available.
                string activeSpeakerParticipantId = this.ResolveParticipantByMsi(this.lastActiveSpeakerMsi).ParticipantId;
                uint preferredCameraMsi = 0;
                IParticipant preferredCameraOwner = null;

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
                        else if (stream.MediaType == Modality.Video)
                        {
                            if (cameraOwner == null)
                            {
                                cameraOwner = participant;
                                cameraMsi = msi;
                            }

                            if (preferredCameraOwner == null && activeSpeakerParticipantId != null && participant.Id == activeSpeakerParticipantId)
                            {
                                preferredCameraOwner = participant;
                                preferredCameraMsi = msi;
                            }
                        }
                    }
                }

                if (preferredCameraOwner != null)
                {
                    cameraOwner = preferredCameraOwner;
                    cameraMsi = preferredCameraMsi;
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

        /// <summary>
        /// Instantly saves a photo of the current active screen share (or meeting video) on demand.
        /// </summary>
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

        /// <summary>Summary of the active/finished video recordings for the management API.</summary>
        public object GetVideoStatus()
        {
            var vbss = this.vbssRecorder;
            var cam = this.cameraRecorder;
            return new
            {
                ScreenShare = vbss == null ? null : new
                {
                    Presenter = vbss.SourceLabel,
                    Msi = vbss.MediaSourceId,
                    StartedAt = vbss.StartedAt,
                    Resolution = $"{vbss.Width}x{vbss.Height}",
                    FramesReceived = vbss.FramesReceived,
                    FramesWritten = vbss.FramesWritten,
                    Files = vbss.SegmentPaths,
                    Snapshots = vbss.Snapshots.Count,
                },
                Camera = cam == null ? null : new
                {
                    Participant = cam.SourceLabel,
                    Msi = cam.MediaSourceId,
                    StartedAt = cam.StartedAt,
                    Resolution = $"{cam.Width}x{cam.Height}",
                    FramesReceived = cam.FramesReceived,
                    FramesWritten = cam.FramesWritten,
                    Files = cam.SegmentPaths,
                },
                CompletedScreenShareSessions = this.Timeline.ScreenShareSessions.Where(s => s.EndedAt != null).Select(s => new { s.PresenterName, s.StartedAt, s.EndedAt, s.VideoFiles, SnapshotCount = s.SnapshotFiles.Count }),
                BotVideoSendActive = this.isVideoSendActive,
                BotVideoFormat = this.preferredSendFormat?.ToString(),
            };
        }

        // ===================================================================
        // 5. Video Sharing by Bot (status card)
        // ===================================================================
        private void OnVideoSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            var preferred = e.PreferredVideoSourceFormat;
            if (preferred != null && preferred.VideoColorFormat == VideoColorFormat.NV12 && preferred.Width > 0 && preferred.Height > 0)
            {
                this.preferredSendFormat = preferred;
            }

            this.isVideoSendActive = (e.MediaSendStatus == MediaSendStatus.Active);
            this.Log($"[VideoSocket] VideoSendStatusChanged: {e.MediaSendStatus}, preferred format: {preferred}");

            if (this.isVideoSendActive)
            {
                // Push a frame right away so the tile appears immediately instead of waiting for the next tick.
                _ = Task.Run(() => this.SendCardFrame(0));
            }
        }

        private void OnVideoKeyFrameNeeded(object sender, VideoKeyFrameNeededEventArgs e)
        {
            this.graphLogger.Info("[VideoSocket] KeyFrameNeeded - sending a fresh frame.");
            _ = Task.Run(() => this.SendCardFrame(0));
        }

        private void StartBotVideoBroadcast()
        {
            if (this.videoSocket == null) return;

            this.videoBroadcastCts = new CancellationTokenSource();
            var token = this.videoBroadcastCts.Token;

            _ = Task.Run(async () =>
            {
                int tick = 0;
                bool snapshotSaved = false;
                var pace = Stopwatch.StartNew();

                while (!token.IsCancellationRequested)
                {
                    tick++;
                    var format = this.preferredSendFormat ?? VideoFormat.NV12_1280x720_15Fps;
                    int frameDelayMs = (int)Math.Max(33, 1000 / Math.Max(1f, format.FrameRate));

                    try
                    {
                        if (!snapshotSaved)
                        {
                            snapshotSaved = true;
                            using (var card = this.RenderCurrentBotFrame(tick))
                            {
                                this.RecordingsManager.SavePhoto(card, "05_bot_broadcast_preview");
                            }
                        }

                        if (this.isVideoSendActive)
                        {
                            this.SendCardFrame(tick);
                        }
                    }
                    catch (Exception ex)
                    {
                        // IMPORTANT: never let one failed Send() kill the loop - that is exactly how the bot's
                        // tile used to "disappear" for the rest of the meeting.
                        if (Interlocked.Increment(ref this.broadcastErrorsLogged) <= 5)
                        {
                            this.graphLogger.Warn($"[Bot Video] Frame send failed (loop continues): {ex.Message}");
                        }
                    }

                    // Pace against the wall clock so render time does not accumulate into drift.
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

            this.graphLogger.Info("[Bot Video Streaming] Broadcast loop initialized.");
        }

        /// <summary>Renders the status card at the negotiated format and sends one NV12 frame.</summary>
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
                    using (var card = this.RenderCurrentBotFrame(tick))
                    {
                        byte[] nv12 = VideoFrameConverter.ConvertBitmapToNV12(card, format.Width, format.Height);

                        // VideoSendBuffer(byte[]) copies into its own unmanaged buffer and frees it on Dispose.
                        // Timestamp must come from the media platform clock (100-ns units), not a 0-based counter.
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

        /// <summary>
        /// Picks between the normal status card and an active visualization (chart / TDA answer /
        /// image shown on the bot's own video tile - see ShowVisualizationAsync). Caller owns and
        /// must Dispose the returned Bitmap.
        /// </summary>
        private Bitmap RenderCurrentBotFrame(int tick)
        {
            Bitmap visualization = null;
            string title = null;
            lock (this.visualizationLock)
            {
                if (this.currentVisualization != null && DateTime.Now < this.visualizationExpiresAt)
                {
                    visualization = this.currentVisualization;
                    title = this.currentVisualizationTitle;
                }
                else if (this.currentVisualization != null)
                {
                    // Expired - clear it so subsequent frames fall back to the status card.
                    this.currentVisualization.Dispose();
                    this.currentVisualization = null;
                    this.currentVisualizationTitle = null;
                }
            }

            if (visualization != null)
            {
                return VideoFrameConverter.CreateVisualizationFrame(visualization, title);
            }

            return VideoFrameConverter.CreateBotStatusCard(
                "Teams AI Assistant",
                this.IsMuted ? "Audio output muted - still recording" : "Recording audio and video",
                this.Call.Id,
                this.IsMuted,
                tick,
                this.activityLine);
        }

        /// <summary>
        /// Shows <paramref name="contentImage"/> on the bot's outgoing video tile for
        /// <paramref name="durationSeconds"/> seconds (0 or negative = show indefinitely until
        /// ClearVisualization or a new call to this method). Takes ownership of contentImage (clones
        /// it internally, caller may dispose its own copy). This is the "share screen to show
        /// visualisation" capability - see BOT_CAPABILITY_EXPECTATIONS.md section 4, option (a).
        /// </summary>
        public void ShowVisualization(Bitmap contentImage, string title, int durationSeconds)
        {
            if (contentImage == null)
            {
                return;
            }

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

            this.Log($"[Visualization] Showing '{title}' on bot video tile" + (durationSeconds > 0 ? $" for {durationSeconds}s." : " indefinitely."));
        }

        /// <summary>Reverts the bot's video tile to the normal status card immediately.</summary>
        public void ClearVisualization()
        {
            lock (this.visualizationLock)
            {
                this.currentVisualization?.Dispose();
                this.currentVisualization = null;
                this.currentVisualizationTitle = null;
                this.visualizationExpiresAt = DateTime.MinValue;
            }
        }

        // ===================================================================
        // TDA (Tata Steel Digital Assistant) integration - inert unless Bot:Tda:Enabled is true.
        // See Config/BotOptions.cs (TdaOptions) and Tda/TdaClient.cs.
        // ===================================================================

        /// <summary>
        /// Asks TDA <paramref name="query"/> and speaks the answer into the meeting. No-ops (logs and
        /// returns false) if TDA is not configured/enabled, if AudioSender is unavailable, or if TDA
        /// returns nothing - never throws into the caller.
        /// </summary>
        public async Task<bool> SpeakTdaAnswerAsync(string query)
        {
            if (!this.tdaClient.IsConfigured)
            {
                this.graphLogger.Warn("[TDA] SpeakTdaAnswerAsync called but TDA is not enabled/configured - no-op.");
                return false;
            }

            try
            {
                string answer = await this.tdaClient.AskAsync(query).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return false;
                }

                if (this.AudioSender != null && !this.IsMuted)
                {
                    await this.AudioSender.SpeakAsync(answer).ConfigureAwait(false);
                }

                this.Timeline.AddEvent("tda_answer_spoken", answer);
                return true;
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[TDA] SpeakTdaAnswerAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sends a message to TDA using a token scoped to the "TSL AI" resource. False/no-op if not configured.</summary>
        public async Task<bool> SendTdaMessageAsync(string message)
        {
            if (!this.tdaClient.IsConfigured)
            {
                this.graphLogger.Warn("[TDA] SendTdaMessageAsync called but TDA is not enabled/configured - no-op.");
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

        // ===================================================================
        // 6. Participants: roster tracking, greetings, auto leave
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
                            await Task.Delay(2500).ConfigureAwait(false); // let their client connect audio first
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

            // Count participants who are in the meeting (not in lobby, and not this bot)
            int humanCount = this.Call.Participants.Count(p =>
                p.Resource.IsInLobby == false &&
                (p.Resource.Info?.Identity?.Application == null || p.Resource.Info.Identity.Application.Id != botAppId));

            this.graphLogger.Info($"[Participants Updated] Active non-bot participant count: {humanCount}");
            Console.WriteLine($">>> [Participants Updated] Active non-bot participant count: {humanCount}");

            // Check and subscribe to participant screen share and video streams
            this.SubscribeParticipantMediaStreams();

            // Initial Grace Period check: do not auto-leave during the first 45 seconds of the call
            bool inGracePeriod = (DateTime.Now - this.sessionStartTime).TotalSeconds < InitialGracePeriodSeconds;

            if (humanCount == 0 && !inGracePeriod)
            {
                // All non-bot participants left after the initial grace period - start countdown
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
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine(">>> [Auto Leave] Countdown canceled - participant returned.");
                        }
                        catch (Exception ex)
                        {
                            this.graphLogger.Error(ex, "Failed executing Auto Leave call deletion.");
                        }
                    }, token);
                }
            }
            else if (humanCount > 0)
            {
                // Humans present - cancel any pending auto leave
                if (this.autoLeaveCts != null && !this.autoLeaveCts.IsCancellationRequested)
                {
                    this.autoLeaveCts.Cancel();
                    this.autoLeaveCts = null;
                    this.graphLogger.Info("[Auto Leave] Canceled because a human participant is present.");
                    Console.WriteLine(">>> [Auto Leave] Countdown canceled - participant is present.");
                }
            }
        }

        /// <summary>
        /// A participant starting/stopping a share changes THAT participant's resource (its mediaStreams),
        /// which raises IParticipant.OnUpdated - not the collection's OnUpdated. Without this hook a share
        /// that begins mid-meeting is never noticed. (Same pattern as Microsoft's PolicyRecordingBot.)
        /// </summary>
        private void HookParticipantUpdates(IEnumerable<IParticipant> participants)
        {
            if (participants == null)
            {
                return;
            }

            foreach (var participant in participants)
            {
                lock (this.participantsWithUpdateHook)
                {
                    if (!this.participantsWithUpdateHook.Add(participant.Id))
                    {
                        continue;
                    }
                }

                participant.OnUpdated += this.OnParticipantUpdated;
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
                // Guests / phone users show up under additional identity keys (e.g. "guest", "phone").
                foreach (var kvp in identity.AdditionalData)
                {
                    var token = kvp.Value as Newtonsoft.Json.Linq.JObject;
                    var dn = token?["displayName"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(dn))
                    {
                        name = dn;
                        break;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(name) ? $"Participant {participant?.Id?.Substring(0, Math.Min(8, participant.Id.Length))}" : name;
        }

        private static string FirstName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "there";
            }

            var parts = displayName.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
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
        // 7. Call state, removal detection & chat message posting
        // ===================================================================
        private void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
        {
            Console.WriteLine($">>> CALL STATE CHANGED: {args.NewResource.State} (call {this.Call.Id})");
            var resultInfo = args.NewResource.ResultInfo;
            if (resultInfo != null)
            {
                Console.WriteLine($">>> CALL RESULT INFO: code={resultInfo.Code}, subcode={resultInfo.Subcode}, message={resultInfo.Message}");
            }

            // Post welcome message and subscribe streams when call becomes Established
            if (args.NewResource.State == CallState.Established)
            {
                this.callEstablished = true;
                if (Interlocked.Exchange(ref this.joinWelcomeSent, 1) == 0)
                {
                    this.Timeline.AddEvent("call_established", this.Call.Id);
                    _ = Task.Run(() => this.PostTextMessageToChatAsync(
                        "🤖 <b>Teams AI Assistant</b> has joined the meeting.<br/>• Audio recording &amp; per-speaker capture: <b>Active</b><br/>• Screen share video recording: <b>Ready</b> (starts automatically when someone shares)<br/>• Bot video status tile: <b>Active</b>"));
                }

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

            // Check if removed by user / organizer (subcodes 7002, 702, 403, or keyword messages)
            bool wasRemovedByUser = false;
            if (resultInfo != null)
            {
                string msg = resultInfo.Message?.ToLowerInvariant() ?? "";
                if (resultInfo.Subcode == 7002 || resultInfo.Subcode == 702 || resultInfo.Code == 403 ||
                    msg.Contains("removed") || msg.Contains("kicked") || msg.Contains("ejected") || msg.Contains("organizer"))
                {
                    wasRemovedByUser = true;
                }
            }

            if (wasRemovedByUser)
            {
                Console.WriteLine(">>> BOT REMOVAL DETECTED: Announcing and posting notification message...");
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

            _ = this.HandleCallEndedAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    this.graphLogger.Error(t.Exception, $"Failed during wind-down for call {this.Call.Id}.");
                }
            });
        }

        /// <summary>
        /// Posts a formatted text/HTML message into the Teams meeting group chat.
        /// Order: Bot Framework Connector (no Graph permission needed) -> Graph chat API fallback.
        /// </summary>
        public async Task<bool> PostTextMessageToChatAsync(string htmlContent, bool isRemovalNotification = false)
        {
            if (string.IsNullOrWhiteSpace(this.chatThreadId))
            {
                this.graphLogger.Warn("[Chat Notification] Missing chatThreadId - cannot post message.");
                return false;
            }

            if (this.options.UseBotFrameworkForChat && this.chatClient.IsConfigured)
            {
                bool viaConnector = await this.chatClient.SendMessageAsync(this.chatThreadId, htmlContent).ConfigureAwait(false);
                if (viaConnector)
                {
                    this.Timeline.AddEvent("chat_posted", "connector");
                    if (isRemovalNotification)
                    {
                        this.RecordingsManager.SaveRemovalMessageRecord(this.chatThreadId, htmlContent, true);
                    }

                    return true;
                }

                this.graphLogger.Warn("[Chat Notification] Connector post failed - falling back to Graph chat API.");
            }
            else if (this.options.UseBotFrameworkForChat)
            {
                this.graphLogger.Warn("[Chat Notification] Bot Framework chat enabled but AadAppSecretOrCertThumbprint is a placeholder - falling back to Graph chat API.");
            }

            string token = this.botAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = BotOptions.Current?.OverrideBearerToken;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                this.graphLogger.Warn("[Chat Notification] No access token available to post chat message.");
                if (isRemovalNotification)
                {
                    this.RecordingsManager.SaveRemovalMessageRecord(this.chatThreadId, htmlContent, false);
                }
                return false;
            }

            try
            {
                string endpoint = $"https://graph.microsoft.com/v1.0/chats/{this.chatThreadId}/messages";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    var payload = new
                    {
                        body = new
                        {
                            contentType = "html",
                            content = htmlContent
                        }
                    };

                    string jsonContent = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(endpoint, content).ConfigureAwait(false);
                    bool success = response.IsSuccessStatusCode;

                    if (isRemovalNotification)
                    {
                        this.RecordingsManager.SaveRemovalMessageRecord(this.chatThreadId, htmlContent, success);
                    }

                    if (success)
                    {
                        this.graphLogger.Info($"[Chat Notification] Successfully posted message to meeting chat {this.chatThreadId}!");
                        Console.WriteLine($">>> [Chat Notification] Successfully posted message to meeting chat!");
                        this.Timeline.AddEvent("chat_posted", "graph");
                        return true;
                    }
                    else
                    {
                        var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        this.graphLogger.Warn($"[Chat Notification] Graph chat post returned ({response.StatusCode}): {err}");
                        Console.WriteLine($">>> [Chat Notification] Graph chat post returned ({response.StatusCode}): {err}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "Error posting message to meeting chat.");
                if (isRemovalNotification)
                {
                    this.RecordingsManager.SaveRemovalMessageRecord(this.chatThreadId, $"Exception: {ex.Message}", false);
                }
                return false;
            }
        }

        // ===================================================================
        // 4. Wind-down: video finalisation, audio flush, transcripts, metadata
        // ===================================================================
        private async Task HandleCallEndedAsync()
        {
            this.graphLogger.Info($"Call {this.Call.Id} terminated - saving all audio, video, transcripts and metadata.");

            // 0. Finalise video files first (fast; makes the AVIs playable even if a later step fails)
            this.StopVbssRecorder("call ended");
            this.StopCameraRecorder("call ended");
            this.Timeline.EndedAt = DateTime.Now;

            // 1. Flush Audio to the session folder (unchanged)
            var speakerAudios = this.AudioAggregator.FlushToWavFiles(this.RecordingsManager.SessionDirectory);

            // 2. Transcribe (unchanged)
            var entries = new List<TranscriptEntry>();
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

            // 3. Save Transcripts (.txt and .json) (unchanged)
            this.RecordingsManager.SaveTranscripts(entries);

            // 4. Optional MP4 conversion of the recorded video segments (only when ffmpeg is configured)
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

            // 5. Save timeline (who joined/left, who shared what and when, file names)
            string timelinePath = null;
            try
            {
                timelinePath = this.Timeline.Save(this.RecordingsManager.SessionDirectory);
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"[Timeline] Save failed: {ex.Message}");
            }

            // 6. Generate Minutes of Meeting (MoM) - detailed Word doc from transcript + screen snapshots
            if (this.options.Mom?.Enabled == true)
            {
                try
                {
                    // 16 kHz, 16-bit mono WAV = 32,000 bytes/second of PCM after the 44-byte header
                    var talkTimeByName = speakerAudios.ToDictionary(
                        s => this.ResolveSpeakerName(s.SpeakerId),
                        s => System.IO.File.Exists(s.WavPath) ? Math.Max(0, new FileInfo(s.WavPath).Length - 44) / 32000.0 : 0.0,
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

                    // 6a. Upload the MoM doc (and, if configured, the transcript files) to GCS and get
                    // signed links, then hand off ONE combined notification (summary + link) to the
                    // Cloud Run relay - the VM never talks to Power Automate/email directly (see
                    // PRODUCTION_STATUS.md §4). Both steps are best-effort - failures here never
                    // affect the local files already saved above.
                    if (!string.IsNullOrWhiteSpace(momResult.DocxPath))
                    {
                        var gcsUploader = new GcsUploader(this.options.Gcs, msg => this.RecordingsManager.Log(msg));
                        string signedUrl = await gcsUploader.UploadMomDocumentAsync(momResult.DocxPath, this.Call.Id, this.chatThreadId).ConfigureAwait(false);
                        await gcsUploader.UploadTranscriptFilesAsync(this.RecordingsManager.SessionDirectory, this.Call.Id, this.chatThreadId).ConfigureAwait(false);

                        var relayClient = new CloudRunRelayClient(this.options.CloudRunRelay, msg => this.RecordingsManager.Log(msg));
                        await relayClient.SendMomNotificationAsync(
                            momResult.Document,
                            this.chatThreadId,
                            signedUrl,
                            this.options.Gcs?.SignedUrlExpiryHours ?? 168,
                            momResult.UsedAi).ConfigureAwait(false);
                    }
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
                // The participant may already have left - fall back to the timeline's record of who owned that MSI.
                displayName = this.Timeline.ScreenShareSessions.Concat(this.Timeline.CameraSessions)
                    .FirstOrDefault(s => s.MediaSourceId == speakerId)?.PresenterName;
            }

            return string.IsNullOrWhiteSpace(displayName) ? $"Speaker {speakerId}" : displayName;
        }

        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            this.videoBroadcastCts?.Cancel();
            this.periodicFlushCts?.Cancel();
            this.autoLeaveCts?.Cancel();
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
                this.vbssSocket.MediaStreamFailure -= this.OnMediaStreamFailure;
            }

            if (this.videoSocket != null)
            {
                this.videoSocket.VideoSendStatusChanged -= this.OnVideoSendStatusChanged;
                this.videoSocket.VideoKeyFrameNeeded -= this.OnVideoKeyFrameNeeded;
                this.videoSocket.VideoReceiveStatusChanged -= this.OnVideoReceiveStatusChanged;
                this.videoSocket.VideoMediaReceived -= this.OnVideoMediaReceived;
                this.videoSocket.MediaStreamFailure -= this.OnMediaStreamFailure;
            }

            // Safety net: if the call was disposed without a Terminated notification, still close the AVIs.
            this.StopVbssRecorder("handler disposed");
            this.StopCameraRecorder("handler disposed");

            this.chatClient?.Dispose();
            this.latestScreenBitmap?.Dispose();
            lock (this.visualizationLock)
            {
                this.currentVisualization?.Dispose();
                this.currentVisualization = null;
            }
        }
    }
}
