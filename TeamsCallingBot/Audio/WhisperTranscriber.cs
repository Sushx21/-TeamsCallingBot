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

        private static string ResolveModelPath()
        {
            var candidates = new[]
            {
                @"C:\whisper\models\ggml-base.en.bin",
                @"C:\whisper\models\ggml-small.en.bin",
                @"C:\whisper\models\ggml-base.bin",
                @"C:\whisper\models\ggml-small.bin",
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return @"C:\whisper\models\ggml-base.en.bin";
        }

        public static async Task<string> TranscribeAsync(string wavPath)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            {
                return string.Empty;
            }

            try
            {
                if (File.Exists(WhisperExecutablePath))
                {
                    var modelPath = ResolveModelPath();
                    var psi = new ProcessStartInfo
                    {
                        FileName = WhisperExecutablePath,
                        Arguments = $"-m \"{modelPath}\" -f \"{wavPath}\" -otxt -of \"{wavPath}\" -l en --no-timestamps -sns -nth 0.65 -nf -t 4",
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
                        return CleanTranscript(transcribed);
                    }
                }

                return string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string CleanTranscript(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanTokens = new System.Collections.Generic.List<string>();
            string lastToken = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Strip hallucinated bracket cues like [BLANK_AUDIO], (speaking in foreign language), (laughter), etc.
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) continue;
                if (trimmed.StartsWith("(") && trimmed.EndsWith(")")) continue;
                if (trimmed.IndexOf("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (trimmed.IndexOf("(speaking in foreign language)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (trimmed.IndexOf("(laughter)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (trimmed.IndexOf("(laughing)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (trimmed.IndexOf("(music)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (trimmed.IndexOf("(applause)", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (trimmed.StartsWith("- ")) trimmed = trimmed.Substring(2).Trim();
                if (trimmed == "-") continue;

                // Strip single isolated filler tokens
                if (trimmed.Equals("you", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("I", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Ensure there are real alphabetic characters
                int letterCount = 0;
                foreach (char c in trimmed)
                {
                    if (char.IsLetter(c)) letterCount++;
                }
                if (letterCount < 3) continue;

                // Deduplicate immediate repetition loops
                if (string.Equals(lastToken, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lastToken = trimmed;
                cleanTokens.Add(trimmed);
            }
            return string.Join(" ", cleanTokens).Trim();
        }

        /// <summary>
        /// net472 has no Process.WaitForExitAsync (that's a .NET 5+ extension method) - this is the
        /// manual equivalent via the Exited event, needed to avoid blocking a thread on WaitForExit().
        ///
        /// FIX (2026-09-09): the previous version left RedirectStandardOutput/Error true but never
        /// drained either pipe. whisper.cpp is very chatty on stderr; once the OS pipe buffer (~4 KB)
        /// filled, the child blocked on write, never exited, and the Exited event never fired - the
        /// transcription Task hung forever and the whole live-transcript loop stalled. We now
        /// asynchronously drain both pipes (BeginOutput/ErrorReadLine) so the child can never block,
        /// and add a hard timeout so a wedged process can't hang a meeting.
        /// </summary>
        private static async Task RunAndWaitAsync(Process process)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            process.Exited += (s, e) => tcs.TrySetResult(true);

            // Discard child output, but KEEP READING it so the pipe never fills and blocks the child.
            process.OutputDataReceived += (s, e) => { };
            process.ErrorDataReceived += (s, e) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.HasExited)
            {
                tcs.TrySetResult(true);
                return;
            }

            // Cap the wait so a hung whisper-cli can't stall the meeting indefinitely.
            var timeout = Task.Delay(TimeSpan.FromMinutes(10));
            var completed = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
            if (completed == timeout)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch { /* best effort */ }
            }
        }
    }
}
