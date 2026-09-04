namespace TeamsCallingBot.Audio
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Buffers raw PCM audio frames as they arrive, PER SPEAKER, and flushes each speaker's audio
    /// to its own WAV file on disk. Also mixes all speakers into a full meeting WAV file.
    /// Default output: D:\Teamsbot\Recordings\<SessionId>\Audio\
    /// </summary>
    public class SpeakerAudio
    {
        public uint SpeakerId { get; set; }

        public string WavPath { get; set; }

        public DateTime FirstSeenAt { get; set; }
    }

    public class AudioAggregator
    {
        public const uint UnknownSpeakerId = 0;

        private const int SampleRate = 16000; // matches AudioFormat.Pcm16K
        private const short BitsPerSample = 16;
        private const short Channels = 1;

        private readonly object bufferLock = new object();
        private readonly Dictionary<uint, MemoryStream> buffersBySpeaker = new Dictionary<uint, MemoryStream>();
        private readonly Dictionary<uint, DateTime> firstSeenAtBySpeaker = new Dictionary<uint, DateTime>();
        private readonly MemoryStream mixedBuffer = new MemoryStream();

        public void Append(uint speakerId, IntPtr data, long length, long timestamp)
        {
            if (data == IntPtr.Zero || length <= 0)
            {
                return;
            }

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);

            lock (this.bufferLock)
            {
                if (!this.buffersBySpeaker.TryGetValue(speakerId, out var stream))
                {
                    stream = new MemoryStream();
                    this.buffersBySpeaker[speakerId] = stream;
                    this.firstSeenAtBySpeaker[speakerId] = DateTime.Now;
                }

                stream.Write(bytes, 0, bytes.Length);
                this.mixedBuffer.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Writes an incremental snapshot of all audio buffers to disk without clearing them.
        /// This ensures WAV files exist and update continuously in the recordings folder during the meeting.
        /// </summary>
        public List<SpeakerAudio> SnapshotToWavFiles(string outputDirectory = null)
        {
            var targetDir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.GetTempPath()
                : outputDirectory;

            Directory.CreateDirectory(targetDir);

            var pcmBySpeaker = new Dictionary<uint, byte[]>();
            var firstSeenAt = new Dictionary<uint, DateTime>();
            byte[] mixedPcm = null;

            lock (this.bufferLock)
            {
                foreach (var kvp in this.buffersBySpeaker)
                {
                    if (kvp.Value.Length > 0)
                    {
                        pcmBySpeaker[kvp.Key] = kvp.Value.ToArray();
                        firstSeenAt[kvp.Key] = this.firstSeenAtBySpeaker[kvp.Key];
                    }
                }

                if (this.mixedBuffer.Length > 0)
                {
                    mixedPcm = this.mixedBuffer.ToArray();
                }
            }

            var result = new List<SpeakerAudio>();

            // 1. Write per-speaker WAVs
            foreach (var kvp in pcmBySpeaker)
            {
                var speakerName = kvp.Key == UnknownSpeakerId ? "speaker_unknown" : $"speaker_{kvp.Key}";
                var filename = $"02_audio_{speakerName}.wav";
                var path = Path.Combine(targetDir, filename);

                WriteWavFile(path, kvp.Value);
                result.Add(new SpeakerAudio { SpeakerId = kvp.Key, WavPath = path, FirstSeenAt = firstSeenAt[kvp.Key] });
            }

            // 2. Write combined mixed WAV (both ways)
            if (mixedPcm != null && mixedPcm.Length > 0)
            {
                var mixedPath = Path.Combine(targetDir, "02_audio_meeting_both_ways.wav");
                WriteWavFile(mixedPath, mixedPcm);
            }

            return result;
        }

        /// <summary>
        /// Final flush of every speaker's buffered audio to WAV files in the specified output directory upon call termination.
        /// </summary>
        public List<SpeakerAudio> FlushToWavFiles(string outputDirectory = null)
        {
            var targetDir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.GetTempPath()
                : outputDirectory;

            Directory.CreateDirectory(targetDir);

            var pcmBySpeaker = new Dictionary<uint, byte[]>();
            var firstSeenAt = new Dictionary<uint, DateTime>();
            byte[] mixedPcm = null;

            lock (this.bufferLock)
            {
                foreach (var kvp in this.buffersBySpeaker)
                {
                    if (kvp.Value.Length > 0)
                    {
                        pcmBySpeaker[kvp.Key] = kvp.Value.ToArray();
                        firstSeenAt[kvp.Key] = this.firstSeenAtBySpeaker[kvp.Key];
                    }

                    kvp.Value.Dispose();
                }

                if (this.mixedBuffer.Length > 0)
                {
                    mixedPcm = this.mixedBuffer.ToArray();
                }

                this.buffersBySpeaker.Clear();
                this.firstSeenAtBySpeaker.Clear();
                this.mixedBuffer.SetLength(0);
            }

            var result = new List<SpeakerAudio>();

            // 1. Write per-speaker WAVs
            foreach (var kvp in pcmBySpeaker)
            {
                var speakerName = kvp.Key == UnknownSpeakerId ? "speaker_unknown" : $"speaker_{kvp.Key}";
                var filename = $"02_audio_{speakerName}.wav";
                var path = Path.Combine(targetDir, filename);

                WriteWavFile(path, kvp.Value);
                result.Add(new SpeakerAudio { SpeakerId = kvp.Key, WavPath = path, FirstSeenAt = firstSeenAt[kvp.Key] });
            }

            // 2. Write combined mixed WAV (both ways)
            if (mixedPcm != null && mixedPcm.Length > 0)
            {
                var mixedPath = Path.Combine(targetDir, "02_audio_meeting_both_ways.wav");
                WriteWavFile(mixedPath, mixedPcm);
            }

            return result;
        }

        public static void WriteWavFile(string path, byte[] pcmData)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                int byteRate = SampleRate * Channels * (BitsPerSample / 8);

                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcmData.Length);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write(Channels);
                writer.Write(SampleRate);
                writer.Write(byteRate);
                writer.Write((short)(Channels * (BitsPerSample / 8)));
                writer.Write(BitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(pcmData.Length);
                writer.Write(pcmData);
            }
        }
    }
}
