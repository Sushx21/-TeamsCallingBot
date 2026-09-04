namespace TeamsCallingBot.Bot
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
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
    using TeamsCallingBot.Audio;
    using TeamsCallingBot.Common;
    using TeamsCallingBot.Config;
    using TeamsCallingBot.Storage;
    using TeamsCallingBot.Video;

    /// <summary>
    /// Complete call lifecycle handler supporting:
    /// 1. Bidirectional Audio (Listen + Speak via AudioSender)
    /// 2. Mute / Unmute
    /// 3. Video Screen Sharing Photo Capture (from participants)
    /// 4. Disk Storage (Audio, Photos, Transcripts, Metadata under D:\Teamsbot\Recordings\<SessionId>)
    /// 5. Bot Video Broadcast (Streaming status card into meeting)
    /// 6. Auto Leave (Leaves after 30s when all human participants leave)
    /// 7. Group Chat Notification if removed by user
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

        // State Flags
        public bool IsMuted { get; private set; } = false;
        private volatile bool isVideoSendActive = false;
        private volatile bool isKeyFrameNeeded = true;
        private int joinWelcomeSent = 0;

        // Screen Capture Throttling (e.g. 1 photo every 5 seconds)
        private DateTime lastPhotoTime = DateTime.MinValue;
        private readonly TimeSpan photoInterval = TimeSpan.FromSeconds(5);
        private readonly object photoLock = new object();
        private Bitmap latestScreenBitmap = null;

        // Bot Video Streaming
        private CancellationTokenSource videoBroadcastCts;

        // Periodic Audio Flush
        private CancellationTokenSource periodicFlushCts;

        // Auto-Leave Timer
        private CancellationTokenSource autoLeaveCts;
        private const int AutoLeaveDebounceSeconds = 30;
        private const int InitialGracePeriodSeconds = 45;

        // Chat Info
        private readonly string chatThreadId;
        private readonly string botAccessToken;

        public ICall Call { get; }

        public CallHandler(ICall call, IGraphLogger logger, string chatThreadId = null, string accessToken = null)
            : base(TimeSpan.FromMinutes(1), logger)
        {
            this.Call = call ?? throw new ArgumentNullException(nameof(call));
            this.graphLogger = logger;
            this.sessionStartTime = DateTime.Now;
            this.chatThreadId = chatThreadId;
            this.botAccessToken = accessToken ?? BotOptions.Current?.OverrideBearerToken;

            // 1. Initialize Disk Storage Manager
            this.RecordingsManager = new RecordingsManager(this.Call.Id);
            this.AudioAggregator = new AudioAggregator();

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

                    // Play pleasant greeting chime + verbal announcement when audio send is active
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
                        }
                    });
                }

                // VBSS (Screen Share Receive) Socket
                this.vbssSocket = localMediaSession.VbssSocket;
                if (this.vbssSocket != null)
                {
                    this.vbssSocket.VideoReceiveStatusChanged += this.OnVbssReceiveStatusChanged;
                    this.vbssSocket.VideoMediaReceived += this.OnVbssMediaReceived;

                    try
                    {
                        this.vbssSocket.Subscribe(VideoResolution.HD1080p);
                    }
                    catch (Exception ex)
                    {
                        this.graphLogger.Warn($"Initial VBSS subscribe returned: {ex.Message}");
                    }

                    this.graphLogger.Info($"[VBSS Socket Initialized] Screen sharing photo capture is active.");
                }

                // Video Socket (Bot Video Streaming & Meeting Video Receiving)
                if (localMediaSession.VideoSockets != null && localMediaSession.VideoSockets.Any())
                {
                    this.videoSocket = localMediaSession.VideoSockets.First();
                    this.videoSocket.VideoSendStatusChanged += this.OnVideoSendStatusChanged;
                    this.videoSocket.VideoKeyFrameNeeded += this.OnVideoKeyFrameNeeded;
                    this.videoSocket.VideoReceiveStatusChanged += this.OnVideoReceiveStatusChanged;
                    this.videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
                    this.StartBotVideoBroadcast();
                }
            }

            // 4. Start Periodic Audio Disk Flush (every 10s so WAV files are continuously present on disk)
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

            this.graphLogger.Info($"CallHandler initialized for {this.Call.Id}. Output folder: {this.RecordingsManager.SessionDirectory}");
            Console.WriteLine($">>> [CallHandler] Output folder: {this.RecordingsManager.SessionDirectory}");
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
        // 3. Video Screen Sharing & Meeting Video Capture (Photos)
        // ===================================================================
        private void OnVbssReceiveStatusChanged(object sender, VideoReceiveStatusChangedEventArgs e)
        {
            this.graphLogger.Info($"[VBSS Socket] VideoReceiveStatusChanged: {e.MediaReceiveStatus}");
            Console.WriteLine($">>> [VBSS Socket] VideoReceiveStatus: {e.MediaReceiveStatus}");
            if (e.MediaReceiveStatus == MediaReceiveStatus.Active && this.vbssSocket != null)
            {
                try
                {
                    this.vbssSocket.Subscribe(VideoResolution.HD1080p);
                    this.graphLogger.Info("[VBSS Socket] Subscribed to HD1080p screen share.");
                    Console.WriteLine(">>> [VBSS Socket] Subscribed to HD1080p screen share.");
                }
                catch (Exception ex)
                {
                    this.graphLogger.Warn($"[VBSS Socket] Subscribe returned: {ex.Message}");
                }
            }
        }

        private void OnVbssMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            try
            {
                DateTime now = DateTime.Now;
                bool shouldCapture = false;

                lock (this.photoLock)
                {
                    if (now - this.lastPhotoTime >= this.photoInterval)
                    {
                        this.lastPhotoTime = now;
                        shouldCapture = true;
                    }
                }

                if (shouldCapture && e.Buffer.Data != IntPtr.Zero)
                {
                    Bitmap bitmap = null;
                    if (e.Buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.NV12)
                    {
                        bitmap = VideoFrameConverter.ConvertNV12ToBitmap(e.Buffer.Data, e.Buffer.VideoFormat.Width, e.Buffer.VideoFormat.Height, e.Buffer.Stride);
                    }
                    else if (e.Buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.Rgb24)
                    {
                        bitmap = VideoFrameConverter.ConvertRGB24ToBitmap(e.Buffer.Data, e.Buffer.VideoFormat.Width, e.Buffer.VideoFormat.Height, e.Buffer.Stride);
                    }

                    if (bitmap != null)
                    {
                        lock (this.photoLock)
                        {
                            this.latestScreenBitmap?.Dispose();
                            this.latestScreenBitmap = (Bitmap)bitmap.Clone();
                        }

                        var savedPath = this.RecordingsManager.SavePhoto(bitmap, "03_photo_screenshare");
                        this.graphLogger.Info($"[Screen Photo Saved] {savedPath}");
                        Console.WriteLine($">>> [Screen Photo Saved] {savedPath}");
                        bitmap.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "Error capturing screen share frame.");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private void OnVideoReceiveStatusChanged(object sender, VideoReceiveStatusChangedEventArgs e)
        {
            this.graphLogger.Info($"[Video Socket] VideoReceiveStatusChanged: {e.MediaReceiveStatus}");
            Console.WriteLine($">>> [Video Socket] VideoReceiveStatus: {e.MediaReceiveStatus}");
            if (e.MediaReceiveStatus == MediaReceiveStatus.Active && this.videoSocket != null)
            {
                try
                {
                    this.videoSocket.Subscribe(VideoResolution.HD720p);
                    this.graphLogger.Info("[Video Socket] Subscribed to HD720p meeting video.");
                    Console.WriteLine(">>> [Video Socket] Subscribed to HD720p meeting video.");
                }
                catch (Exception ex)
                {
                    this.graphLogger.Warn($"[Video Socket] Subscribe returned: {ex.Message}");
                }
            }
        }

        private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            try
            {
                DateTime now = DateTime.Now;
                bool shouldCapture = false;

                lock (this.photoLock)
                {
                    if (now - this.lastPhotoTime >= this.photoInterval)
                    {
                        this.lastPhotoTime = now;
                        shouldCapture = true;
                    }
                }

                if (shouldCapture && e.Buffer.Data != IntPtr.Zero)
                {
                    Bitmap bitmap = null;
                    if (e.Buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.NV12)
                    {
                        bitmap = VideoFrameConverter.ConvertNV12ToBitmap(e.Buffer.Data, e.Buffer.VideoFormat.Width, e.Buffer.VideoFormat.Height, e.Buffer.Stride);
                    }
                    else if (e.Buffer.VideoFormat?.VideoColorFormat == VideoColorFormat.Rgb24)
                    {
                        bitmap = VideoFrameConverter.ConvertRGB24ToBitmap(e.Buffer.Data, e.Buffer.VideoFormat.Width, e.Buffer.VideoFormat.Height, e.Buffer.Stride);
                    }

                    if (bitmap != null)
                    {
                        lock (this.photoLock)
                        {
                            this.latestScreenBitmap?.Dispose();
                            this.latestScreenBitmap = (Bitmap)bitmap.Clone();
                        }

                        var savedPath = this.RecordingsManager.SavePhoto(bitmap, "03_photo_video");
                        this.graphLogger.Info($"[Meeting Video Photo Saved] {savedPath}");
                        Console.WriteLine($">>> [Meeting Video Photo Saved] {savedPath}");
                        bitmap.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Error(ex, "Error capturing meeting video frame.");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        /// <summary>
        /// Instantly saves a photo of the current active screen share or meeting video on demand.
        /// </summary>
        public string CapturePhotoNow(string tag = "manual")
        {
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
        // 5. Video Sharing by Bot
        // ===================================================================
        private void OnVideoSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            this.isVideoSendActive = (e.MediaSendStatus == MediaSendStatus.Active);
            this.graphLogger.Info($"[VideoSocket] VideoSendStatusChanged: {e.MediaSendStatus}");
            Console.WriteLine($">>> [VideoSocket] VideoSendStatus: {e.MediaSendStatus}");
        }

        private void OnVideoKeyFrameNeeded(object sender, VideoKeyFrameNeededEventArgs e)
        {
            this.isKeyFrameNeeded = true;
            this.graphLogger.Info("[VideoSocket] KeyFrameNeeded");
        }

        private void StartBotVideoBroadcast()
        {
            if (this.videoSocket == null) return;

            this.videoBroadcastCts = new CancellationTokenSource();
            var token = this.videoBroadcastCts.Token;

            _ = Task.Run(async () =>
            {
                int width = 1280;
                int height = 720;
                int frameRate = 15; // 15 FPS
                int frameDelayMs = 1000 / frameRate;
                uint timestamp = 0;
                int tick = 0;
                bool snapshotSaved = false;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        tick++;

                        // Generate dynamic status card with prominent bot icon, waveform bars, and status
                        using (var card = VideoFrameConverter.CreateBotStatusCard(
                            "Teams AI Assistant",
                            this.IsMuted ? "Muted" : "Active & Listening",
                            this.Call.Id,
                            this.IsMuted,
                            tick))
                        {
                            // Save a single preview snapshot of the bot broadcast card on start
                            if (!snapshotSaved)
                            {
                                snapshotSaved = true;
                                this.RecordingsManager.SavePhoto(card, "05_bot_broadcast_preview");
                            }

                            // Only send NV12 frames when the socket send status is Active!
                            if (this.isVideoSendActive)
                            {
                                if (this.isKeyFrameNeeded)
                                {
                                    this.isKeyFrameNeeded = false;
                                }
                                byte[] nv12Bytes = VideoFrameConverter.ConvertBitmapToNV12(card, width, height);

                                // Use safe VideoSendBuffer constructor that manages its own unmanaged buffer
                                // and frees upon Dispose() - completely eliminating double free!
                                using (var videoBuffer = new VideoSendBuffer(
                                    nv12Bytes,
                                    (uint)nv12Bytes.Length,
                                    VideoFormat.NV12_1280x720_30Fps,
                                    (long)timestamp))
                                {
                                    this.videoSocket.Send(videoBuffer);
                                }
                            }
                        }

                        timestamp += (uint)frameDelayMs;
                        await Task.Delay(frameDelayMs, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    this.graphLogger.Error(ex, "Error in bot video broadcast loop.");
                }
            }, token);

            this.graphLogger.Info("[Bot Video Streaming] Broadcast loop initialized at 15 FPS.");
        }

        // ===================================================================
        // 6. Auto Leave
        // ===================================================================
        private void OnParticipantsUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            string botAppId = BotOptions.Current?.AadAppId;

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

        private void SubscribeParticipantMediaStreams()
        {
            try
            {
                string botAppId = BotOptions.Current?.AadAppId;
                if (this.Call?.Participants == null) return;

                foreach (var participant in this.Call.Participants)
                {
                    if (participant.Resource.IsInLobby == true) continue;
                    if (participant.Resource.Info?.Identity?.Application?.Id == botAppId) continue;

                    var streams = participant.Resource.MediaStreams;
                    if (streams == null) continue;

                    foreach (var stream in streams)
                    {
                        if (uint.TryParse(stream.SourceId, out uint msi) && msi > 0)
                        {
                            string label = stream.Label ?? "";
                            string mediaTypeStr = stream.MediaType.ToString();

                            // 1. Screen sharing stream (VBSS)
                            if (mediaTypeStr.IndexOf("ScreenSharing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("sharing", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    this.vbssSocket?.Subscribe(VideoResolution.HD1080p, msi);
                                    this.graphLogger.Info($"[VBSS Socket] Subscribed to screen share MSI {msi} ({label}) for {participant.Id}");
                                    Console.WriteLine($">>> [VBSS Socket] Subscribed to screen share MSI {msi} ({label})");
                                }
                                catch (Exception ex)
                                {
                                    this.graphLogger.Warn($"[VBSS Socket] Error subscribing MSI {msi}: {ex.Message}");
                                }
                            }
                            // 2. Participant camera / video stream
                            else if (mediaTypeStr.IndexOf("Video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     label.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    this.videoSocket?.Subscribe(VideoResolution.HD720p, msi);
                                    this.graphLogger.Info($"[Video Socket] Subscribed to meeting video MSI {msi} ({label}) for {participant.Id}");
                                    Console.WriteLine($">>> [Video Socket] Subscribed to meeting video MSI {msi} ({label})");
                                }
                                catch (Exception ex)
                                {
                                    this.graphLogger.Warn($"[Video Socket] Error subscribing MSI {msi}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.graphLogger.Warn($"Error subscribing participant media streams: {ex.Message}");
            }
        }

        // ===================================================================
        // 7. Detection of Bot Removal & Group Chat Message Posting
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
                if (Interlocked.Exchange(ref this.joinWelcomeSent, 1) == 0)
                {
                    _ = Task.Run(() => this.PostTextMessageToChatAsync(
                        "🤖 <b>Teams AI Assistant</b> has joined the meeting.<br/>• Audio recording & unmixed speaker capture: <b>Active</b><br/>• Video status broadcast: <b>Active</b><br/>• Screen share photo capture: <b>Ready</b>"));
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
        /// </summary>
        public async Task<bool> PostTextMessageToChatAsync(string htmlContent, bool isRemovalNotification = false)
        {
            if (string.IsNullOrWhiteSpace(this.chatThreadId))
            {
                this.graphLogger.Warn("[Chat Notification] Missing chatThreadId - cannot post message.");
                return false;
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
        // 4. Transcript & Audio Disk Saving (Wind-Down)
        // ===================================================================
        private async Task HandleCallEndedAsync()
        {
            this.graphLogger.Info($"Call {this.Call.Id} terminated - saving all audio, transcripts and metadata.");

            // 1. Flush Audio to D:\Teamsbot\Recordings\<SessionId>\ (Same folder)
            var speakerAudios = this.AudioAggregator.FlushToWavFiles(this.RecordingsManager.SessionDirectory);

            // 2. Transcribe
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

            // 3. Save Transcripts (.txt and .json)
            this.RecordingsManager.SaveTranscripts(entries);

            // 4. Save Session Metadata
            var metadata = new
            {
                SessionId = this.Call.Id,
                StartTime = this.sessionStartTime,
                EndTime = DateTime.Now,
                DurationSeconds = (DateTime.Now - this.sessionStartTime).TotalSeconds,
                SpeakerAudioFilesCount = speakerAudios.Count,
                TranscriptsCount = entries.Count,
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

            var participant = this.Call.Participants.SingleOrDefault(p =>
                p.Resource.IsInLobby == false &&
                p.Resource.MediaStreams.Any(m => m.SourceId == speakerId.ToString()));

            var displayName = participant?.Resource?.Info?.Identity?.User?.DisplayName;
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
            }

            this.latestScreenBitmap?.Dispose();
        }
    }
}
