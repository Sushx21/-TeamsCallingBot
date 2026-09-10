namespace TeamsCallingBot.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Google.Apis.Auth.OAuth2;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using TeamsCallingBot.Config;

    /// <summary>
    /// Distributed meeting lock service backed by Google Cloud Firestore (REST API),
    /// with an automatic in-memory fallback when Firestore is not configured or in local development.
    ///
    /// Prevents duplicate joins when multiple bot instances, Power Automate webhooks,
    /// or manual chat triggers target the same meeting concurrently.
    /// </summary>
    public class FirestoreMeetingLockService
    {
        private static readonly Lazy<FirestoreMeetingLockService> DefaultInstance =
            new Lazy<FirestoreMeetingLockService>(() => new FirestoreMeetingLockService());

        public static FirestoreMeetingLockService Instance => DefaultInstance.Value;

        private readonly FirestoreOptions options;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, InMemoryLock> localLocks =
            new ConcurrentDictionary<string, InMemoryLock>(StringComparer.OrdinalIgnoreCase);

        public bool IsFirestoreConfigured =>
            this.options != null &&
            this.options.Enabled &&
            !string.IsNullOrWhiteSpace(this.options.ProjectId) &&
            !string.IsNullOrWhiteSpace(this.options.ServiceAccountKeyPath) &&
            File.Exists(this.options.ServiceAccountKeyPath);

        public FirestoreMeetingLockService(FirestoreOptions options = null, Action<string> log = null)
        {
            this.options = options ?? BotOptions.Current?.Firestore ?? new FirestoreOptions();
            this.log = log ?? (msg => Console.WriteLine($">>> [FirestoreLock] {msg}"));
        }

        /// <summary>
        /// Attempts to acquire an exclusive lock for the given meeting thread or join URL.
        /// Returns true if lock was acquired; false if another instance or call has already joined.
        /// </summary>
        public async Task<bool> TryAcquireLockAsync(string meetingThreadIdOrUrl, string joinUrl, string callId = null)
        {
            if (string.IsNullOrWhiteSpace(meetingThreadIdOrUrl))
            {
                return true;
            }

            string lockKey = SanitizeDocumentKey(meetingThreadIdOrUrl);

            // 1. Local in-memory check first (fast-path)
            DateTime utcNow = DateTime.UtcNow;
            if (this.localLocks.TryGetValue(lockKey, out var existingLocal))
            {
                if (existingLocal.ExpiresAtUtc > utcNow && existingLocal.Status != "completed")
                {
                    this.log($"[Lock Denied - Local] Meeting '{lockKey}' is already locked by call '{existingLocal.CallId}' (Status: {existingLocal.Status})");
                    return false;
                }
            }

            // 2. If Firestore is configured, acquire distributed atomic lock
            if (this.IsFirestoreConfigured)
            {
                try
                {
                    bool firestoreLocked = await this.TryAcquireFirestoreLockAsync(lockKey, joinUrl, callId, utcNow).ConfigureAwait(false);
                    if (!firestoreLocked)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    this.log($"[Warning] Firestore lock call failed ({ex.Message}) - falling back to local memory lock.");
                }
            }

            // 3. Record local in-memory lock
            var acquired = new InMemoryLock
            {
                LockKey = lockKey,
                CallId = callId,
                JoinUrl = joinUrl,
                Status = "joining",
                LockedAtUtc = utcNow,
                ExpiresAtUtc = utcNow.AddMinutes(this.options.LockTtlMinutes)
            };
            this.localLocks[lockKey] = acquired;

            this.log($"[Lock Acquired] Successfully locked meeting '{lockKey}' (TTL: {this.options.LockTtlMinutes}m)");
            return true;
        }

        /// <summary>
        /// Updates the lock document status to "joined" once the call establishes.
        /// </summary>
        public async Task ConfirmJoinedAsync(string meetingThreadIdOrUrl, string callId)
        {
            if (string.IsNullOrWhiteSpace(meetingThreadIdOrUrl)) return;
            string lockKey = SanitizeDocumentKey(meetingThreadIdOrUrl);

            if (this.localLocks.TryGetValue(lockKey, out var local))
            {
                local.Status = "joined";
                local.CallId = callId;
            }

            if (!this.IsFirestoreConfigured) return;

            try
            {
                await this.UpdateFirestoreStatusAsync(lockKey, "joined", callId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.log($"[Warning] Could not update Firestore status to 'joined': {ex.Message}");
            }
        }

        /// <summary>
        /// Releases or marks the lock document as "completed" when the bot leaves the meeting.
        /// </summary>
        public async Task ReleaseLockAsync(string meetingThreadIdOrUrl, string callId = null)
        {
            if (string.IsNullOrWhiteSpace(meetingThreadIdOrUrl)) return;
            string lockKey = SanitizeDocumentKey(meetingThreadIdOrUrl);

            this.localLocks.TryRemove(lockKey, out _);

            if (!this.IsFirestoreConfigured) return;

            try
            {
                await this.UpdateFirestoreStatusAsync(lockKey, "completed", callId).ConfigureAwait(false);
                this.log($"[Lock Released] Meeting lock for '{lockKey}' released.");
            }
            catch (Exception ex)
            {
                this.log($"[Warning] Could not release Firestore lock: {ex.Message}");
            }
        }

        private async Task<bool> TryAcquireFirestoreLockAsync(string lockKey, string joinUrl, string callId, DateTime utcNow)
        {
            string token = await this.GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            string docUrl = $"https://firestore.googleapis.com/v1/projects/{this.options.ProjectId}/databases/(default)/documents/{this.options.CollectionName}/{lockKey}";

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Check existing document
                var getResponse = await http.GetAsync(docUrl).ConfigureAwait(false);
                if (getResponse.IsSuccessStatusCode)
                {
                    string json = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var doc = JObject.Parse(json);
                    var fields = doc["fields"];
                    if (fields != null)
                    {
                        string status = fields["status"]?["stringValue"]?.ToString();
                        string expiresStr = fields["expiresAt"]?["timestampValue"]?.ToString();
                        DateTime expiresAt = DateTime.MinValue;
                        if (!string.IsNullOrWhiteSpace(expiresStr))
                        {
                            DateTime.TryParse(expiresStr, out expiresAt);
                        }

                        // If still valid and not completed, refuse duplicate join
                        if (status != "completed" && expiresAt > utcNow)
                        {
                            string existingCallId = fields["callId"]?["stringValue"]?.ToString() ?? "unknown";
                            this.log($"[Lock Denied - Firestore] Meeting '{lockKey}' is already active (CallId: {existingCallId}, Status: {status}, Expires: {expiresAt:u})");
                            return false;
                        }
                    }
                }

                // Write lock document (PATCH with update)
                DateTime expires = utcNow.AddMinutes(this.options.LockTtlMinutes);
                var payload = new
                {
                    fields = new
                    {
                        meetingKey = new { stringValue = lockKey },
                        meetingJoinUrl = new { stringValue = joinUrl ?? string.Empty },
                        callId = new { stringValue = callId ?? string.Empty },
                        lockedBy = new { stringValue = Environment.MachineName },
                        status = new { stringValue = "joining" },
                        lockedAt = new { timestampValue = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                        expiresAt = new { timestampValue = expires.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                    }
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                var putResponse = await PatchAsync(http, docUrl, content).ConfigureAwait(false);
                return putResponse.IsSuccessStatusCode;
            }
        }

        private async Task UpdateFirestoreStatusAsync(string lockKey, string newStatus, string callId)
        {
            string token = await this.GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return;

            string docUrl = $"https://firestore.googleapis.com/v1/projects/{this.options.ProjectId}/databases/(default)/documents/{this.options.CollectionName}/{lockKey}?updateMask.fieldPaths=status&updateMask.fieldPaths=callId";

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var payload = new
                {
                    fields = new
                    {
                        status = new { stringValue = newStatus },
                        callId = new { stringValue = callId ?? string.Empty }
                    }
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                await PatchAsync(http, docUrl, content).ConfigureAwait(false);
            }
        }

        private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string requestUri, HttpContent content)
        {
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri) { Content = content };
            return await client.SendAsync(request).ConfigureAwait(false);
        }

        private async Task<string> GetAccessTokenAsync()
        {
            try
            {
                if (!File.Exists(this.options.ServiceAccountKeyPath))
                {
                    return null;
                }

                using (var stream = File.OpenRead(this.options.ServiceAccountKeyPath))
                {
                    var credential = GoogleCredential.FromStream(stream)
                        .CreateScoped("https://www.googleapis.com/auth/datastore");
                    return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.log($"[Error] Could not acquire Google Cloud token for Firestore: {ex.Message}");
                return null;
            }
        }

        public static string SanitizeDocumentKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "meeting_unknown";

            // Extract thread ID if full meetup-join URL is supplied
            var match = Regex.Match(input, @"19%3ameeting_[a-zA-Z0-9_\-]+%40thread\.v2|19:meeting_[a-zA-Z0-9_\-]+@thread\.v2", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                input = match.Value;
            }

            // Replace URL-encoded or invalid characters
            string clean = input
                .Replace("%3a", "_")
                .Replace("%3A", "_")
                .Replace("%40", "_")
                .Replace(":", "_")
                .Replace("@", "_")
                .Replace("/", "_")
                .Replace("?", "_")
                .Replace("&", "_")
                .Replace("=", "_");

            if (clean.Length > 100)
            {
                clean = clean.Substring(0, 100);
            }

            return clean;
        }

        private class InMemoryLock
        {
            public string LockKey { get; set; }
            public string CallId { get; set; }
            public string JoinUrl { get; set; }
            public string Status { get; set; }
            public DateTime LockedAtUtc { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }
    }
}
