namespace TeamsCallingBot.Audio
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Calls.Media;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Skype.Bots.Media;

    /// <summary>
    /// Handles outbound audio playback into the Teams meeting through AudioSocket.Send().
    /// Supports sending 16kHz 16-bit Mono PCM audio in 20ms chunks (640 bytes per frame).
    /// </summary>
    public class AudioSender : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BytesPerSample = 2; // 16-bit
        private const int Channels = 1;       // Mono
        private const int FrameDurationMs = 20;
        private const int BytesPerFrame = (SampleRate * BytesPerSample * Channels * FrameDurationMs) / 1000; // 640 bytes

        private readonly IAudioSocket audioSocket;
        private readonly IGraphLogger graphLogger;
        private CancellationTokenSource currentPlaybackCts;
        private readonly object playbackLock = new object();

        public bool IsMuted { get; set; } = false;
        public bool IsAudioSendActive { get; private set; } = false;

        public AudioSender(IAudioSocket audioSocket, IGraphLogger graphLogger)
        {
            this.audioSocket = audioSocket;
            this.graphLogger = graphLogger;

            if (this.audioSocket != null)
            {
                this.audioSocket.AudioSendStatusChanged += this.OnAudioSendStatusChanged;

                if (this.audioSocket != null)
                {
                    try
                    {
                        var prop = this.audioSocket.GetType().GetProperty("LastKnownSendStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop != null)
                        {
                            var val = (MediaSendStatus)prop.GetValue(this.audioSocket);
                            this.IsAudioSendActive = (val == MediaSendStatus.Active);
                        }
                        else
                        {
                            this.IsAudioSendActive = true;
                        }
                    }
                    catch
                    {
                        this.IsAudioSendActive = true;
                    }

                    this.graphLogger?.Info($"[AudioSender] Initialized. IsAudioSendActive={this.IsAudioSendActive}");
                }
            }
        }

        private void OnAudioSendStatusChanged(object sender, AudioSendStatusChangedEventArgs e)
        {
            this.IsAudioSendActive = (e.MediaSendStatus == MediaSendStatus.Active);
            this.graphLogger?.Info($"[AudioSender] AudioSendStatusChanged: {e.MediaSendStatus}");
            Console.WriteLine($">>> [AudioSender] AudioSendStatus: {e.MediaSendStatus}");
        }

        /// <summary>
        /// Plays a standard WAV file (converted/extracted to 16kHz 16-bit mono PCM) into the meeting.
        /// </summary>
        public async Task PlayWavFileAsync(string wavFilePath, CancellationToken cancellationToken = default)
        {
            if (this.audioSocket == null || !File.Exists(wavFilePath))
            {
                this.graphLogger?.Warn($"AudioSender: WAV file not found: {wavFilePath}");
                return;
            }

            var pcmBytes = ExtractPcmFromWav(wavFilePath);
            if (pcmBytes == null || pcmBytes.Length == 0)
            {
                this.graphLogger?.Warn($"AudioSender: Failed to extract PCM bytes from {wavFilePath}");
                return;
            }

            await PlayPcmBytesAsync(pcmBytes, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Plays a sequence of raw 16kHz 16-bit mono PCM bytes in 20ms frames.
        /// Safely allocates per frame so AudioSendBuffer.Dispose() can free its memory without double-freeing.
        /// </summary>
        public async Task PlayPcmBytesAsync(byte[] pcmData, CancellationToken cancellationToken = default)
        {
            if (this.audioSocket == null || pcmData == null || pcmData.Length == 0)
            {
                return;
            }

            lock (this.playbackLock)
            {
                this.currentPlaybackCts?.Cancel();
                this.currentPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            var token = this.currentPlaybackCts.Token;

            // Wait up to 5 seconds if audio send is not yet active
            var startWait = DateTime.Now;
            while (!this.IsAudioSendActive && (DateTime.Now - startWait).TotalSeconds < 5 && !token.IsCancellationRequested)
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }

            uint timestamp = 0;
            int offset = 0;
            var frameBuffer = new byte[BytesPerFrame];

            try
            {
                while (offset < pcmData.Length && !token.IsCancellationRequested)
                {
                    if (this.IsMuted)
                    {
                        // Fill with silence if muted
                        Array.Clear(frameBuffer, 0, frameBuffer.Length);
                    }
                    else
                    {
                        int bytesToCopy = Math.Min(BytesPerFrame, pcmData.Length - offset);
                        Array.Copy(pcmData, offset, frameBuffer, 0, bytesToCopy);
                        if (bytesToCopy < BytesPerFrame)
                        {
                            Array.Clear(frameBuffer, bytesToCopy, BytesPerFrame - bytesToCopy);
                        }
                    }

                    if (this.IsAudioSendActive)
                    {
                        // Allocate unmanaged memory specifically for this frame.
                        // AudioSendBuffer.Dispose() calls Marshal.FreeHGlobal() on this pointer!
                        IntPtr unmanaged = Marshal.AllocHGlobal(BytesPerFrame);
                        Marshal.Copy(frameBuffer, 0, unmanaged, BytesPerFrame);

                        using (var audioMediaBuffer = new AudioSendBuffer(
                            unmanaged,
                            (long)BytesPerFrame,
                            AudioFormat.Pcm16K,
                            (long)timestamp))
                        {
                            this.audioSocket.Send(audioMediaBuffer);
                        }
                    }

                    offset += BytesPerFrame;
                    timestamp += FrameDurationMs;

                    await Task.Delay(FrameDurationMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                this.graphLogger?.Info("AudioSender: Audio playback was canceled.");
            }
            catch (Exception ex)
            {
                this.graphLogger?.Error(ex, "AudioSender: Error during audio frame playback.");
            }
        }

        /// <summary>
        /// Generates and plays a pleasant greeting chime (sine wave tone).
        /// </summary>
        public async Task PlayChimeAsync(CancellationToken cancellationToken = default)
        {
            int durationMs = 800;
            int totalSamples = (SampleRate * durationMs) / 1000;
            var pcm = new byte[totalSamples * 2];

            // Harmonic chord: D5 (587 Hz) then F#5 (740 Hz) then A5 (880 Hz)
            int chordDuration = totalSamples / 3;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / SampleRate;
                double freq = (i < chordDuration) ? 587.33 : (i < chordDuration * 2 ? 739.99 : 880.00);

                // Apply envelope (fade in and fade out)
                double env = Math.Sin(Math.PI * i / totalSamples);
                double sample = Math.Sin(2.0 * Math.PI * freq * t) * 0.4 * env;

                short sample16 = (short)(sample * short.MaxValue);
                pcm[i * 2] = (byte)(sample16 & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
            }

            await PlayPcmBytesAsync(pcm, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Synthesizes spoken voice from text using Windows System.Speech and plays it into the meeting.
        /// </summary>
        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text) || this.audioSocket == null)
            {
                return;
            }

            try
            {
                byte[] pcmData = null;
                using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                using (var ms = new MemoryStream())
                {
                    SelectConfiguredVoice(synth, this.graphLogger);
                    synth.Rate = -1; // slightly slower than default - clearer over a conference codec
                    var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(SampleRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono);
                    synth.SetOutputToAudioStream(ms, format);
                    synth.Speak(text);
                    pcmData = ms.ToArray();
                }

                if (pcmData != null && pcmData.Length > 0)
                {
                    this.graphLogger?.Info($"[AudioSender] Speaking text: {text} ({pcmData.Length} bytes)");
                    Console.WriteLine($">>> [AudioSender] Speaking into call: \"{text}\"");
                    await PlayPcmBytesAsync(pcmData, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.graphLogger?.Error(ex, $"[AudioSender] Failed to synthesize or play speech for '{text}'");
            }
        }

        /// <summary>
        /// Plays a welcome chime followed by a verbal introduction.
        /// </summary>
        public async Task PlayGreetingAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await PlayChimeAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                var greeting = TeamsCallingBot.Config.BotOptions.Current?.GreetingText;
                if (string.IsNullOrWhiteSpace(greeting))
                {
                    greeting = "Hello everyone, I am the AI meeting assistant. I have joined to record this meeting.";
                }

                await SpeakAsync(greeting, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.graphLogger?.Error(ex, "[AudioSender] Error playing greeting.");
            }
        }

        private static bool voicesLogged;

        /// <summary>
        /// Picks the TTS voice: exact Bot:TtsVoiceName if installed, otherwise the first installed
        /// voice whose culture matches Bot:TtsCulture (default en-IN = Indian English, e.g.
        /// "Microsoft Heera Desktop" / "Microsoft Ravi"), preferring Bot:TtsVoiceGender. Falls back
        /// to the system default voice. Installed voices are logged once so operators can see what
        /// the VM actually has (see docs for how to expose Windows OneCore voices to System.Speech).
        /// </summary>
        internal static void SelectConfiguredVoice(System.Speech.Synthesis.SpeechSynthesizer synth, IGraphLogger logger)
        {
            var options = TeamsCallingBot.Config.BotOptions.Current;
            string wantedName = options?.TtsVoiceName;
            string wantedCulture = string.IsNullOrWhiteSpace(options?.TtsCulture) ? "en-IN" : options.TtsCulture;
            bool wantMale = string.Equals(options?.TtsVoiceGender, "Male", StringComparison.OrdinalIgnoreCase);

            try
            {
                var installed = synth.GetInstalledVoices();
                if (!voicesLogged)
                {
                    voicesLogged = true;
                    foreach (var v in installed)
                    {
                        var line = $"[TTS] Installed voice: {v.VoiceInfo.Name} ({v.VoiceInfo.Culture.Name}, {v.VoiceInfo.Gender}, enabled={v.Enabled})";
                        logger?.Info(line);
                        Console.WriteLine(">>> " + line);
                    }
                }

                if (!string.IsNullOrWhiteSpace(wantedName))
                {
                    foreach (var v in installed)
                    {
                        if (v.Enabled && string.Equals(v.VoiceInfo.Name, wantedName, StringComparison.OrdinalIgnoreCase))
                        {
                            synth.SelectVoice(v.VoiceInfo.Name);
                            return;
                        }
                    }

                    logger?.Warn($"[TTS] Configured voice '{wantedName}' is not installed - falling back to culture match.");
                }

                System.Speech.Synthesis.InstalledVoice best = null;
                foreach (var v in installed)
                {
                    if (!v.Enabled || !string.Equals(v.VoiceInfo.Culture.Name, wantedCulture, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isMale = v.VoiceInfo.Gender == System.Speech.Synthesis.VoiceGender.Male;
                    if (best == null || isMale == wantMale)
                    {
                        best = v;
                        if (isMale == wantMale)
                        {
                            break;
                        }
                    }
                }

                if (best != null)
                {
                    synth.SelectVoice(best.VoiceInfo.Name);
                    return;
                }

                // Last resort: let SAPI pick by hints (may still choose a non en-IN voice).
                synth.SelectVoiceByHints(
                    wantMale ? System.Speech.Synthesis.VoiceGender.Male : System.Speech.Synthesis.VoiceGender.Female,
                    System.Speech.Synthesis.VoiceAge.Adult,
                    0,
                    new System.Globalization.CultureInfo(wantedCulture));
                logger?.Warn($"[TTS] No installed voice for culture {wantedCulture}; using '{synth.Voice?.Name}'. Install the '{wantedCulture}' speech pack for an Indian English voice.");
            }
            catch (Exception ex)
            {
                logger?.Warn($"[TTS] Voice selection failed ({ex.Message}) - using default voice.");
            }
        }

        public void Stop()
        {
            lock (this.playbackLock)
            {
                this.currentPlaybackCts?.Cancel();
            }
        }

        public void Dispose()
        {
            this.Stop();
            if (this.audioSocket != null)
            {
                this.audioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
            }
        }

        private static byte[] ExtractPcmFromWav(string wavFilePath)
        {
            using (var stream = File.OpenRead(wavFilePath))
            using (var reader = new BinaryReader(stream))
            {
                // Check RIFF header
                var riff = new string(reader.ReadChars(4));
                if (riff != "RIFF") return null;

                reader.ReadInt32(); // chunk size
                var wave = new string(reader.ReadChars(4));
                if (wave != "WAVE") return null;

                // Find data chunk
                while (stream.Position < stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = reader.ReadInt32();

                    if (chunkId == "data")
                    {
                        return reader.ReadBytes(chunkSize);
                    }

                    stream.Seek(chunkSize, SeekOrigin.Current);
                }
            }

            return null;
        }
    }
}
