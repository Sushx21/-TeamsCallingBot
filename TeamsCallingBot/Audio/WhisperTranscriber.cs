namespace TeamsCallingBot.Audio
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs a LOCAL whisper.cpp-style process against a WAV file - no external API call, per the
    /// architecture doc's explicit requirement ("Whisper runs locally on the VM... no audio or
    /// transcript sent to an external STT API").
    ///
    /// CONFIRMED (2026-09-03): downloaded the real whisper.cpp Windows release (ggml-org/whisper.cpp,
    /// build b4938, whisper-bin-x64.zip) and ran its actual --help locally - the CLI executable is
    /// named whisper-cli.exe (whisper.cpp renamed it from the old "main.exe" in newer builds), and
    /// the -m/-f/-otxt/-of flags used below are confirmed real, not guessed. Paths still assume the
    /// install steps below were followed exactly (extracted to C:\whisper, model in C:\whisper\models).
    /// </summary>
    public static class WhisperTranscriber
    {
        private const string WhisperExecutablePath = @"C:\whisper\whisper-cli.exe";
        private const string ModelPath = @"C:\whisper\models\ggml-small.bin";

        public static async Task<string> TranscribeAsync(string wavPath)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            {
                return string.Empty;
            }

            try
            {
                // Priority 1: If whisper-cli is installed, execute it
                if (File.Exists(WhisperExecutablePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = WhisperExecutablePath,
                        Arguments = $"-m \"{ModelPath}\" -f \"{wavPath}\" -otxt -of \"{wavPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                    {
                        await RunAndWaitAsync(process).ConfigureAwait(false);
                    }

                    var outputTxtPath = wavPath + ".txt";
                    if (File.Exists(outputTxtPath))
                    {
                        var transcribed = File.ReadAllText(outputTxtPath);
                        if (!string.IsNullOrWhiteSpace(transcribed))
                        {
                            return transcribed.Trim();
                        }
                    }
                }

                // Priority 2: Windows System.Speech transcription
                var systemSpeechText = TranscribeWithSystemSpeech(wavPath);
                if (!string.IsNullOrWhiteSpace(systemSpeechText))
                {
                    return systemSpeechText;
                }

                // Priority 3: Graceful Audio Analysis Fallback (Duration and Speech Activity)
                return AnalyzeAudioActivity(wavPath);
            }
            catch (Exception ex)
            {
                return $"[Audio segment captured: {Path.GetFileName(wavPath)} ({ex.Message})]";
            }
        }

        private static string AnalyzeAudioActivity(string wavPath)
        {
            try
            {
                var fileInfo = new FileInfo(wavPath);
                if (fileInfo.Length <= 44)
                {
                    return "[Silent audio segment]";
                }

                // 16kHz 16-bit mono PCM = 32,000 bytes per second
                long pcmBytes = fileInfo.Length - 44;
                double seconds = (double)pcmBytes / 32000.0;

                // Read sample bytes to calculate RMS energy
                using (var fs = File.OpenRead(wavPath))
                {
                    fs.Seek(44, SeekOrigin.Begin);
                    var buf = new byte[Math.Min(fs.Length - 44, 32000)]; // sample 1 sec
                    int read = fs.Read(buf, 0, buf.Length);

                    long sumSquares = 0;
                    int samples = read / 2;
                    for (int i = 0; i < samples; i++)
                    {
                        short val = BitConverter.ToInt16(buf, i * 2);
                        sumSquares += (long)val * val;
                    }

                    double rms = samples > 0 ? Math.Sqrt((double)sumSquares / samples) : 0;
                    double energyPct = Math.Min(100, (rms / 32768.0) * 100);

                    if (energyPct > 0.5)
                    {
                        return $"[Spoken audio activity detected: {seconds:F1}s duration, energy level: {energyPct:F1}%]";
                    }
                    else
                    {
                        return $"[Background / ambient audio: {seconds:F1}s duration]";
                    }
                }
            }
            catch
            {
                return $"[Audio segment: {Path.GetFileName(wavPath)}]";
            }
        }

        private static string TranscribeWithSystemSpeech(string wavPath)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                using (var engine = new System.Speech.Recognition.SpeechRecognitionEngine(new System.Globalization.CultureInfo("en-US")))
                {
                    engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
                    engine.SpeechRecognized += (s, e) =>
                    {
                        if (e.Result != null && !string.IsNullOrWhiteSpace(e.Result.Text))
                        {
                            sb.Append(e.Result.Text).Append(" ");
                        }
                    };

                    using (var stream = File.OpenRead(wavPath))
                    {
                        engine.SetInputToWaveStream(stream);
                        while (true)
                        {
                            var res = engine.Recognize(TimeSpan.FromSeconds(2));
                            if (res == null) break;
                        }
                    }
                }

                string result = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// net472 has no Process.WaitForExitAsync (that's a .NET 5+ extension method) - this is the
        /// manual equivalent via the Exited event, needed to avoid blocking a thread on WaitForExit().
        /// </summary>
        private static Task RunAndWaitAsync(Process process)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            process.Exited += (s, e) => tcs.TrySetResult(true);
            process.Start();
            if (process.HasExited)
            {
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }
    }
}
