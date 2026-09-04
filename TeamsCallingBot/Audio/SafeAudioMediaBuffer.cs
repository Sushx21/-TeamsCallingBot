namespace TeamsCallingBot.Audio
{
    using System;
    using Microsoft.Skype.Bots.Media;

    /// <summary>
    /// Custom AudioMediaBuffer that does NOT free its unmanaged pointer on Dispose().
    /// Memory lifecycle is managed by the AudioSender frame pool.
    /// This prevents native media platform use-after-free or double-free crashes (0xc0000005)
    /// when transmitting audio frames.
    /// </summary>
    public class SafeAudioMediaBuffer : AudioMediaBuffer
    {
        public SafeAudioMediaBuffer(IntPtr data, long length, AudioFormat audioFormat, long timestamp)
        {
            this.Data = data;
            this.Length = length;
            this.AudioFormat = audioFormat;
            this.Timestamp = timestamp;
        }

        protected override void Dispose(bool disposing)
        {
            // Intentionally no-op. Memory is owned and freed by the AudioSender pool.
        }
    }
}
