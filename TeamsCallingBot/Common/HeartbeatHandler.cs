// Copied verbatim (namespace changed only) from Microsoft's own sample repo:
// Samples/Common/Sample.Common/HeartbeatHandler.cs (microsoft-graph-comms-samples, MIT licensed).
namespace TeamsCallingBot.Common
{
    using System;
    using System.Threading.Tasks;
    using System.Timers;
    using Microsoft.Graph.Communications.Common;
    using Microsoft.Graph.Communications.Common.Telemetry;

    public abstract class HeartbeatHandler : ObjectRootDisposable
    {
        private readonly Timer heartbeatTimer;

        public HeartbeatHandler(TimeSpan frequency, IGraphLogger logger)
            : base(logger)
        {
            var timer = new Timer(frequency.TotalMilliseconds)
            {
                Enabled = true,
                AutoReset = true,
            };
            timer.Elapsed += this.HeartbeatDetected;
            this.heartbeatTimer = timer;
        }

        protected abstract Task HeartbeatAsync(ElapsedEventArgs args);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            this.heartbeatTimer.Elapsed -= this.HeartbeatDetected;
            this.heartbeatTimer.Stop();
            this.heartbeatTimer.Dispose();
        }

        private void HeartbeatDetected(object sender, ElapsedEventArgs args)
        {
            _ = Task.Run(() => this.HeartbeatAsync(args)).ForgetAndLogExceptionAsync(this.GraphLogger);
        }
    }
}
