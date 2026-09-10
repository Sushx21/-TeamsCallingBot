namespace TeamsCallingBot.Bot
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph;
    using Microsoft.Graph.Communications.Calls;
    using Microsoft.Graph.Communications.Calls.Media;
    using Microsoft.Graph.Communications.Client;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Graph.Communications.Resources;
    using Microsoft.Skype.Bots.Media;
    using TeamsCallingBot.Common;
    using TeamsCallingBot.Config;
    using TeamsCallingBot.Storage;

    /// <summary>
    /// Builds the Graph Communications client and joins a specific meeting by its join URL, with a
    /// LOCAL (application-hosted) media session so we receive raw audio frames instead of Microsoft's
    /// service-hosted media.
    ///
    /// This combines two pieces verified separately from Microsoft's own sample repo, which never
    /// demonstrates them together in one sample:
    ///   - meeting-join-by-URL logic: Samples/V1.0Samples/RemoteMediaSamples/IncidentBot/Bot/Bot.cs
    ///   - local media session / AudioSocket setup: Samples/V1.0Samples/LocalMediaSamples/PolicyRecordingBot
    ///
    /// CONFIRMED (2026-09-03, via ildasm against the actual installed DLLs, not guessed): checked
    /// Microsoft.Graph.Communications.Calls.dll directly - JoinMeetingParameters has a real
    /// constructor `.ctor(ChatInfo, MeetingInfo, IMediaSession)`, and ILocalMediaSession (returned by
    /// CreateMediaSession below) implements IMediaSession. The join+local-media-session combination
    /// in JoinCallAsync is a real, valid call - this is no longer an open question.
    /// </summary>
    public class Bot : IDisposable
    {
        private readonly IGraphLogger graphLogger;
        private readonly SemaphoreSlim concurrentCallSlots;
        private readonly BotOptions options;

        public Bot(BotOptions options, IGraphLogger graphLogger)
        {
            this.graphLogger = graphLogger;
            this.options = options;

            // Cap per the architecture doc's own capacity estimate for per-speaker (unmixed) audio
            // capture: ~3-4 concurrent meetings per capture VM, vs ~15 for mixed-stream - per-speaker
            // costs ~5x the bandwidth/compute per meeting. Configurable because that estimate is
            // explicitly marked "needs validation" in the doc, not a measured number.
            this.concurrentCallSlots = new SemaphoreSlim(options.MaxConcurrentCalls);

            var name = this.GetType().Assembly.GetName().Name;
            var builder = new CommunicationsClientBuilder(name, options.AadAppId, graphLogger);

            var authProvider = new AuthenticationProvider(name, options.AadAppId, options.AadAppSecretOrCertThumbprint, graphLogger, options.OverrideBearerToken, options.AadTenantId);

            // FIX (2026-09-04): fire-and-forget, started as early as possible (right here at Bot
            // construction, well before any real Graph notification can arrive) so the OpenID config
            // network fetch is already cached by the time it's actually needed - see WarmupAsync's own
            // comment for why this matters (measured 2.7-4.7s notification-ack latency, likely
            // dominated by this fetch on the first callback of every test run).
            _ = authProvider.WarmupAsync();

            var notificationUrl = new Uri($"https://{options.ServiceDnsName}:{options.InstancePublicPort}/api/calling/notification");

            builder.SetAuthenticationProvider(authProvider);
            builder.SetNotificationUrl(notificationUrl);
            builder.SetServiceBaseUrl(options.PlaceCallEndpointUrl);
            builder.SetMediaPlatformSettings(new MediaPlatformSettings
            {
                MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                {
                    CertificateThumbprint = options.CertificateThumbprint,
                    InstanceInternalPort = options.MediaInstanceInternalPort,
                    InstancePublicIPAddress = System.Net.IPAddress.Parse(options.InstancePublicIpAddress),
                    InstancePublicPort = options.MediaInstancePublicPort,
                    ServiceFqdn = options.ServiceDnsName,
                },
                ApplicationId = options.AadAppId,
            });

            this.Client = builder.Build();
            this.Client.Calls().OnUpdated += this.CallsOnUpdated;
            this.Client.Calls().OnIncoming += this.CallsOnIncoming;
        }

        public ICommunicationsClient Client { get; }

        public ConcurrentDictionary<string, CallHandler> CallHandlers { get; } = new ConcurrentDictionary<string, CallHandler>();

        public void Dispose()
        {
            this.Client.Calls().OnUpdated -= this.CallsOnUpdated;
            this.Client.Calls().OnIncoming -= this.CallsOnIncoming;
            foreach (var handler in this.CallHandlers.Values)
            {
                try { handler.Dispose(); } catch { }
            }
            this.Client?.Dispose();
            this.concurrentCallSlots.Dispose();
        }

        private IMediaSession CreateMediaSession()
        {
            return this.Client.CreateMediaSession(
                new AudioSocketSettings
                {
                    StreamDirections = StreamDirection.Sendrecv,
                    SupportedAudioFormat = AudioFormat.Pcm16K,
                    ReceiveUnmixedMeetingAudio = true,
                },
                new[]
                {
                    new VideoSocketSettings
                    {
                        StreamDirections = StreamDirection.Sendrecv,
                        ReceiveColorFormat = VideoColorFormat.NV12,
                        SupportedSendVideoFormats = new List<VideoFormat>
                        {
                            VideoFormat.NV12_1280x720_15Fps,
                            VideoFormat.NV12_640x360_15Fps,
                            VideoFormat.NV12_1280x720_30Fps,
                        },
                    }
                },
                new VideoSocketSettings
                {
                    StreamDirections = StreamDirection.Recvonly,
                    ReceiveColorFormat = VideoColorFormat.NV12,
                    MediaType = MediaType.Vbss,
                },
                null,
                mediaSessionId: Guid.NewGuid());
        }

        public async Task<ICall> JoinCallAsync(string meetingJoinUrl, bool recordVideo = true)
        {
            if (string.IsNullOrWhiteSpace(meetingJoinUrl))
            {
                throw new ArgumentNullException(nameof(meetingJoinUrl));
            }

            ChatInfo chatInfo;
            MeetingInfo meetingInfo;
            string tenantId;
            try
            {
                (chatInfo, meetingInfo, tenantId) = await JoinInfo.ParseJoinURLAsync(meetingJoinUrl).ConfigureAwait(false);
            }
            catch (Exception parseEx)
            {
                this.graphLogger.Error(parseEx, $"[Join] Could not parse the join URL - NOT attempting to join. URL: {meetingJoinUrl}");
                throw;
            }

            var organizerId = (meetingInfo as OrganizerMeetingInfo)?.Organizer?.User?.Id;
            return await this.JoinCallByCoordinatesAsync(
                chatInfo?.ThreadId,
                tenantId,
                organizerId,
                recordVideo,
                meetingJoinUrl).ConfigureAwait(false);
        }

        /// <summary>
        /// Joins a Teams meeting directly by its threadId, tenantId, and organizerOid.
        /// Used by the in-chat auto-extraction engine (!join, #joincall) where Teams provides
        /// the exact coordinates in the message activity headers without needing any meeting URL.
        /// </summary>
        public async Task<ICall> JoinCallByCoordinatesAsync(
            string threadId,
            string tenantId = null,
            string organizerOid = null,
            bool recordVideo = true,
            string meetingJoinUrl = null)
        {
            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new ArgumentNullException(nameof(threadId), "ThreadId cannot be null or empty.");
            }

            // 1. De-duplication check: if the bot is already handling this meeting, don't spin up another call!
            foreach (var kvp in this.CallHandlers)
            {
                string existingThread = kvp.Value.GetEffectiveChatThreadId();
                if (!string.IsNullOrWhiteSpace(existingThread) &&
                    string.Equals(existingThread, threadId, StringComparison.OrdinalIgnoreCase))
                {
                    this.graphLogger.Info($"[Join] Bot is already in meeting thread '{threadId}'. Updating screen recording to {recordVideo}.");
                    Console.WriteLine($">>> [Join] Bot already in meeting thread '{threadId}'. Screen recording updated: {recordVideo}");
                    kvp.Value.RecordScreenShareOverride = recordVideo;
                    return kvp.Value.Call;
                }
            }

            // 2. Concurrency limit check
            if (!this.concurrentCallSlots.Wait(0))
            {
                throw new InvalidOperationException(
                    $"Refusing to join - already at the configured concurrency cap ({this.CallHandlers.Count} active calls).");
            }

            // 2b. Distributed Firestore Lock check (cross-instance deduplication for 100 concurrent instances)
            bool lockAcquired = await FirestoreMeetingLockService.Instance.TryAcquireLockAsync(
                threadId,
                meetingJoinUrl ?? threadId).ConfigureAwait(false);

            if (!lockAcquired)
            {
                this.concurrentCallSlots.Release();
                this.graphLogger.Warn($"[Join] Meeting '{threadId}' is already locked or joined by another instance. Skipping duplicate join.");
                Console.WriteLine($">>> [Join] Skipped duplicate join: meeting '{threadId}' is already locked or joined by another instance.");
                return null;
            }

            try
            {
                string effectiveTenantId = !string.IsNullOrWhiteSpace(tenantId)
                    ? tenantId
                    : (this.options?.AadTenantId ?? "f35425af-4755-4e0c-b1bb-b3cb9f1c6afd");

                string effectiveOrganizerId = !string.IsNullOrWhiteSpace(organizerOid)
                    ? organizerOid
                    : (this.options?.AadAppId ?? "9076f721-7295-4f81-9817-4743b7153c2c");

                this.graphLogger.Info(
                    $"[Join] Direct coordinate join: threadId={threadId}, " +
                    $"organizerOid={effectiveOrganizerId}, " +
                    $"tenantId={effectiveTenantId}, recordVideo={recordVideo}.");
                Console.WriteLine($">>> [Join] Auto-joining meeting via coordinates: threadId={threadId} | video={recordVideo}");

                var chatInfo = new ChatInfo
                {
                    ThreadId = threadId,
                    MessageId = "0",
                    ReplyChainMessageId = "0"
                };

                var meetingInfo = new OrganizerMeetingInfo
                {
                    Organizer = new IdentitySet
                    {
                        User = new Identity
                        {
                            Id = effectiveOrganizerId
                        }
                    }
                };
                meetingInfo.Organizer.User.SetTenantId(effectiveTenantId);

                var mediaSession = this.CreateMediaSession();
                var scenarioId = Guid.NewGuid();

                var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
                {
                    AllowGuestToBypassLobby = true,
                    TenantId = effectiveTenantId,
                };

                var call = await this.Client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);

                CallHandler handler;
                try
                {
                    handler = new CallHandler(call, this.graphLogger, chatInfo.ThreadId, this.options?.OverrideBearerToken, meetingJoinUrl);
                    handler.RecordScreenShareOverride = recordVideo;
                }
                catch (Exception ex)
                {
                    this.graphLogger.Error(ex, $"CallHandler construction failed for call {call.Id} - hanging up.");
                    try
                    {
                        await call.DeleteAsync().ConfigureAwait(false);
                    }
                    catch (Exception deleteEx)
                    {
                        this.graphLogger.Error(deleteEx, $"Failed to hang up orphaned call {call.Id}.");
                    }

                    throw;
                }

                this.CallHandlers[call.Id] = handler;
                this.graphLogger.Info($"[Join] Call CREATED: {call.Id} ({this.CallHandlers.Count} active calls). Waiting for CallState.Established...");
                return call;
            }
            catch
            {
                this.concurrentCallSlots.Release();
                _ = FirestoreMeetingLockService.Instance.ReleaseLockAsync(threadId);
                throw;
            }
        }

        private void CallsOnIncoming(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            foreach (var call in args.AddedResources)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!this.concurrentCallSlots.Wait(0))
                        {
                            this.graphLogger.Warn($"Rejecting incoming call {call.Id} - at concurrency cap.");
                            return;
                        }

                        this.graphLogger.Info($"[Incoming Call] Detected incoming call {call.Id}. Answering with local media session...");
                        Console.WriteLine($">>> [Incoming Call] Answering incoming call {call.Id} into meeting!");

                        var mediaSession = this.CreateMediaSession();
                        var scenarioId = Guid.NewGuid();
                        await call.AnswerAsync(mediaSession, null, scenarioId).ConfigureAwait(false);

                        var handler = new CallHandler(call, this.graphLogger, call.Resource.ChatInfo?.ThreadId, this.options?.OverrideBearerToken);
                        this.CallHandlers[call.Id] = handler;
                        this.graphLogger.Info($"[Incoming Call] Successfully answered call {call.Id} ({this.CallHandlers.Count} active calls now).");
                        Console.WriteLine($">>> [Incoming Call] Successfully joined meeting via direct invite!");
                    }
                    catch (Exception ex)
                    {
                        this.concurrentCallSlots.Release();
                        this.graphLogger.Error(ex, $"Failed to answer incoming call {call.Id}.");
                        Console.WriteLine($">>> [Incoming Call Failed] {ex.Message}");
                    }
                });
            }
        }

        private void CallsOnUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            foreach (var call in args.RemovedResources)
            {
                if (this.CallHandlers.TryRemove(call.Id, out var handler))
                {
                    handler.Dispose();
                    this.concurrentCallSlots.Release();
                }
            }
        }
    }
}
