namespace TeamsCallingBot.Common
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Newtonsoft.Json;

    /// <summary>
    /// Thread-safe registry for meetings registered via Power Automate, calendar forwarding,
    /// manual chat commands, or scheduled configurations.
    ///
    /// Holds future meetings, auto-triggers joins when the meeting start time is reached,
    /// and persists state to disk (cache/registered_meetings.json) across restarts.
    /// </summary>
    public class MeetingRegistryService
    {
        private static readonly string StoragePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "cache",
            "registered_meetings.json");

        private readonly ConcurrentDictionary<string, RegisteredMeeting> meetings =
            new ConcurrentDictionary<string, RegisteredMeeting>(StringComparer.OrdinalIgnoreCase);

        private readonly object fileLock = new object();

        public MeetingRegistryService()
        {
            this.LoadFromDisk();
        }

        public RegisteredMeeting Register(
            string joinUrl,
            string subject = null,
            DateTime? startTimeUtc = null,
            DateTime? endTimeUtc = null,
            string organizer = null,
            bool recordVideo = true,
            string threadId = null,
            string source = "PowerAutomate")
        {
            if (string.IsNullOrWhiteSpace(joinUrl) && string.IsNullOrWhiteSpace(threadId))
            {
                throw new ArgumentException("Either joinUrl or threadId must be provided.");
            }

            string key = !string.IsNullOrWhiteSpace(joinUrl) ? joinUrl : threadId;

            var existing = this.meetings.Values.FirstOrDefault(m =>
                (!string.IsNullOrWhiteSpace(joinUrl) && string.Equals(m.MeetingJoinUrl, joinUrl, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(threadId) && string.Equals(m.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
            {
                // Update existing registration details
                existing.Subject = subject ?? existing.Subject;
                existing.ScheduledStartTimeUtc = startTimeUtc ?? existing.ScheduledStartTimeUtc;
                existing.ScheduledEndTimeUtc = endTimeUtc ?? existing.ScheduledEndTimeUtc;
                existing.Organizer = organizer ?? existing.Organizer;
                existing.RecordVideo = recordVideo;
                this.SaveToDisk();
                return existing;
            }

            var meeting = new RegisteredMeeting
            {
                Id = Guid.NewGuid().ToString("N"),
                MeetingJoinUrl = joinUrl,
                ThreadId = threadId,
                Subject = !string.IsNullOrWhiteSpace(subject) ? subject : "Teams Meeting",
                Organizer = organizer,
                ScheduledStartTimeUtc = startTimeUtc,
                ScheduledEndTimeUtc = endTimeUtc,
                RecordVideo = recordVideo,
                Source = source,
                State = MeetingState.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };

            this.meetings[meeting.Id] = meeting;
            this.SaveToDisk();

            Console.WriteLine($">>> [MeetingRegistry] Registered new meeting: '{meeting.Subject}' | StartUtc: {meeting.ScheduledStartTimeUtc:u} | Source: {source} | Video: {recordVideo}");
            return meeting;
        }

        public List<RegisteredMeeting> GetPendingMeetingsToJoin(DateTime utcNow)
        {
            var list = new List<RegisteredMeeting>();

            foreach (var m in this.meetings.Values)
            {
                if (m.State != MeetingState.Pending)
                {
                    continue;
                }

                if (!m.ScheduledStartTimeUtc.HasValue)
                {
                    // No scheduled time means join immediately
                    list.Add(m);
                    continue;
                }

                // If current time is within 2 minutes before scheduled start time up to 60 minutes after
                var diff = utcNow - m.ScheduledStartTimeUtc.Value;
                if (diff.TotalMinutes >= -2.0 && diff.TotalMinutes <= 60.0)
                {
                    list.Add(m);
                }
            }

            return list;
        }

        public void MarkJoining(string id)
        {
            if (this.meetings.TryGetValue(id, out var m))
            {
                m.State = MeetingState.Joining;
                this.SaveToDisk();
            }
        }

        public void MarkJoined(string idOrUrlOrThread, string callId)
        {
            var m = this.FindMeeting(idOrUrlOrThread);
            if (m != null)
            {
                m.State = MeetingState.Joined;
                m.CallId = callId;
                m.JoinedAtUtc = DateTime.UtcNow;
                this.SaveToDisk();
                Console.WriteLine($">>> [MeetingRegistry] Meeting marked JOINED: '{m.Subject}' (CallId: {callId})");
            }
        }

        public void MarkFailed(string idOrUrlOrThread, string error)
        {
            var m = this.FindMeeting(idOrUrlOrThread);
            if (m != null)
            {
                m.State = MeetingState.Failed;
                m.ErrorMessage = error;
                this.SaveToDisk();
                Console.WriteLine($">>> [MeetingRegistry] Meeting marked FAILED: '{m.Subject}' | Error: {error}");
            }
        }

        public void MarkCompleted(string callId)
        {
            var m = this.meetings.Values.FirstOrDefault(x => string.Equals(x.CallId, callId, StringComparison.OrdinalIgnoreCase));
            if (m != null)
            {
                m.State = MeetingState.Completed;
                m.CompletedAtUtc = DateTime.UtcNow;
                this.SaveToDisk();
                Console.WriteLine($">>> [MeetingRegistry] Meeting marked COMPLETED: '{m.Subject}' (CallId: {callId})");
            }
        }

        public List<RegisteredMeeting> GetAll()
        {
            return this.meetings.Values.OrderByDescending(m => m.CreatedAtUtc).ToList();
        }

        private RegisteredMeeting FindMeeting(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            if (this.meetings.TryGetValue(query, out var direct))
            {
                return direct;
            }

            return this.meetings.Values.FirstOrDefault(m =>
                string.Equals(m.MeetingJoinUrl, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.ThreadId, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.CallId, query, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadFromDisk()
        {
            lock (this.fileLock)
            {
                try
                {
                    if (File.Exists(StoragePath))
                    {
                        string json = File.ReadAllText(StoragePath);
                        var items = JsonConvert.DeserializeObject<List<RegisteredMeeting>>(json);
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                this.meetings[item.Id] = item;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($">>> [MeetingRegistry] Could not load from disk: {ex.Message}");
                }
            }
        }

        private void SaveToDisk()
        {
            lock (this.fileLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(StoragePath);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string json = JsonConvert.SerializeObject(this.meetings.Values.ToList(), Formatting.Indented);
                    File.WriteAllText(StoragePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($">>> [MeetingRegistry] Could not save to disk: {ex.Message}");
                }
            }
        }
    }

    public enum MeetingState
    {
        Pending,
        Joining,
        Joined,
        Completed,
        Failed
    }

    public class RegisteredMeeting
    {
        public string Id { get; set; }
        public string Subject { get; set; }
        public string MeetingJoinUrl { get; set; }
        public string ThreadId { get; set; }
        public string Organizer { get; set; }
        public DateTime? ScheduledStartTimeUtc { get; set; }
        public DateTime? ScheduledEndTimeUtc { get; set; }
        public bool RecordVideo { get; set; } = true;
        public string Source { get; set; }
        public MeetingState State { get; set; }
        public string CallId { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? JoinedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }
}
