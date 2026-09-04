namespace TeamsCallingBot.Http
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using TeamsCallingBot.Bot;

    [Route("api/calls")]
    [ApiController]
    public class CallManagementController : ControllerBase
    {
        private readonly Bot bot;

        public CallManagementController(Bot bot)
        {
            this.bot = bot ?? throw new ArgumentNullException(nameof(bot));
        }

        [HttpGet]
        public IActionResult GetActiveCalls()
        {
            var calls = this.bot.CallHandlers.Select(kvp => new
            {
                CallId = kvp.Key,
                IsMuted = kvp.Value.IsMuted,
                RecordingsFolder = kvp.Value.RecordingsManager.SessionDirectory
            });

            return this.Ok(calls);
        }

        [HttpPost("{callId}/mute")]
        public async Task<IActionResult> MuteAsync(string callId)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                await handler.MuteAsync().ConfigureAwait(false);
                return this.Ok(new { Message = "Bot muted successfully.", CallId = callId, IsMuted = true });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/unmute")]
        public async Task<IActionResult> UnmuteAsync(string callId)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                await handler.UnmuteAsync().ConfigureAwait(false);
                return this.Ok(new { Message = "Bot unmuted successfully.", CallId = callId, IsMuted = false });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/play-chime")]
        public async Task<IActionResult> PlayChimeAsync(string callId)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                await handler.AudioSender.PlayChimeAsync().ConfigureAwait(false);
                return this.Ok(new { Message = "Chime played successfully.", CallId = callId });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/play-audio")]
        public async Task<IActionResult> PlayAudioAsync(string callId, [FromBody] PlayAudioRequest request)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                if (string.IsNullOrWhiteSpace(request?.FilePath))
                {
                    return this.BadRequest(new { Message = "FilePath is required." });
                }

                await handler.PlayAudioFileAsync(request.FilePath).ConfigureAwait(false);
                return this.Ok(new { Message = "Audio playback started.", FilePath = request.FilePath, CallId = callId });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/capture-photo")]
        public IActionResult CapturePhoto(string callId, [FromQuery] string tag = "manual")
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                var photoPath = handler.CapturePhotoNow(tag);
                if (photoPath != null)
                {
                    return this.Ok(new { Message = "Photo captured successfully.", PhotoPath = photoPath, CallId = callId });
                }

                return this.BadRequest(new { Message = "No active screen share frame available to capture yet.", CallId = callId });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/send-message")]
        public async Task<IActionResult> SendChatMessageAsync(string callId, [FromBody] SendMessageRequest request)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                if (string.IsNullOrWhiteSpace(request?.Message))
                {
                    return this.BadRequest(new { Message = "Message is required." });
                }

                bool sent = await handler.PostTextMessageToChatAsync(request.Message).ConfigureAwait(false);
                if (sent)
                {
                    return this.Ok(new { Message = "Message posted to meeting chat.", CallId = callId, Content = request.Message });
                }
                else
                {
                    return this.StatusCode(502, new { Message = "Failed posting message to meeting chat (check token permissions or threadId).", CallId = callId });
                }
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/leave")]
        public async Task<IActionResult> LeaveAsync(string callId)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                await handler.Call.DeleteAsync().ConfigureAwait(false);
                return this.Ok(new { Message = "Bot left meeting.", CallId = callId });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }

        [HttpPost("{callId}/speak")]
        public async Task<IActionResult> SpeakAsync(string callId, [FromBody] SpeakRequest request)
        {
            if (this.bot.CallHandlers.TryGetValue(callId, out var handler))
            {
                if (string.IsNullOrWhiteSpace(request?.Text))
                {
                    return this.BadRequest(new { Message = "Text is required." });
                }

                if (handler.AudioSender == null)
                {
                    return this.BadRequest(new { Message = "AudioSender is not available for this call." });
                }

                await handler.AudioSender.SpeakAsync(request.Text).ConfigureAwait(false);
                return this.Ok(new { Message = "Speech synthesized and played into meeting.", Text = request.Text, CallId = callId });
            }

            return this.NotFound(new { Message = $"Call {callId} not found." });
        }
    }

    public class SpeakRequest
    {
        public string Text { get; set; }
    }

    public class SendMessageRequest
    {
        public string Message { get; set; }
    }

    public class PlayAudioRequest
    {
        public string FilePath { get; set; }
    }
}
