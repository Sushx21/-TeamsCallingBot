namespace TeamsCallingBot.Http
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Newtonsoft.Json.Linq;
    using TeamsCallingBot.Bot;
    using TeamsCallingBot.Config;

    /// <summary>
    /// Receives incoming Bot Framework message activities from Teams.
    ///
    /// When a user types in a Teams meeting chat and the bot is registered as part of that team/meeting,
    /// Teams delivers the message as a Bot Framework Activity to POST /api/messages.
    /// This endpoint parses the activity, verifies it's a user message, and routes it to the
    /// matching CallHandler so the bot can reply both in chat and via voice.
    ///
    /// This is the ONLY reliable way to receive meeting chat messages without Graph Chat.Read.All
    /// application permission. No admin consent needed — this is standard bot messaging.
    /// </summary>
    [Route("api/messages")]
    [ApiController]
    public class BotMessagingController : ControllerBase
    {
        private readonly Bot bot;

        public BotMessagingController(Bot bot)
        {
            this.bot = bot ?? throw new ArgumentNullException(nameof(bot));
        }

        [HttpPost]
        public async Task<IActionResult> OnMessageAsync()
        {
            string body;
            using (var reader = new StreamReader(this.Request.Body))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return this.Ok();
            }

            JObject activity;
            try
            {
                activity = JObject.Parse(body);
            }
            catch
            {
                return this.BadRequest("Invalid JSON");
            }

            string activityType = activity["type"]?.ToString() ?? "";
            Console.WriteLine($">>> [BotMessaging] Received activity type: {activityType}");

            // Only process message activities from users (not system events)
            if (!string.Equals(activityType, "message", StringComparison.OrdinalIgnoreCase))
            {
                // Acknowledge non-message activities (invoke, event, etc.) with 200
                return this.Ok();
            }

            // Extract sender info
            string fromId = activity["from"]?["id"]?.ToString() ?? "";
            string fromName = activity["from"]?["name"]?.ToString() ?? "Participant";
            string botId = BotOptions.Current?.AadAppId ?? "";

            // Skip messages sent by the bot itself
            if (!string.IsNullOrWhiteSpace(fromId) &&
                string.Equals(fromId, botId, StringComparison.OrdinalIgnoreCase))
            {
                return this.Ok();
            }

            // Extract message text (strip HTML tags from the content)
            string rawText = activity["text"]?.ToString() ?? "";
            string plainText = System.Text.RegularExpressions.Regex.Replace(rawText, "<.*?>", string.Empty).Trim();

            // Get service URL and conversation ID from the activity
            string serviceUrl = activity["serviceUrl"]?.ToString() ?? "";
            string conversationId = activity["conversation"]?["id"]?.ToString() ?? "";
            string tenantId = activity["channelData"]?["tenant"]?["id"]?.ToString() ?? "";

            Console.WriteLine($">>> [BotMessaging] Message from '{fromName}': \"{plainText}\" | ConvId: {conversationId} | ServiceUrl: {serviceUrl}");

            if (string.IsNullOrWhiteSpace(plainText))
            {
                return this.Ok();
            }

            // Find the relevant CallHandler by matching thread ID.
            // The conversationId in Teams is the channel/meeting thread ID.
            CallHandler matchedHandler = null;

            foreach (var kvp in this.bot.CallHandlers)
            {
                string handlerThreadId = kvp.Value.GetEffectiveChatThreadId();
                if (!string.IsNullOrWhiteSpace(handlerThreadId) &&
                    (conversationId.IndexOf(handlerThreadId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     handlerThreadId.IndexOf(conversationId, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    matchedHandler = kvp.Value;
                    break;
                }
            }

            // If no exact match found, use the only active call handler (common for single-call bots)
            if (matchedHandler == null && this.bot.CallHandlers.Count == 1)
            {
                matchedHandler = this.bot.CallHandlers.Values.First();
                Console.WriteLine($">>> [BotMessaging] No exact thread match found - routing to the only active CallHandler.");
            }

            if (matchedHandler == null)
            {
                Console.WriteLine($">>> [BotMessaging] No matching CallHandler found for conversationId={conversationId}. Active handlers: {this.bot.CallHandlers.Count}");
                return this.Ok();
            }

            // Delegate reply generation to the CallHandler
            _ = Task.Run(() => matchedHandler.HandleIncomingChatActivity(fromName, fromId, plainText, serviceUrl));

            return this.Ok();
        }
    }
}
