namespace TeamsCallingBot.Http
{
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Extensions;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Graph.Communications.Client;
    using TeamsCallingBot.Bot;

    /// <summary>
    /// Receives Graph's call-state-change webhooks and hands them to the Communications SDK.
    ///
    /// FLAGGED AS LEAST-VERIFIED FILE IN THIS PROJECT: Microsoft's own sample controllers for this
    /// (PlatformCallController in PolicyRecordingBot/IncidentBot) are written against classic OWIN
    /// (System.Web.Http, HttpRequestMessage-native), not ASP.NET Core's HttpRequest. Since this
    /// project uses ASP.NET Core (net472 + Microsoft.AspNetCore.Mvc) for a cleaner modern hosting
    /// model, ToHttpRequestMessageAsync below manually bridges the two - a well-known conversion
    /// pattern, but not copied from a verified source file like the rest of this project was.
    /// Test this specific file first once real Graph notifications start arriving.
    /// </summary>
    [Route(HttpRouteConstants.CallSignalingRoutePrefix)]
    public class PlatformCallController : Controller
    {
        private readonly TeamsCallingBot.Bot.Bot bot;

        public PlatformCallController(TeamsCallingBot.Bot.Bot bot)
        {
            this.bot = bot;
        }

        [HttpPost]
        [Route(HttpRouteConstants.OnNotificationRequestRoute)]
        public async Task<IActionResult> OnNotificationRequestAsync()
        {
            var requestMessage = await ToHttpRequestMessageAsync(this.Request).ConfigureAwait(false);
            var responseMessage = await this.bot.Client.ProcessNotificationAsync(requestMessage).ConfigureAwait(false);

            // FIX (2026-09-04): responseMessage.Content can genuinely be null here - hit a live
            // NullReferenceException on a real Graph notification callback (not every ProcessNotificationAsync
            // response sets Content, e.g. a bare 200 ack). This was exactly the risk this file's own
            // class-level comment already flagged as "least-verified" - confirmed live, now guarded.
            var content = responseMessage.Content == null
                ? string.Empty
                : await responseMessage.Content.ReadAsStringAsync().ConfigureAwait(false);
            return this.StatusCode((int)responseMessage.StatusCode, content);
        }

        private static async Task<HttpRequestMessage> ToHttpRequestMessageAsync(HttpRequest aspNetRequest)
        {
            var body = new MemoryStream();
            await aspNetRequest.Body.CopyToAsync(body).ConfigureAwait(false);
            body.Position = 0;

            var requestMessage = new HttpRequestMessage(new HttpMethod(aspNetRequest.Method), aspNetRequest.GetEncodedUrl())
            {
                Content = new StreamContent(body),
            };

            foreach (var header in aspNetRequest.Headers)
            {
                var values = (IEnumerable<string>)header.Value;
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, values))
                {
                    requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, values);
                }
            }

            return requestMessage;
        }
    }
}

