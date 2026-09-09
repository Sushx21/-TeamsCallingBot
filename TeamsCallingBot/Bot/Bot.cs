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

            var authProvider = new AuthenticationProvider(name, options.AadAppId, options.AadAppSecretOrCertThumbprint, graphLogger, options.OverrideBearerToken);

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

        public async Task<ICall> JoinCallAsync(string meetingJoinUrl)
        {
            // Fails fast instead of silently overloading the VM past the capacity the architecture
            // doc itself calls out as the limiting factor for per-speaker capture. This is a real
            // decision point, not a nicety - past this limit, per-call audio/Whisper throughput
            // degrades for EVERY call already in progress, not just the new one.
            if (!this.concurrentCallSlots.Wait(0))
            {
                throw new InvalidOperationException(
                    $"Refusing to join - already at the configured concurrency cap ({this.CallHandlers.Count} active calls).");
            }

            try
            {
                ChatInfo chatInfo;
                MeetingInfo meetingInfo;
                string tenantId;
                try
                {
                    (chatInfo, meetingInfo, tenantId) = await JoinInfo.ParseJoinURLAsync(meetingJoinUrl).ConfigureAwait(false);
                }
                catch (Exception parseEx)
                {
                    // A parse failure here means we never even attempt to join - make that explicit
                    // rather than letting it look like a generic join error later.
                    this.graphLogger.Error(parseEx, $"[Join] Could not parse the join URL - NOT attempting to join. URL: {meetingJoinUrl}");
                    throw;
                }

                // Log exactly what coordinates we derived. Missing organizer/tenant is the usual reason
                // a "successful" join never produces a face card, so surface it up front.
                var organizerId = (meetingInfo as OrganizerMeetingInfo)?.Organizer?.User?.Id;
                this.graphLogger.Info(
                    $"[Join] Parsed join coordinates: threadId={(string.IsNullOrEmpty(chatInfo?.ThreadId) ? "(MISSING)" : chatInfo.ThreadId)}, " +
                    $"organizerOid={(string.IsNullOrEmpty(organizerId) ? "(MISSING)" : organizerId)}, " +
                    $"tenantId={(string.IsNullOrEmpty(tenantId) ? "(MISSING)" : tenantId)}. " +
                    "Any (MISSING) value here means the join will likely fail silently (no face card).");

                var mediaSession = this.CreateMediaSession();

                var scenarioId = Guid.NewGuid();

                // Confirmed real constructor - see class-level comment above.
                var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
                {
                    // Explicitly request to bypass lobby so bot is immediately admitted into the active meeting
                    AllowGuestToBypassLobby = true,
                    TenantId = tenantId,
                };

                var call = await this.Client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);

                CallHandler handler;
                try
                {
                    handler = new CallHandler(call, this.graphLogger, chatInfo?.ThreadId, this.options?.OverrideBearerToken);
                }
                catch (Exception ex)
                {
                    this.graphLogger.Error(ex, $"CallHandler construction failed for call {call.Id} - hanging up the now-orphaned Graph-side call.");
                    try
                    {
                        await call.DeleteAsync().ConfigureAwait(false);
                    }
                    catch (Exception deleteEx)
                    {
                        this.graphLogger.Error(deleteEx, $"Failed to hang up orphaned call {call.Id} after CallHandler construction failed.");
                    }

                    throw;
                }

                this.CallHandlers[call.Id] = handler;

                // NOTE: this only means the call was CREATED with Graph, NOT that the bot has joined.
                // Actual join success is when CallHandler logs CallState.Established; if it doesn't
                // within ~45s the join watchdog will warn. Do not treat this line as "joined".
                this.graphLogger.Info($"[Join] Call CREATED (not yet joined): {call.Id} ({this.CallHandlers.Count} active calls now). Waiting for CallState.Established...");
                return call;
            }
            catch
            {
                this.concurrentCallSlots.Release();
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
