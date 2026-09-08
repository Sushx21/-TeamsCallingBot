namespace TeamsCallingBot.Audio
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Speech.Recognition;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Common.Telemetry;

    /// <summary>
    /// Listens to live meeting audio from participants in real-time, recognizes spoken trigger phrases
    /// (such as "tda bot", "kaise ho", "hi bot", "status", "help"), and triggers immediate voice replies.
    /// Uses Windows built-in System.Speech.Recognition with both targeted grammar choices and dictation.
    /// </summary>
    public class RealtimeSpeechRecognizer : IDisposable
    {
        private const int SampleRate = 16000;
        private const short BitsPerSample = 16;
        private const short Channels = 1;
        private const int BytesPerSecond = SampleRate * (BitsPerSample / 8) * Channels; // 32,000 bytes/sec
        private const int WindowSeconds = 3;
        private const int MaxBufferBytes = BytesPerSecond * WindowSeconds; // 96,000 bytes (3 sec window)
        private const int MinProcessingBytes = BytesPerSecond * 1; // At least 1.0 sec to analyze

        private readonly IGraphLogger logger;
        private readonly Func<bool> isBotSpeakingFunc;
        private readonly MemoryStream audioStream = new MemoryStream();
        private readonly object streamLock = new object();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private SpeechRecognitionEngine speechEngine;
        private DateTime lastTriggerTime = DateTime.MinValue;
        private readonly TimeSpan triggerCooldown = TimeSpan.FromSeconds(3.5);

        /// <summary>
        /// Event fired when a voice trigger is recognized.
        /// Parameters: (recognizedText, voiceReply, chatReply)
        /// </summary>
        public event Action<string, string, string> OnTriggerDetected;

        public RealtimeSpeechRecognizer(IGraphLogger logger, Func<bool> isBotSpeakingFunc = null)
        {
            this.logger = logger;
            this.isBotSpeakingFunc = isBotSpeakingFunc;

            this.InitializeSpeechEngine();
            Task.Run(this.RecognitionLoopAsync, this.cts.Token);
        }

        private void InitializeSpeechEngine()
        {
            try
            {
                this.speechEngine = new SpeechRecognitionEngine(new System.Globalization.CultureInfo("en-US"));

                // 1. Specific phrase grammar with high weight for rapid, reliable keyword detection
                var choices = new Choices();
                choices.Add(new string[]
                {
                    "tda bot",
                    "tda",
                    "t d a",
                    "t d a bot",
                    "bot",
                    "hi bot",
                    "hello bot",
                    "hey bot",
                    "kaise ho",
                    "kaisa ho",
                    "kaisa hai",
                    "kaisa",
                    "badhiya",
                    "how are you",
                    "buckeyes",
                    "buckeyes hope",
                    "hi",
                    "hello",
                    "status",
                    "help",
                    "who are you",
                    "meeting assistant",
                    "are you listening",
                    "are you there"
                });

                var gb = new GrammarBuilder(choices);
                var keywordGrammar = new Grammar(gb) { Name = "KeywordGrammar", Priority = 1 };
                this.speechEngine.LoadGrammar(keywordGrammar);

                // 2. Dictation grammar as a general fallback
                var dictation = new DictationGrammar { Name = "DictationGrammar", Priority = 0 };
                this.speechEngine.LoadGrammar(dictation);

                this.logger?.Info("[RealtimeSpeechRecognizer] SpeechRecognitionEngine initialized with keyword and dictation grammars.");
                Console.WriteLine(">>> [Voice Recognizer] SpeechRecognitionEngine initialized with trigger grammars.");
            }
            catch (Exception ex)
            {
                this.logger?.Error(ex, "[RealtimeSpeechRecognizer] Failed to initialize SpeechRecognitionEngine.");
                Console.WriteLine($">>> [Voice Recognizer] Warning: Speech engine init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends incoming uncompressed 16kHz 16-bit mono PCM bytes from participant audio.
        /// </summary>
        public void AppendAudio(IntPtr data, long length)
        {
            if (data == IntPtr.Zero || length <= 0) return;

            // If the bot itself is currently speaking, do not capture our own voice
            if (this.isBotSpeakingFunc != null && this.isBotSpeakingFunc())
            {
                return;
            }

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);

            lock (this.streamLock)
            {
                // Prevent buffer from growing unbounded
                if (this.audioStream.Length > MaxBufferBytes * 2)
                {
                    this.audioStream.SetLength(0);
                }

                this.audioStream.Write(bytes, 0, bytes.Length);
            }
        }

        private async Task RecognitionLoopAsync()
        {
            var token = this.cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1200, token).ConfigureAwait(false);

                    // Skip processing if bot is speaking
                    if (this.isBotSpeakingFunc != null && this.isBotSpeakingFunc())
                    {
                        lock (this.streamLock)
                        {
                            this.audioStream.SetLength(0);
                        }
                        continue;
                    }

                    byte[] pcmData = null;
                    lock (this.streamLock)
                    {
                        if (this.audioStream.Length >= MinProcessingBytes)
                        {
                            pcmData = this.audioStream.ToArray();
                            this.audioStream.SetLength(0);
                        }
                    }

                    if (pcmData == null || pcmData.Length < MinProcessingBytes)
                    {
                        continue;
                    }

                    // Check audio energy level: skip if pure silence
                    double energyPct = CalculateRmsEnergyPercentage(pcmData);
                    if (energyPct < 0.6)
                    {
                        // Background noise / silence, skip recognition
                        continue;
                    }

                    // Check cooldown
                    if (DateTime.UtcNow - this.lastTriggerTime < this.triggerCooldown)
                    {
                        continue;
                    }

                    // Perform speech recognition in a short non-blocking task
                    string recognized = await Task.Run(() => this.RecognizePcm(pcmData)).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(recognized))
                    {
                        this.ProcessRecognizedText(recognized);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    this.logger?.Warn($"[RealtimeSpeechRecognizer] Error in recognition loop: {ex.Message}");
                }
            }
        }

        private string RecognizePcm(byte[] pcm)
        {
            if (this.speechEngine == null || pcm == null || pcm.Length == 0) return null;

            try
            {
                byte[] wav = CreateWavBytes(pcm);
                using (var ms = new MemoryStream(wav))
                {
                    this.speechEngine.SetInputToWaveStream(ms);
                    var result = this.speechEngine.Recognize(TimeSpan.FromMilliseconds(900));
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        return result.Text;
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger?.Warn($"[RealtimeSpeechRecognizer] Recognize error: {ex.Message}");
            }

            return null;
        }

        private void ProcessRecognizedText(string text)
        {
            string lower = text.ToLowerInvariant();
            this.logger?.Info($"[RealtimeSpeechRecognizer] Recognized: \"{text}\"");
            Console.WriteLine($">>> [Voice Recognizer] Heard: \"{text}\"");

            bool isTrigger = lower.Contains("tda") || lower.Contains("bot") ||
                             lower.Contains("tito") || lower.Contains("tita") ||
                             lower.Contains("kaise ho") || lower.Contains("kaisa") ||
                             lower.Contains("buckeyes") || lower.Contains("how are you") ||
                             lower.Contains("hi") || lower.Contains("hello") ||
                             lower.Contains("hey") || lower.Contains("status") ||
                             lower.Contains("help") || lower.Contains("recording") ||
                             lower.Contains("who are you") || lower.Contains("badhiya") ||
                             lower.Contains("listening") || lower.Contains("there");

            if (!isTrigger) return;

            // Enforce cooldown
            this.lastTriggerTime = DateTime.UtcNow;

            string voiceReply;
            string chatReply;

            if (lower.Contains("kaise ho") || lower.Contains("kaisa") || lower.Contains("buckeyes") || lower.Contains("how are you") || lower.Contains("badhiya"))
            {
                voiceReply = "Hello! Main badhiya hoon. I am TDA Bot. How can I help you today?";
                chatReply = "Hello! 🙏 Main badhiya hoon (I'm doing well!). Main TDA Bot hoon, aapka meeting assistant. Kya madad kar sakta hoon?";
            }
            else if (lower.Contains("status") || lower.Contains("recording"))
            {
                voiceReply = "TDA Bot is active. Audio recording and screen share capture are running smoothly.";
                chatReply = "📊 **TDA Bot Status:** \n• 🎙️ Audio recording: Active\n• 📺 Screen share capture: Active\n• 🤖 AI assistant: Listening";
            }
            else if (lower.Contains("help") || lower.Contains("who are you"))
            {
                voiceReply = "Hello! I am TDA Bot. I can record this meeting, capture screen shares, and assist you. Just say TDA Bot or ask how are you.";
                chatReply = "Hi! 👋 I'm **TDA Bot**. I can:\n• 🎙️ Record meeting audio\n• 📺 Capture screen shares\n• 💬 Reply to your voice and chat messages\n\nJust say **TDA Bot** or **kaise ho** to talk to me!";
            }
            else
            {
                voiceReply = "Hello! I am TDA Bot, your AI meeting assistant. How can I help you?";
                chatReply = "Hello! 👋 I'm **TDA Bot**, your AI meeting assistant. I'm actively listening and recording this meeting.";
            }

            Console.WriteLine($">>> [VOICE TRIGGER DETECTED] Heard: \"{text}\" -> Replying: \"{voiceReply}\"");
            this.logger?.Info($"[VOICE TRIGGER DETECTED] Heard: \"{text}\" -> Replying: \"{voiceReply}\"");

            try
            {
                this.OnTriggerDetected?.Invoke(text, voiceReply, chatReply);
            }
            catch (Exception ex)
            {
                this.logger?.Error(ex, "[RealtimeSpeechRecognizer] Error dispatching trigger response.");
            }
        }

        private static double CalculateRmsEnergyPercentage(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2) return 0;
            long sumSquares = 0;
            int samples = pcm.Length / 2;
            for (int i = 0; i < samples; i++)
            {
                short val = BitConverter.ToInt16(pcm, i * 2);
                sumSquares += (long)val * val;
            }

            double rms = samples > 0 ? Math.Sqrt((double)sumSquares / samples) : 0;
            return Math.Min(100.0, (rms / 32768.0) * 100.0);
        }

        private static byte[] CreateWavBytes(byte[] pcmData)
        {
            int subchunk2Size = pcmData.Length;
            int chunkSize = 36 + subchunk2Size;
            short blockAlign = (short)(Channels * (BitsPerSample / 8));
            int byteRate = SampleRate * blockAlign;

            var ms = new MemoryStream(44 + subchunk2Size);
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(chunkSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16); // Subchunk1Size (PCM = 16)
                bw.Write((short)1); // AudioFormat (1 = PCM)
                bw.Write((short)Channels);
                bw.Write(SampleRate);
                bw.Write(byteRate);
                bw.Write(blockAlign);
                bw.Write(BitsPerSample);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(subchunk2Size);
                bw.Write(pcmData);
            }

            return ms.ToArray();
        }

        public void Dispose()
        {
            this.cts.Cancel();
            this.cts.Dispose();
            if (this.speechEngine != null)
            {
                try
                {
                    this.speechEngine.Dispose();
                }
                catch { }
                this.speechEngine = null;
            }

            this.audioStream.Dispose();
        }
    }
}
