namespace TeamsCallingBot.Http
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using TeamsCallingBot.Bot;

    /// <summary>
    /// Manual trigger for testing: POST a meeting join URL here and the bot joins it.
    /// Real triggering (Cloud Run deciding when to join, per the architecture doc) is a separate,
    /// later integration - this exists purely so the VM side can be smoke-tested on its own first.
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
            var call = await this.bot.JoinCallAsync(request.MeetingJoinUrl).ConfigureAwait(false);
            return this.Ok(new { callId = call.Id });
        }
    }

    public class JoinCallRequest
    {
        public string MeetingJoinUrl { get; set; }
    }
}
