// Copied verbatim (namespace changed only) from Microsoft's own sample repo:
// Samples/Common/Sample.Common/Logging/SampleObserver.cs (microsoft-graph-comms-samples, MIT licensed).
namespace TeamsCallingBot.Common
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Common.Telemetry;

    public class SampleObserver : IObserver<LogEvent>, IDisposable
    {
        private static readonly int MaxLogCount = 5000;
        private IDisposable subscription;
        private LinkedList<string> logs = new LinkedList<string>();
        private readonly object lockLogs = new object();
        private ILogEventFormatter formatter = new CommsLogEventFormatter();

        public SampleObserver(IGraphLogger logger)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.Error(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) => logger.Error(e.Exception, "Unobserved task exception");
            this.subscription = logger.Subscribe(this);
        }

        public void OnNext(LogEvent logEvent)
        {
            if (logEvent.EventType == LogEventType.Metric)
            {
                return;
            }

            // Real gap fixed (2026-09-04), two layers deep:
            //   1. This filter originally dropped everything except HttpTrace/Error/Warning -
            //      GraphLogger.Info(...) calls (Bot.cs/CallHandler.cs's OWN call-lifecycle
            //      narration, e.g. "Call {id} terminated - starting wind-down") are Info level,
            //      so they never survived this filter at all, regardless of any sink below.
            //   2. Even what DID survive only ever got buffered into an in-memory LinkedList
            //      with no route/console sink reading it - Microsoft's own sample repo exposes
            //      this buffer via a diagnostics endpoint, which this project never added.
            // Together these silently swallowed the ONE set of logs that would explain why a
            // call actually joins then leaves - both layers must be fixed together, fixing only
            // the sink (as an earlier pass here did, for a different class - AuthenticationProvider's
            // OverrideBearerToken diagnostic - by switching to plain Console.WriteLine instead of
            // going through GraphLogger at all) still shows nothing for Info-level events routed
            // through THIS observer.
            if (logEvent.EventType != LogEventType.HttpTrace &&
                logEvent.Level != TraceLevel.Error && logEvent.Level != TraceLevel.Warning &&
                logEvent.Level != TraceLevel.Info)
            {
                return;
            }

            var logString = this.formatter.Format(logEvent);

            // Console.WriteLine here (not just the buffer below) - this is what actually makes any
            // of this visible in real time on the console you're watching during a test run.
            Console.WriteLine($">>> [{logEvent.Level}] {logString}");

            // Persist to session log file on disk (C:\ or Downloads)
            TeamsCallingBot.Storage.RecordingsManager.Current?.Log($"[{logEvent.Level}] {logString}");

            lock (this.lockLogs)
            {
                this.logs.AddFirst(logString);
                if (this.logs.Count > MaxLogCount)
                {
                    this.logs.RemoveLast();
                }
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void Dispose()
        {
            lock (this.lockLogs)
            {
                this.logs?.Clear();
                this.logs = null;
            }

            this.subscription?.Dispose();
            this.subscription = null;
            this.formatter = null;
        }
    }
}
