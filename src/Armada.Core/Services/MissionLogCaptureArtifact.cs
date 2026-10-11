namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Stores and reads bounded, mission-owned diagnostic captures beside the mission log.</summary>
    public static class MissionLogCaptureArtifact
    {
        /// <summary>Default capture page size in bytes.</summary>
        public const int DefaultPageLengthBytes = 16000;

        /// <summary>Largest capture page size in bytes.</summary>
        public const int MaximumPageLengthBytes = 64000;
        private const string StartPrefix = "[ARMADA:DOD_CAPTURE_START] ";
        private const string EndPrefix = "[ARMADA:DOD_CAPTURE_END] ";
        private static readonly Regex _MissionIdPattern = new Regex(@"^msn_[A-Za-z0-9_]{1,120}$", RegexOptions.Compiled);

        /// <summary>Write one redacted output capture atomically beside its mission log.</summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission that owns the capture.</param>
        /// <param name="output">Output retained by the bounded command runner.</param>
        /// <param name="runnerOutputTruncated">Whether a runner stream reached its byte limit.</param>
        /// <param name="runnerOutputOmittedBytes">Bytes omitted by the runner.</param>
        /// <param name="runnerOutputIncomplete">Whether timeout, drain or containment state may have omitted output.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Capture identity, byte length, digest and runner completeness metadata.</returns>
        public static async Task<MissionLogCaptureMetadata> WriteAsync(
            string logDirectory, string missionId, string output, bool runnerOutputTruncated = false,
            long runnerOutputOmittedBytes = 0, bool runnerOutputIncomplete = false, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            ValidateMissionId(missionId);
            if (output == null) throw new ArgumentNullException(nameof(output));

            string safeOutput = RuntimeLogFormatter.RedactSecrets(output);
            byte[] body = Encoding.UTF8.GetBytes(safeOutput);
            string captureId = Guid.NewGuid().ToString("N");
            string sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            long omittedBytes = Math.Max(0, runnerOutputOmittedBytes);
            bool outputIncomplete = runnerOutputIncomplete || runnerOutputTruncated || omittedBytes > 0;
            string start = StartPrefix + captureId + " " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + sha256 + " " + (runnerOutputTruncated ? "1" : "0") + " "
                + omittedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "
                + (outputIncomplete ? "1" : "0") + "\n";
            string end = "\n" + EndPrefix + captureId + "\n";
            byte[] header = Encoding.UTF8.GetBytes(start);
            byte[] footer = Encoding.UTF8.GetBytes(end);
            string directory = Path.Combine(logDirectory, "missions");
            Directory.CreateDirectory(directory);
            string fileName = missionId + ".dod-" + captureId + ".log";
            string finalPath = Path.Combine(directory, fileName);
            string tempPath = finalPath + ".tmp";

            try
            {
                await using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(header, token).ConfigureAwait(false);
                    await stream.WriteAsync(body, token).ConfigureAwait(false);
                    await stream.WriteAsync(footer, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                File.Move(tempPath, finalPath);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }

            return new MissionLogCaptureMetadata
            {
                CaptureId = captureId,
                TotalUtf8Bytes = body.LongLength,
                Sha256 = sha256,
                RunnerOutputTruncated = runnerOutputTruncated,
                RunnerOutputOmittedBytes = omittedBytes,
                RunnerOutputIncomplete = outputIncomplete
            };
        }

        /// <summary>Read one bounded UTF-8 byte page from a mission-owned capture.</summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission that owns the capture.</param>
        /// <param name="captureId">Capture identifier from the mission's evaluation record.</param>
        /// <param name="offsetBytes">Zero-based requested byte offset.</param>
        /// <param name="lengthBytes">Requested page length in bytes.</param>
        /// <param name="expectedSha256">Expected digest from the evaluation record, when supplied.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A bounded page with the stable whole-capture digest or a named unavailable state.</returns>
        public static async Task<MissionLogCapturePage> ReadPageAsync(
            string logDirectory, string missionId, string captureId, long offsetBytes, int lengthBytes,
            string? expectedSha256 = null, CancellationToken token = default)
        {
            ValidateMissionId(missionId);
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            if (!Guid.TryParseExact(captureId, "N", out _)) throw new ArgumentException("captureId is outside the valid format", nameof(captureId));
            if (offsetBytes < 0) throw new ArgumentOutOfRangeException(nameof(offsetBytes));
            if (lengthBytes <= 0 || lengthBytes > MaximumPageLengthBytes) throw new ArgumentOutOfRangeException(nameof(lengthBytes));

            string path = Path.Combine(logDirectory, "missions", missionId + ".dod-" + captureId + ".log");
            if (!File.Exists(path)) return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture not found" };

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    string header = await ReadLineAsync(stream, 512, token).ConfigureAwait(false);
                    string[] fields = header.StartsWith(StartPrefix, StringComparison.Ordinal)
                        ? header.Substring(StartPrefix.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        : Array.Empty<string>();
                    if (fields.Length != 6 || !String.Equals(fields[0], captureId, StringComparison.Ordinal)
                        || !Int64.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long totalBytes)
                        || totalBytes < 0 || !Regex.IsMatch(fields[2], "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)
                        || (fields[3] != "0" && fields[3] != "1")
                        || !Int64.TryParse(fields[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long omittedBytes)
                        || omittedBytes < 0 || (fields[5] != "0" && fields[5] != "1"))
                        return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture header is invalid" };
                    string sha256 = fields[2].ToLowerInvariant();
                    if (!String.IsNullOrEmpty(expectedSha256) && !String.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture hash does not match its evaluation record" };

                    long contentStart = stream.Position;
                    long footerStart = contentStart + totalBytes;
                    if (footerStart < contentStart || footerStart + Encoding.UTF8.GetByteCount("\n" + EndPrefix + captureId + "\n") != stream.Length)
                        return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture length does not match its markers" };
                    stream.Position = footerStart;
                    string footer = await ReadToEndAsync(stream, 256, token).ConfigureAwait(false);
                    if (!String.Equals(footer, "\n" + EndPrefix + captureId + "\n", StringComparison.Ordinal))
                        return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture end marker is invalid" };

                    if (offsetBytes > totalBytes) throw new ArgumentOutOfRangeException(nameof(offsetBytes));
                    int pageLimit = Math.Max(lengthBytes, 4);
                    long start = AlignForward(stream, contentStart, totalBytes, offsetBytes);
                    long end = Math.Min(totalBytes, start + pageLimit);
                    end = AlignBackward(stream, contentStart, end);
                    if (end < start) end = start;
                    int pageBytes = checked((int)(end - start));
                    byte[] content = new byte[pageBytes];
                    stream.Position = contentStart + start;
                    int read = 0;
                    while (read < content.Length)
                    {
                        int count = await stream.ReadAsync(content.AsMemory(read), token).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                    }
                    if (read != content.Length) return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture page ended early" };

                    return new MissionLogCapturePage
                    {
                        MissionId = missionId,
                        CaptureId = captureId,
                        Content = Encoding.UTF8.GetString(content),
                        OffsetBytes = start,
                        LengthBytes = pageBytes,
                        TotalUtf8Bytes = totalBytes,
                        Sha256 = sha256,
                        HasMore = end < totalBytes,
                        Complete = fields[5] == "0",
                        RunnerOutputTruncated = fields[3] == "1",
                        RunnerOutputOmittedBytes = omittedBytes,
                        RunnerOutputIncomplete = fields[5] == "1"
                    };
                }
            }
            catch (FileNotFoundException)
            {
                return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture not found" };
            }
            catch (IOException)
            {
                return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture could not be read" };
            }
            catch (UnauthorizedAccessException)
            {
                return new MissionLogCapturePage { MissionId = missionId, CaptureId = captureId, Error = "capture could not be read" };
            }
        }

        /// <summary>Return true when an ID uses the exact safe capture format.</summary>
        /// <param name="captureId">Candidate ID.</param>
        /// <returns>True for a 32-character GUID in compact format.</returns>
        public static bool IsCaptureId(string? captureId) => Guid.TryParseExact(captureId, "N", out _);

        private static void ValidateMissionId(string missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId) || !_MissionIdPattern.IsMatch(missionId))
                throw new ArgumentException("missionId is outside the valid format", nameof(missionId));
        }

        private static long AlignForward(FileStream stream, long start, long length, long offset)
        {
            long aligned = offset;
            byte[] one = new byte[1];
            while (aligned < length)
            {
                stream.Position = start + aligned;
                if (stream.Read(one, 0, 1) != 1 || (one[0] & 0xC0) != 0x80) break;
                aligned++;
            }
            return aligned;
        }

        private static long AlignBackward(FileStream stream, long start, long offset)
        {
            long aligned = offset;
            byte[] one = new byte[1];
            for (int i = 0; i < 3 && aligned > 0; i++)
            {
                stream.Position = start + aligned;
                if (stream.Read(one, 0, 1) != 1 || (one[0] & 0xC0) != 0x80) break;
                aligned--;
            }
            return aligned;
        }

        private static async Task<string> ReadLineAsync(FileStream stream, int maximumBytes, CancellationToken token)
        {
            byte[] buffer = new byte[maximumBytes];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count, 1), token).ConfigureAwait(false);
                if (read == 0 || buffer[count] == (byte)'\n') break;
                count++;
            }
            if (count == buffer.Length) return String.Empty;
            return Encoding.UTF8.GetString(buffer, 0, count);
        }

        private static async Task<string> ReadToEndAsync(FileStream stream, int maximumBytes, CancellationToken token)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[128];
                while (memory.Length <= maximumBytes)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)memory.Length)), token).ConfigureAwait(false);
                    if (read == 0) break;
                    await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (memory.Length > maximumBytes) return String.Empty;
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
    }
}
