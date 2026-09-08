namespace TeamsCallingBot.Video
{
    using System;
    using Microsoft.Skype.Bots.Media;

    /// <summary>
    /// Custom VideoMediaBuffer that does NOT free its unmanaged pointer on Dispose().
    /// Memory lifecycle is managed by the caller pool in CallHandler.StartBotVideoBroadcast.
    /// This prevents native video hardware/software encoder use-after-free crashes (0xc0000005)
    /// which occur when using the standard VideoSendBuffer with immediate disposal.
    /// </summary>
    public class SafeVideoMediaBuffer : VideoMediaBuffer
    {
        public SafeVideoMediaBuffer(IntPtr data, long length, VideoFormat videoFormat, long timestamp)
        {
            this.Data = data;
            this.Length = length;
            this.VideoFormat = videoFormat;
            this.Timestamp = timestamp;
        }

        protected override void Dispose(bool disposing)
        {
            // Intentionally no-op.
            // VideoSendBuffer's Dispose() calls Marshal.FreeHGlobal(this.Data) immediately,
            // which causes native video encoder worker threads in mpencoder.dll / MediaApi.dll
            // to crash with Access Violation (0xc0000005) when reading the freed memory.
            // Here memory is pooled and safely freed only after the broadcast loop stops.
        }
    }
}
