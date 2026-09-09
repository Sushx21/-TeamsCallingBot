namespace TeamsCallingBot.Http
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using TeamsCallingBot.Bot;
    using TeamsCallingBot.Common;

    /// <summary>
    /// Dedicated enterprise endpoint for Power Automate and Outlook calendar invite forwarders.
    ///
    /// When users forward meeting invitations to the TSL AI mailbox, Power Automate captures the
    /// event and calls POST /api/powerautomate/register-meeting.
    ///
    /// If the meeting is already live (or AutoJoinNow is set), the bot joins immediately.
    /// If scheduled for the future, it queues into the MeetingRegistryService for automated on-time joining.
    /// </summary>
    [Route("api/powerautomate")]
    [Route("api/meetings")]
    [ApiController]
    public class PowerAutomateController : ControllerBase
    {
        private readonly Bot bot;
        private readonly MeetingRegistryService registry;

        public PowerAutomateController(Bot bot, MeetingRegistryService registry)
        {
            this.bot = bot ?? throw new ArgumentNullException(nameof(bot));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        [HttpPost("register-meeting")]
        [HttpPost("register")]
        public async Task<IActionResult> RegisterMeetingAsync([FromBody] PowerAutomateMeetingRequest request)
        {
            if (request == null)
            {
                return this.BadRequest(new { error = "Request body is required." });
            }

            string joinUrl = request.MeetingJoinUrl?.Trim();
            string threadId = request.ThreadId?.Trim();
            string subject = request.Subject?.Trim();

            // 1. If raw email/invite body is provided, auto-extract meeting coordinates
            if (!string.IsNullOrWhiteSpace(request.RawBody))
            {
                var parsed = EmailMeetingInviteParser.ParseEmailContent(request.RawBody);
                if (parsed.Success && !string.IsNullOrWhiteSpace(parsed.JoinUrl))
                {
                    joinUrl = parsed.JoinUrl;
                    subject = subject ?? parsed.Subject;
                }
            }

            if (string.IsNullOrWhiteSpace(joinUrl) && string.IsNullOrWhiteSpace(threadId))
            {
                return this.BadRequest(new
                {
                    error = "Could not resolve a valid meeting join URL or Thread ID. Provide 'MeetingJoinUrl', 'ThreadId', or forward the invite email body in 'RawBody'."
                });
            }

            DateTime? startTimeUtc = null;
            if (DateTime.TryParse(request.StartTime, out var parsedStart))
            {
                startTimeUtc = parsedStart.ToUniversalTime();
            }

            DateTime? endTimeUtc = null;
            if (DateTime.TryParse(request.EndTime, out var parsedEnd))
            {
                endTimeUtc = parsedEnd.ToUniversalTime();
            }

            string organizer = !string.IsNullOrWhiteSpace(request.OrganizerName)
                ? $"{request.OrganizerName} ({request.OrganizerEmail})"
                : request.OrganizerEmail;

            // 2. Register with MeetingRegistryService
            var reg = this.registry.Register(
                joinUrl: joinUrl,
                subject: subject,
                startTimeUtc: startTimeUtc,
                endTimeUtc: endTimeUtc,
                organizer: organizer,
                recordVideo: request.RecordVideo,
                threadId: threadId,
                source: "PowerAutomate");

            // 3. Determine if we should join immediately:
            // - If explicitly requested via AutoJoinNow
            // - OR if meeting is scheduled right now (current time >= startTime - 2 minutes)
            bool shouldJoinNow = request.AutoJoinNow;
            if (!shouldJoinNow && startTimeUtc.HasValue)
            {
                var diff = DateTime.UtcNow - startTimeUtc.Value;
                if (diff.TotalMinutes >= -2.0 && diff.TotalMinutes <= 60.0)
                {
                    shouldJoinNow = true;
                }
            }

            if (shouldJoinNow && !string.IsNullOrWhiteSpace(joinUrl))
            {
                try
                {
                    this.registry.MarkJoining(reg.Id);
                    var call = await this.bot.JoinCallAsync(joinUrl, request.RecordVideo).ConfigureAwait(false);
                    this.registry.MarkJoined(reg.Id, call.Id);

                    return this.Ok(new
                    {
                        status = "JoinedImmediately",
                        registrationId = reg.Id,
                        callId = call.Id,
                        subject = reg.Subject,
                        meetingJoinUrl = joinUrl,
                        recordVideo = request.RecordVideo,
                        message = "Bot successfully initiated connection to the live meeting."
                    });
                }
                catch (Exception ex)
                {
                    this.registry.MarkFailed(reg.Id, ex.Message);
                    return this.StatusCode(500, new
                    {
                        status = "JoinFailed",
                        registrationId = reg.Id,
                        error = ex.Message,
                        meetingJoinUrl = joinUrl
                    });
                }
            }

            return this.Ok(new
            {
                status = "Scheduled",
                registrationId = reg.Id,
                subject = reg.Subject,
                scheduledStartTimeUtc = reg.ScheduledStartTimeUtc,
                recordVideo = request.RecordVideo,
                message = "Meeting registered successfully. The bot will automatically join at the scheduled time."
            });
        }

        [HttpGet("registered")]
        [HttpGet("list")]
        public IActionResult ListRegisteredMeetings()
        {
            var all = this.registry.GetAll();
            return this.Ok(new
            {
                totalRegistered = all.Count,
                activeBotCalls = this.bot.CallHandlers.Count,
                meetings = all
            });
        }
    }

    public class PowerAutomateMeetingRequest
    {
        public string Subject { get; set; }
        public string MeetingJoinUrl { get; set; }
        public string ThreadId { get; set; }
        public string StartTime { get; set; }
        public string EndTime { get; set; }
        public string OrganizerEmail { get; set; }
        public string OrganizerName { get; set; }
        public string RawBody { get; set; }
        public bool RecordVideo { get; set; } = true;
        public bool AutoJoinNow { get; set; } = false;
    }
}
