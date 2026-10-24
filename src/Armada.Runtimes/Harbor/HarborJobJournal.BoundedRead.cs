namespace Armada.Runtimes.Harbor
{
    using System.IO;
    using System.Text;

    /// <summary>Reads bounded UTF-8 metadata without loading an untrusted file without a ceiling.</summary>
    public sealed partial class HarborJobJournal
    {
        private const int MaximumMetadataBytes = 64 * 1024;

        private string ReadBoundedTextFile(string path, string tooLargeReason, string invalidReason)
        {
            EnsureFile(path);
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumMetadataBytes)
                        throw Failure(tooLargeReason, "The metadata file exceeds its size limit.");
                    byte[] bytes = new byte[MaximumMetadataBytes + 1];
                    int total = 0;
                    while (total < bytes.Length)
                    {
                        int read = stream.Read(bytes, total, bytes.Length - total);
                        if (read == 0) break;
                        total += read;
                    }
                    if (total > MaximumMetadataBytes || stream.Length > MaximumMetadataBytes)
                        throw Failure(tooLargeReason, "The metadata file exceeds its size limit.");
                    return new UTF8Encoding(false, true).GetString(bytes, 0, total);
                }
            }
            catch (HarborJobJournalException)
            {
                throw;
            }
            catch (Exception exception) when (exception is DecoderFallbackException || exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Failure(invalidReason, "The metadata file cannot be read as bounded UTF-8 text.", exception);
            }
        }
    }
}
