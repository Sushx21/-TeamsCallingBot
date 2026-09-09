namespace TeamsCallingBot.Http
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using TeamsCallingBot.Bot;

    /// <summary>
    /// Manual trigger for testing: POST a meeting join URL here and the bot joins it.
    /// Real triggering (Cloud Run deciding when to join, per the architecture doc) is a separate,
    /// later integration - this exists purely so the VM side can be smoke-tested on its own first.
    ///
    /// CONCURRENT MEETINGS: POST here once PER meeting, each with that meeting's long-format
    /// join URL (Graph event.onlineMeeting.joinUrl - the .../19:meeting_...@thread.v2/0?context={tid,oid}
    /// form). Each call runs independently up to BotOptions.MaxConcurrentCalls; the response carries
    /// that meeting's own callId, which you then use with the management endpoints.
    /// </summary>
    [Route("api/testjoin")]
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
            if (request == null || string.IsNullOrWhiteSpace(request.MeetingJoinUrl))
            {
                return this.BadRequest(new { error = "MeetingJoinUrl is required." });
            }

            try
            {
                var call = await this.bot.JoinCallAsync(request.MeetingJoinUrl).ConfigureAwait(false);
                return this.Ok(new { callId = call.Id, note = "Call CREATED. It is only truly joined once its state reaches Established - check the logs." });
            }
            catch (ArgumentException ex)
            {
                // Bad / unparseable join URL (e.g. short link + passcode). 400 with the actionable reason.
                return this.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // Concurrency cap reached (see BotOptions.MaxConcurrentCalls). 429 = try again later.
                return this.StatusCode(429, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, new { error = "Join failed unexpectedly.", detail = ex.Message });
            }
        }
    }

    public class JoinCallRequest
    {
        public string MeetingJoinUrl { get; set; }
    }
}
