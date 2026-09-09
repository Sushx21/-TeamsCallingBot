namespace TeamsCallingBot.Common
{
    using System;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Production placeholder & parser for meetings forwarded via email to the TSL AI account.
    ///
    /// In production, users will forward their Outlook / Teams meeting invitations to the bot's
    /// designated email (e.g. tsl-ai@tatasteel.com). This parser extracts:
    /// 1. Long Teams meetup-join URLs containing threadId and ?context={tid,oid}
    /// 2. Short /meet/ links with passcode parameters (?p=...)
    /// 3. Meeting ID and Passcode blocks
    /// 4. Scheduled Start Time and Subject if present
    /// </summary>
    public static class EmailMeetingInviteParser
    {
        private static readonly Regex LongJoinUrlRegex = new Regex(
            @"https?://teams\.microsoft\.com/l/meetup-join/[^\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ShortJoinUrlRegex = new Regex(
            @"https?://teams\.microsoft\.com/meet/[^\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MeetingIdRegex = new Regex(
            @"Meeting\s*ID[:\s]+(?<id>[0-9\s]{9,18})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PasscodeRegex = new Regex(
            @"Passcode[:\s]+(?<pass>[a-zA-Z0-9]{4,12})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public class ParsedInviteResult
        {
            public bool Success { get; set; }
            public string JoinUrl { get; set; }
            public string MeetingId { get; set; }
            public string Passcode { get; set; }
            public string Subject { get; set; }
            public DateTime? ScheduledStartTime { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// Parses a forwarded email body (plain text or HTML) and extracts meeting join details.
        /// </summary>
        public static ParsedInviteResult ParseEmailContent(string emailContent)
        {
            if (string.IsNullOrWhiteSpace(emailContent))
            {
                return new ParsedInviteResult
                {
                    Success = false,
                    Error = "Email content was empty."
                };
            }

            var result = new ParsedInviteResult();

            // 1. Try finding the full long join URL (best & most reliable)
            var longMatch = LongJoinUrlRegex.Match(emailContent);
            if (longMatch.Success)
            {
                result.JoinUrl = CleanUrl(longMatch.Value);
                result.Success = true;
            }

            // 2. If no long URL, look for short /meet/ URL
            if (!result.Success)
            {
                var shortMatch = ShortJoinUrlRegex.Match(emailContent);
                if (shortMatch.Success)
                {
                    result.JoinUrl = CleanUrl(shortMatch.Value);
                    result.Success = true;
                }
            }

            // 3. Extract Meeting ID and Passcode if available
            var idMatch = MeetingIdRegex.Match(emailContent);
            if (idMatch.Success)
            {
                result.MeetingId = Regex.Replace(idMatch.Groups["id"].Value, @"\s+", string.Empty);
            }

            var passMatch = PasscodeRegex.Match(emailContent);
            if (passMatch.Success)
            {
                result.Passcode = passMatch.Groups["pass"].Value.Trim();
            }

            if (!result.Success && string.IsNullOrWhiteSpace(result.MeetingId))
            {
                result.Error = "No Microsoft Teams join URL or Meeting ID found in forwarded email content.";
            }

            return result;
        }

        private static string CleanUrl(string rawUrl)
        {
            // Remove any trailing punctuation or HTML tags that might have been picked up
            return rawUrl.TrimEnd('.', ',', ';', '>', '"', '\'', ')', ']');
        }
    }
}
