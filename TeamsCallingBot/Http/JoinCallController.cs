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

        public JoinCallController(TeamsCallingBot.Bot.Bot bot)
        {
            this.bot = bot;
        }

        [HttpPost]
        public async Task<IActionResult> JoinAsync([FromBody] JoinCallRequest request)
        {
            if (request == null)
            {
                return this.BadRequest(new { success = false, error = "Request body is required.", message = "Request body is required." });
            }

            var urls = new List<string>();

            if (request.MeetingJoinUrls != null && request.MeetingJoinUrls.Count > 0)
            {
                urls.AddRange(request.MeetingJoinUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()));
            }

            var singleInputUrl = !string.IsNullOrWhiteSpace(request.MeetingUrl)
                ? request.MeetingUrl
                : request.MeetingJoinUrl;

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
                return this.BadRequest(new { success = false, error = "At least one meetingUrl or MeetingJoinUrl is required.", message = "At least one meetingUrl or MeetingJoinUrl is required." });
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
                        request.UserAdid,
                        request.TranscriptFileName,
                        request.Prompt).ConfigureAwait(false);

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
                        userAdid = request.UserAdid,
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
    }

    public class ForwardedInviteRequest
    {
        public string EmailContent { get; set; }
        public bool RecordVideo { get; set; } = true;
    }
}
