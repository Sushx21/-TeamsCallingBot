namespace TeamsCallingBot.Common
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Unified Meeting Link & ID Resolver for Teams Calling Bot.
    /// Supports all 4 production input formats:
    /// 1. Auto-extract from raw text (chat messages, email forwarding, pasted invitations).
    /// 2. Direct 19:meeting_...@thread.v2 meeting IDs.
    /// 3. Passcode-protected short links (teams.microsoft.com/meet/<id>?p=<passcode>).
    /// 4. Pluggable Table Lookup Placeholder (database / BigQuery / table mapping to 19 v2 links).
    /// </summary>
    public static class MeetingLinkResolver
    {
        // 1. Long Meetup-Join URL Regex
        private static readonly Regex LongJoinRegex = new Regex(
            @"https?://teams\.microsoft\.com/l/meetup-join/[^\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 2. Short /meet/ URL Regex (with or without ?p= passcode)
        private static readonly Regex ShortJoinRegex = new Regex(
            @"https?://teams\.(microsoft|live)\.com/meet/[^\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 3. Direct 19:...@thread.v2 or 19:...@thread.skype meeting ID Regex
        private static readonly Regex DirectThreadIdRegex = new Regex(
            @"(?:19%3a|19:)[a-zA-Z0-9_\-]+(?:%40|@)thread\.(?:v2|skype|tacv2)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 4. Meeting ID (numeric or spaced numeric) Regex
        private static readonly Regex NumericMeetingIdRegex = new Regex(
            @"Meeting\s*ID[:\s]+(?<id>[0-9\s]{9,18})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // In-memory table for mapping short links / meeting IDs to full 19 v2 links
        private static readonly ConcurrentDictionary<string, string> TableMappings =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers or pre-populates a meeting mapping from your table into memory.
        /// E.g. RegisterTableMapping("123456789", "19:meeting_...@thread.v2")
        /// </summary>
        public static void RegisterTableMapping(string key, string fullMeetingUrlOrThreadId)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(fullMeetingUrlOrThreadId)) return;
            TableMappings[key.Trim()] = fullMeetingUrlOrThreadId.Trim();
        }

        public class ResolutionResult
        {
            public bool Success { get; set; }
            public string ResolvedUrl { get; set; }
            public string ThreadId { get; set; }
            public bool IsDirectThreadId { get; set; }
            public string Passcode { get; set; }
            public string Source { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Resolves any meeting input (text, short link, long link, or bare thread ID)
        /// into a joinable format for the Calling Bot.
        /// </summary>
        public static async Task<ResolutionResult> ResolveAsync(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return new ResolutionResult { Success = false, ErrorMessage = "Meeting input cannot be null or empty." };
            }

            var trimmed = rawInput.Trim();

            // ------------------------------------------------------------------------
            // 1. Auto-Extract Long Meetup-Join URL from text (PRESERVES ?context={"Tid":...,"Oid":...})
            // ------------------------------------------------------------------------
            var longMatch = LongJoinRegex.Match(trimmed);
            if (longMatch.Success)
            {
                var cleanLongUrl = CleanUrl(longMatch.Value);
                return new ResolutionResult
                {
                    Success = true,
                    ResolvedUrl = cleanLongUrl,
                    Source = "AutoExtractedLongUrl"
                };
            }

            // ------------------------------------------------------------------------
            // 2. Direct 19:...@thread.v2 Meeting ID Check (Only when no full URL present)
            // ------------------------------------------------------------------------
            var threadMatch = DirectThreadIdRegex.Match(trimmed);
            if (threadMatch.Success)
            {
                var threadId = WebUtility.UrlDecode(threadMatch.Value);
                return new ResolutionResult
                {
                    Success = true,
                    IsDirectThreadId = true,
                    ThreadId = threadId,
                    ResolvedUrl = $"https://teams.microsoft.com/l/meetup-join/{Uri.EscapeDataString(threadId)}/0",
                    Source = "DirectThreadId"
                };
            }

            // ------------------------------------------------------------------------
            // 3. Table Lookup Placeholder (Check internal cache / database table)
            // ------------------------------------------------------------------------
            if (TableMappings.TryGetValue(trimmed, out var mappedValue))
            {
                return CreateResultFromMappedValue(mappedValue, "MemoryTableLookup");
            }

            // External Database / BigQuery placeholder query
            var externalDbMatch = await QueryExternalMeetingTableAsync(trimmed).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(externalDbMatch))
            {
                TableMappings[trimmed] = externalDbMatch;
                return CreateResultFromMappedValue(externalDbMatch, "ExternalDatabaseTableLookup");
            }

            // ------------------------------------------------------------------------
            // 4. Short /meet/ link (with passcode ?p=...)
            // ------------------------------------------------------------------------
            var shortMatch = ShortJoinRegex.Match(trimmed);
            if (shortMatch.Success)
            {
                var shortUrl = CleanUrl(shortMatch.Value);
                var passcode = ExtractPasscode(shortUrl);

                // Check table with the short URL or meeting ID portion
                var meetKey = ExtractShortMeetKey(shortUrl);
                if (!string.IsNullOrWhiteSpace(meetKey))
                {
                    if (TableMappings.TryGetValue(meetKey, out var shortMapped))
                    {
                        return CreateResultFromMappedValue(shortMapped, "ShortKeyTableLookup");
                    }

                    var dbShortMapped = await QueryExternalMeetingTableAsync(meetKey).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(dbShortMapped))
                    {
                        TableMappings[meetKey] = dbShortMapped;
                        return CreateResultFromMappedValue(dbShortMapped, "ExternalDbShortKeyLookup");
                    }
                }

                // If not in table, return the short URL so JoinInfo can attempt HTTP unroll
                return new ResolutionResult
                {
                    Success = true,
                    ResolvedUrl = shortUrl,
                    Passcode = passcode,
                    Source = "ShortMeetUrl"
                };
            }

            // ------------------------------------------------------------------------
            // 5. Fallback: URL starts with http
            // ------------------------------------------------------------------------
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new ResolutionResult
                {
                    Success = true,
                    ResolvedUrl = CleanUrl(trimmed),
                    Source = "RawHttpUrl"
                };
            }

            return new ResolutionResult
            {
                Success = false,
                ErrorMessage = $"Could not resolve meeting coordinates from input. Provide a full meetup-join URL, a 19:...@thread.v2 ID, or register it in the table. Input: {trimmed}"
            };
        }

        /// <summary>
        /// PLACEHOLDER: Query your SQL / BigQuery / Firestore database table here.
        /// Maps a short link, meeting passcode, or numeric ID to the full 19:...@thread.v2 link.
        /// </summary>
        public static async Task<string> QueryExternalMeetingTableAsync(string meetingKey)
        {
            if (string.IsNullOrWhiteSpace(meetingKey)) return null;

            // ====================================================================
            // PLACEHOLDER: INSERT YOUR PRODUCTION DATABASE QUERY HERE
            // Example:
            // string query = "SELECT full_meeting_url FROM meetings_registry WHERE short_key = @key";
            // ====================================================================

            return await Task.FromResult<string>(null);
        }

        private static ResolutionResult CreateResultFromMappedValue(string mappedValue, string source)
        {
            var clean = mappedValue.Trim();
            if (clean.StartsWith("19:", StringComparison.OrdinalIgnoreCase) && clean.Contains("@thread."))
            {
                return new ResolutionResult
                {
                    Success = true,
                    IsDirectThreadId = true,
                    ThreadId = clean,
                    ResolvedUrl = $"https://teams.microsoft.com/l/meetup-join/{Uri.EscapeDataString(clean)}/0",
                    Source = source
                };
            }

            return new ResolutionResult
            {
                Success = true,
                ResolvedUrl = clean,
                Source = source
            };
        }

        private static string ExtractPasscode(string url)
        {
            var match = Regex.Match(url, @"[?&]p=(?<pass>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["pass"].Value : null;
        }

        private static string ExtractShortMeetKey(string url)
        {
            var match = Regex.Match(url, @"/meet/(?<key>[^?#/\s]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["key"].Value : null;
        }

        private static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            return url.Trim().TrimEnd('.', ',', ';', ')', '>', ']', '"', '\'');
        }
    }
}
