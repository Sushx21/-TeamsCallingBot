// Copied verbatim (namespace changed only) from Microsoft's own sample repo:
// Samples/Common/Sample.Common/CommonUtilities.cs (microsoft-graph-comms-samples, MIT licensed).
namespace TeamsCallingBot.Common
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Common.Telemetry;

    public static class CommonUtilities
    {
        public static async Task ForgetAndLogExceptionAsync(
            this Task task,
            IGraphLogger logger,
            string description = null,
            [CallerMemberName] string memberName = null,
            [CallerFilePath] string filePath = null,
            [CallerLineNumber] int lineNumber = 0)
        {
            try
            {
                await task.ConfigureAwait(false);
                logger?.Verbose($"Completed running task successfully: {description ?? string.Empty}", memberName: memberName, filePath: filePath, lineNumber: lineNumber);
            }
            catch (Exception e)
            {
                logger?.Error(e, $"Caught an Exception running the task: {description ?? string.Empty}", memberName: memberName, filePath: filePath, lineNumber: lineNumber);
            }
        }
    }
}
