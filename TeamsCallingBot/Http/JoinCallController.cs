namespace TeamsCallingBot.Http
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using TeamsCallingBot.Bot;

    /// <summary>
    /// Trigger for meeting joins: POST one or multiple meeting join URLs here and the bot joins them concurrently.
    /// Supports:
    /// 1. Single meeting join URL: { "MeetingJoinUrl": "..." }
    /// 2. Multiple meeting URLs in array: { "MeetingJoinUrls": ["url1", "url2", "url3"] }
    /// 3. Semicolon/newline separated URLs in "MeetingJoinUrl".
    /// 
    /// Each call runs independently up to BotOptions.MaxConcurrentCalls (default 10).
    /// </summary>
    [Route("api/testjoin")]
    [Route("api/join")]
    [Route("api/calling/join")]
    [Route("api/calls/join")]
    public class JoinCallController : Controller
    {
        private readonly TeamsCallingBot.Bot.Bot bot;
        private readonly Common.MeetingRegistryService registry;

        public JoinCallController(TeamsCallingBot.Bot.Bot bot, Common.MeetingRegistryService registry = null)
        {
            this.bot = bot;
            this.registry = registry;
        }

        [HttpPost]
        public async Task<IActionResult> JoinAsync([FromBody] JoinCallRequest request)
        {
            if (request == null)
            {
                return this.BadRequest(new { success = false, error = "Request body is required.", message = "Request body is required." });
            }

            var effectiveUserAdid = !string.IsNullOrWhiteSpace(request.UserAdid) ? request.UserAdid : request.Adid;

            // Auto-register meetingId <-> meetingLink in local table resolver so any short-key or manual join can find it
            if (!string.IsNullOrWhiteSpace(request.MeetingId) && !string.IsNullOrWhiteSpace(request.MeetingLink))
            {
                Common.MeetingLinkResolver.RegisterTableMapping(request.MeetingId, request.MeetingLink);
            }

            var urls = new List<string>();

            if (request.MeetingJoinUrls != null && request.MeetingJoinUrls.Count > 0)
            {
                urls.AddRange(request.MeetingJoinUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()));
            }

            var singleInputUrl = !string.IsNullOrWhiteSpace(request.MeetingLink)
                ? request.MeetingLink
                : (!string.IsNullOrWhiteSpace(request.MeetingUrl)
                    ? request.MeetingUrl
                    : (!string.IsNullOrWhiteSpace(request.MeetingJoinUrl)
                        ? request.MeetingJoinUrl
                        : request.MeetingId));

            if (!string.IsNullOrWhiteSpace(singleInputUrl))
            {
                var split = singleInputUrl
                    .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(u => u.Trim())
                    .Where(u => !string.IsNullOrWhiteSpace(u));
                urls.AddRange(split);
            }

            urls = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (urls.Count == 0)
            {
                return this.BadRequest(new { success = false, error = "At least one meetingLink, meetingUrl, or meetingId is required.", message = "At least one meetingLink, meetingUrl, or meetingId is required." });
            }

            var effectivePrompt = !string.IsNullOrWhiteSpace(request.Prompt)
                ? request.Prompt
                : (!string.IsNullOrWhiteSpace(request.MeetingName) ? $"Meeting name: {request.MeetingName}" : null);

            // If scheduled in the future (> 2 mins away), register with scheduler
            if (this.registry != null && request.MeetingStartTime.HasValue && request.MeetingStartTime.Value > DateTime.UtcNow.AddMinutes(2))
            {
                var registered = this.registry.Register(
                    singleInputUrl,
                    request.MeetingName ?? request.Subject,
                    request.MeetingStartTime.Value.ToUniversalTime(),
                    request.MeetingEndTime?.ToUniversalTime(),
                    effectiveUserAdid,
                    request.RecordVideo,
                    request.MeetingId,
                    "TableSync");

                return this.Ok(new
                {
                    success = true,
                    status = "Scheduled",
                    meetingId = request.MeetingId,
                    meetingLink = singleInputUrl,
                    meetingName = request.MeetingName,
                    scheduledStartTimeUtc = registered.ScheduledStartTimeUtc,
                    message = $"Meeting '{request.MeetingName}' registered in scheduler. Will auto-join at scheduled time ({registered.ScheduledStartTimeUtc:u})."
                });
            }

            // Single URL case (for direct compatibility with teamsBot.js and API callers)
            if (urls.Count == 1)
            {
                var singleUrl = urls[0];
                try
                {
                    var call = await this.bot.JoinCallAsync(
                        singleUrl,
                        request.RecordVideo,
                        effectiveUserAdid,
                        request.TranscriptFileName,
                        effectivePrompt).ConfigureAwait(false);

                    if (call == null)
                    {
                        return this.Ok(new
                        {
                            success = false,
                            status = "AlreadyJoinedOrLocked",
                            callId = (string)null,
                            meetingUrl = singleUrl,
                            message = "I'm already joining or have joined this meeting. Please wait a moment."
                        });
                    }

                    return this.Ok(new
                    {
                        success = true,
                        status = "CallCreated",
                        callId = call.Id,
                        meetingUrl = singleUrl,
                        userAdid = effectiveUserAdid,
                        transcriptfilename = request.TranscriptFileName,
                        message = "AI Meeting Assistant joined the call successfully."
                    });
                }
                catch (ArgumentException ex)
                {
                    return this.BadRequest(new { success = false, error = ex.Message, message = "Could not parse meeting link: " + ex.Message, meetingUrl = singleUrl });
                }
                catch (InvalidOperationException ex)
                {
                    return this.StatusCode(429, new { success = false, error = ex.Message, message = "Bot busy at maximum capacity: " + ex.Message, meetingUrl = singleUrl });
                }
                catch (Exception ex)
                {
                    return this.StatusCode(500, new { success = false, error = "Join failed unexpectedly.", detail = ex.Message, message = "Internal error joining meeting: " + ex.Message, meetingUrl = singleUrl });
                }
            }

            // Multiple URLs case - staggered joins with 1.5s interval to prevent Graph 500#1203003 burst errors
            var results = new List<object>();
            for (int i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                if (i > 0)
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                }

                try
                {
                    var call = await this.bot.JoinCallAsync(url).ConfigureAwait(false);
                    results.Add(new
                    {
                        success = call != null,
                        meetingJoinUrl = url,
                        callId = call?.Id,
                        status = call != null ? "CallCreated" : "AlreadyJoinedOrLocked",
                        error = call == null ? "Meeting is already locked or joined by another instance." : (string)null
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        success = false,
                        meetingJoinUrl = url,
                        callId = (string)null,
                        status = "Failed",
                        error = ex.Message
                    });
                }
            }

            return this.Ok(new
            {
                message = $"Processed {results.Count} join requests with staggered scheduling.",
                activeCallsCount = this.bot.CallHandlers.Count,
                results = results
            });
        }

        /// <summary>
        /// Production endpoint for forwarded meeting invites (e.g. forwarded to tsl-ai mailbox).
        /// Accepts raw email body text or HTML, auto-extracts the meeting coordinates, and joins.
        /// </summary>
        [HttpPost("invite/forward")]
        public async Task<IActionResult> ForwardedInviteAsync([FromBody] ForwardedInviteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.EmailContent))
            {
                return this.BadRequest(new { error = "EmailContent is required." });
            }

            var parsed = Common.EmailMeetingInviteParser.ParseEmailContent(request.EmailContent);
            if (!parsed.Success || string.IsNullOrWhiteSpace(parsed.JoinUrl))
            {
                return this.BadRequest(new { error = parsed.Error ?? "Could not extract meeting URL from email content." });
            }

            try
            {
                var call = await this.bot.JoinCallAsync(parsed.JoinUrl, request.RecordVideo).ConfigureAwait(false);
                return this.Ok(new
                {
                    status = "CallCreated",
                    callId = call.Id,
                    meetingJoinUrl = parsed.JoinUrl,
                    recordVideo = request.RecordVideo,
                    subject = parsed.Subject
                });
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, new { error = ex.Message, joinUrl = parsed.JoinUrl });
            }
        }

        /// <summary>
        /// Registers a meeting mapping in the local table placeholder.
        /// Allows mapping a short passcode link, meeting ID, or custom key to the real 19:meeting_...@thread.v2 URL or threadId.
        /// </summary>
        [HttpPost("map")]
        [HttpPost("/api/meetings/map")]
        public IActionResult RegisterMapping([FromBody] MeetingMappingRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.MeetingUrlOrThreadId))
            {
                return this.BadRequest(new { error = "Key and MeetingUrlOrThreadId are required." });
            }

            Common.MeetingLinkResolver.RegisterTableMapping(request.Key, request.MeetingUrlOrThreadId);
            return this.Ok(new
            {
                success = true,
                message = "Meeting mapping registered in table successfully.",
                key = request.Key,
                mappedTarget = request.MeetingUrlOrThreadId
            });
        }

        /// <summary>
        /// Production endpoint for syncing one or multiple meetings directly from your database / BigQuery table.
        /// Accepts a single meeting JSON record or an array of meeting records.
        /// </summary>
        [HttpPost("table")]
        [HttpPost("/api/meetings/table")]
        public async Task<IActionResult> TableSyncAsync([FromBody] Newtonsoft.Json.Linq.JToken token)
        {
            if (token == null)
            {
                return this.BadRequest(new { error = "Request body is required." });
            }

            var requests = new List<JoinCallRequest>();
            if (token.Type == Newtonsoft.Json.Linq.JTokenType.Array)
            {
                requests = token.ToObject<List<JoinCallRequest>>();
            }
            else if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
            {
                requests.Add(token.ToObject<JoinCallRequest>());
            }

            if (requests.Count == 0)
            {
                return this.BadRequest(new { error = "No valid meeting records found in request." });
            }

            var results = new List<object>();
            foreach (var req in requests)
            {
                var effectiveAdid = !string.IsNullOrWhiteSpace(req.UserAdid) ? req.UserAdid : req.Adid;
                var urlOrId = !string.IsNullOrWhiteSpace(req.MeetingLink)
                    ? req.MeetingLink
                    : (!string.IsNullOrWhiteSpace(req.MeetingUrl) ? req.MeetingUrl : req.MeetingId);

                if (!string.IsNullOrWhiteSpace(req.MeetingId) && !string.IsNullOrWhiteSpace(req.MeetingLink))
                {
                    Common.MeetingLinkResolver.RegisterTableMapping(req.MeetingId, req.MeetingLink);
                }

                if (this.registry != null && req.MeetingStartTime.HasValue && req.MeetingStartTime.Value > DateTime.UtcNow.AddMinutes(2))
                {
                    var registered = this.registry.Register(
                        urlOrId,
                        req.MeetingName ?? req.Subject,
                        req.MeetingStartTime.Value.ToUniversalTime(),
                        req.MeetingEndTime?.ToUniversalTime(),
                        effectiveAdid,
                        req.RecordVideo,
                        req.MeetingId,
                        "TableSync");

                    results.Add(new
                    {
                        meetingId = req.MeetingId,
                        meetingName = req.MeetingName ?? req.Subject,
                        status = "Scheduled",
                        scheduledStartTimeUtc = registered.ScheduledStartTimeUtc
                    });
                }
                else
                {
                    try
                    {
                        var prompt = !string.IsNullOrWhiteSpace(req.Prompt) ? req.Prompt : (!string.IsNullOrWhiteSpace(req.MeetingName) ? $"Meeting name: {req.MeetingName}" : null);
                        var call = await this.bot.JoinCallAsync(urlOrId, req.RecordVideo, effectiveAdid, req.TranscriptFileName, prompt).ConfigureAwait(false);
                        results.Add(new
                        {
                            meetingId = req.MeetingId,
                            meetingName = req.MeetingName ?? req.Subject,
                            status = call != null ? "CallCreated" : "AlreadyJoinedOrLocked",
                            callId = call?.Id
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            meetingId = req.MeetingId,
                            meetingName = req.MeetingName ?? req.Subject,
                            status = "Failed",
                            error = ex.Message
                        });
                    }
                }
            }

            return this.Ok(new
            {
                success = true,
                message = $"Processed {results.Count} table record(s) successfully.",
                count = results.Count,
                results = results
            });
        }
    }

    public class MeetingMappingRequest
    {
        public string Key { get; set; }
        public string MeetingUrlOrThreadId { get; set; }
    }

    public class JoinCallRequest
    {
        public string MeetingJoinUrl { get; set; }
        public List<string> MeetingJoinUrls { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingUrl")]
        public string MeetingUrl { get; set; }

        [Newtonsoft.Json.JsonProperty("userAdid")]
        public string UserAdid { get; set; }

        [Newtonsoft.Json.JsonProperty("transcriptfilename")]
        public string TranscriptFileName { get; set; }

        [Newtonsoft.Json.JsonProperty("prompt")]
        public string Prompt { get; set; }

        public bool RecordVideo { get; set; } = true;

        // --- Production Table Schema Support ---
        [Newtonsoft.Json.JsonProperty("adid")]
        public string Adid { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingId")]
        public string MeetingId { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingLink")]
        public string MeetingLink { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingName")]
        public string MeetingName { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingStartTime")]
        public DateTime? MeetingStartTime { get; set; }

        [Newtonsoft.Json.JsonProperty("meetingEndTime")]
        public DateTime? MeetingEndTime { get; set; }

        [Newtonsoft.Json.JsonProperty("eventId")]
        public string EventId { get; set; }

        [Newtonsoft.Json.JsonProperty("dialInMeetingId")]
        public string DialInMeetingId { get; set; }

        [Newtonsoft.Json.JsonProperty("joinMeetingId")]
        public string JoinMeetingId { get; set; }

        [Newtonsoft.Json.JsonProperty("joinPasscode")]
        public string JoinPasscode { get; set; }

        [Newtonsoft.Json.JsonProperty("isRecurring")]
        public bool? IsRecurring { get; set; }

        [Newtonsoft.Json.JsonProperty("jobStatus")]
        public string JobStatus { get; set; }

        [Newtonsoft.Json.JsonProperty("organizerEmail")]
        public string OrganizerEmail { get; set; }

        [Newtonsoft.Json.JsonProperty("organizerUpn")]
        public string OrganizerUpn { get; set; }

        [Newtonsoft.Json.JsonProperty("organizerUserId")]
        public string OrganizerUserId { get; set; }

        [Newtonsoft.Json.JsonProperty("subject")]
        public string Subject { get; set; }

        [Newtonsoft.Json.JsonProperty("threadId")]
        public string ThreadId { get; set; }
    }

    public class ForwardedInviteRequest
    {
        public string EmailContent { get; set; }
        public bool RecordVideo { get; set; } = true;
    }
}
