namespace TeamsCallingBot.Storage
{
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Google.Cloud.Storage.V1;

    /// <summary>
    /// Uploads the finished transcript to GCS. Auth comes from GOOGLE_APPLICATION_CREDENTIALS,
    /// the same pattern the rest of this codebase already uses (e.g. svc-openai-prod.json) -
    /// deliberately no separate credential-handling code here.
    ///
    /// Needs the Google.Cloud.Storage.V1 NuGet package added (not pre-added to the .csproj - see
    /// the TODO note there).
    /// </summary>
    public static class GcsTranscriptUploader
    {
        private const string BucketName = "TODO-confirm-real-bucket-name";

        public static async Task UploadAsync(string meetingId, string transcriptText)
        {
            if (string.IsNullOrEmpty(transcriptText))
            {
                return;
            }

            var storage = await StorageClient.CreateAsync().ConfigureAwait(false);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcriptText)))
            {
                await storage.UploadObjectAsync(BucketName, $"{meetingId}.txt", "text/plain", stream).ConfigureAwait(false);
            }
        }
    }
}
