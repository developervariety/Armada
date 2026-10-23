namespace Armada.Core.Services.Archive
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// <see cref="IArchiveSink"/> for any S3-compatible object store, signed with the S3 Signature
    /// Version 4 scheme. It needs only PUT, HEAD, DELETE and ListObjectsV2, so it carries no SDK. Each upload
    /// sends the content's MD5 and SHA-256, which the store checks against the bytes it receives, and
    /// then reads the stored object back with HEAD; it is verified only when the stored size and the
    /// SHA-256 recorded with it match the local content.
    /// </summary>
    public sealed class S3ArchiveSink : IArchiveSink
    {
        #region Public-Members

        /// <summary>
        /// Largest object one PUT may carry. Larger files are refused with <c>too_large</c> and kept.
        /// </summary>
        public const long MaxSinglePutBytes = 5L * 1024 * 1024 * 1024;

        /// <summary>
        /// Object metadata header that carries the content's SHA-256.
        /// </summary>
        public const string Sha256MetadataHeader = "x-amz-meta-sha256";

        #endregion

        #region Private-Members

        private const string _Service = "s3";
        private const string _Algorithm = "AWS4-HMAC-SHA256";
        private const string _EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        private static readonly HttpClient _SharedClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private readonly ArchiveSettings _Settings;
        private readonly HttpClient _Http;
        private readonly Func<DateTime> _Clock;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate over the archive settings.
        /// </summary>
        /// <param name="settings">Archive settings; endpoint, bucket and credentials file must be set.</param>
        /// <param name="http">HTTP client; a shared client is used when null.</param>
        /// <param name="clock">UTC clock used to sign requests; the system clock when null.</param>
        public S3ArchiveSink(ArchiveSettings settings, HttpClient? http = null, Func<DateTime>? clock = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Http = http ?? _SharedClient;
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ArchivePutResult> PutFileAsync(string key, string localPath, string sha256, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
            if (String.IsNullOrWhiteSpace(localPath)) throw new ArgumentNullException(nameof(localPath));
            FileInfo info = new FileInfo(localPath);
            if (!info.Exists) return ArchivePutResult.Failed(key, "local_missing", "local file " + localPath + " does not exist");
            if (info.Length > MaxSinglePutBytes)
                return ArchivePutResult.Failed(key, "too_large", "file is " + info.Length + " bytes, above the " + MaxSinglePutBytes + " byte single-upload limit");

            string md5;
            using (FileStream stream = OpenRead(localPath))
            {
                md5 = Convert.ToBase64String(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
            }

            return await PutAsync(key, () => new StreamContent(OpenRead(localPath)), info.Length, sha256.ToLowerInvariant(), md5, "application/octet-stream", token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ArchivePutResult> VerifyStoredAsync(string key, long bytes, string sha256, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
            if (String.IsNullOrWhiteSpace(sha256)) throw new ArgumentNullException(nameof(sha256));
            Credentials credentials;
            try
            {
                credentials = ReadCredentials();
            }
            catch (InvalidOperationException ex)
            {
                return ArchivePutResult.Failed(key, "credentials_missing", ex.Message);
            }

            try
            {
                return await HeadAndCompareAsync(key, bytes, sha256.ToLowerInvariant(), credentials, token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return ArchivePutResult.Failed(key, "verify_failed", "stored object '" + key + "' could not be read back: " + ex.Message);
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                return ArchivePutResult.Failed(key, "verify_failed", "stored object '" + key + "' could not be read back in time: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task<ArchivePutResult> PutBytesAsync(string key, byte[] content, string contentType, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
            if (content == null) throw new ArgumentNullException(nameof(content));
            string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            string md5 = Convert.ToBase64String(MD5.HashData(content));
            return await PutAsync(key, () => new ByteArrayContent(content), content.LongLength, sha256, md5,
                String.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ArchiveObjectInfo>> ListAsync(string prefix, CancellationToken token = default)
        {
            Credentials credentials = ReadCredentials();
            List<ArchiveObjectInfo> objects = new List<ArchiveObjectInfo>();
            string? continuation = null;
            do
            {
                SortedDictionary<string, string> query = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    { "list-type", "2" },
                    { "prefix", prefix ?? String.Empty }
                };
                if (continuation != null) query["continuation-token"] = continuation;

                using (HttpRequestMessage request = BuildRequest(HttpMethod.Get, null, query, credentials, _EmptySha256, null))
                using (HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("archive list of '" + prefix + "' failed: HTTP " + (int)response.StatusCode + " " + ErrorCode(body));

                    XDocument document = XDocument.Parse(body);
                    XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
                    foreach (XElement item in document.Descendants(ns + "Contents"))
                    {
                        objects.Add(new ArchiveObjectInfo
                        {
                            Key = item.Element(ns + "Key")?.Value ?? String.Empty,
                            Bytes = Int64.TryParse(item.Element(ns + "Size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ? size : 0,
                            LastModifiedUtc = DateTime.TryParse(item.Element(ns + "LastModified")?.Value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime modified) ? modified : DateTime.MinValue,
                            ETag = item.Element(ns + "ETag")?.Value?.Trim('"')
                        });
                    }
                    bool truncated = String.Equals(document.Root?.Element(ns + "IsTruncated")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                    continuation = truncated ? document.Root?.Element(ns + "NextContinuationToken")?.Value : null;
                }
            }
            while (continuation != null);
            return objects;
        }

        /// <inheritdoc />
        public async Task<byte[]?> GetBytesAsync(string key, long maxBytes, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
            Credentials credentials = ReadCredentials();
            using (HttpRequestMessage request = BuildRequest(HttpMethod.Get, key, null, credentials, _EmptySha256, null))
            using (HttpResponseMessage response = await _Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    throw new InvalidOperationException("archive read of '" + key + "' failed: HTTP " + (int)response.StatusCode + " " + ErrorCode(body));
                }
                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue && length.Value > maxBytes)
                    throw new InvalidOperationException("archive object '" + key + "' is " + length.Value + " bytes, above the " + maxBytes + " byte read limit");
                byte[] content = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (content.LongLength > maxBytes)
                    throw new InvalidOperationException("archive object '" + key + "' is above the " + maxBytes + " byte read limit");
                return content;
            }
        }

        /// <inheritdoc />
        public async Task<string?> DeleteAsync(string key, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
            Credentials credentials;
            try
            {
                credentials = ReadCredentials();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            using (HttpRequestMessage request = BuildRequest(HttpMethod.Delete, key, null, credentials, _EmptySha256, null))
            using (HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return null;
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return "archive delete of '" + key + "' failed: HTTP " + (int)response.StatusCode + " " + ErrorCode(body);
            }
        }

        #endregion

        #region Private-Methods

        private async Task<ArchivePutResult> PutAsync(string key, Func<HttpContent> contentFactory, long bytes, string sha256, string md5,
            string contentType, CancellationToken token)
        {
            Credentials credentials;
            try
            {
                credentials = ReadCredentials();
            }
            catch (InvalidOperationException ex)
            {
                return ArchivePutResult.Failed(key, "credentials_missing", ex.Message);
            }

            string? etag;
            try
            {
                Dictionary<string, string> extra = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { Sha256MetadataHeader, sha256 }
                };
                using (HttpRequestMessage request = BuildRequest(HttpMethod.Put, key, null, credentials, sha256, extra))
                {
                    HttpContent content = contentFactory();
                    content.Headers.ContentLength = bytes;
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                    content.Headers.ContentMD5 = Convert.FromBase64String(md5);
                    request.Content = content;
                    using (HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                            string code = ErrorCode(body);
                            string failure = code.Contains("SHA256", StringComparison.OrdinalIgnoreCase) ? "sha256_mismatch"
                                : code.Contains("BadDigest", StringComparison.OrdinalIgnoreCase) ? "md5_mismatch" : "upload_failed";
                            return ArchivePutResult.Failed(key, failure, "upload of '" + key + "' failed: HTTP " + (int)response.StatusCode + " " + code);
                        }
                        etag = response.Headers.ETag?.Tag?.Trim('"');
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                return ArchivePutResult.Failed(key, "upload_failed", "upload of '" + key + "' failed: " + ex.Message);
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                return ArchivePutResult.Failed(key, "upload_failed", "upload of '" + key + "' timed out: " + ex.Message);
            }

            // Read the stored object back: the store must hold exactly the bytes and the hash sent.
            ArchivePutResult verified = await HeadAndCompareAsync(key, bytes, sha256, credentials, token).ConfigureAwait(false);
            if (verified.Verified) verified.ETag = etag;
            return verified;
        }

        private async Task<ArchivePutResult> HeadAndCompareAsync(string key, long bytes, string sha256, Credentials credentials, CancellationToken token)
        {
            using (HttpRequestMessage head = BuildRequest(HttpMethod.Head, key, null, credentials, _EmptySha256, null))
            using (HttpResponseMessage response = await _Http.SendAsync(head, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return ArchivePutResult.Failed(key, "not_found", "no stored object '" + key + "'");
                if (!response.IsSuccessStatusCode)
                {
                    // A download/transaction cap returns 403 with a body that names the cap. That is a
                    // whole-store condition, not a fault of this object, so it is reported with its own
                    // code and the caller stops verifying the rest of the run. An auth or signature 403,
                    // which does not name a cap, stays a per-object verify failure.
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Forbidden && IsCapExceeded(body))
                        return ArchivePutResult.Failed(key, ArchivePutResult.CapExceededCode,
                            "stored object '" + key + "' read-back refused: the object store download or transaction cap is exceeded (HTTP 403 " + ErrorCode(body) + ")");
                    return ArchivePutResult.Failed(key, "verify_failed", "stored object '" + key + "' could not be read back: HTTP " + (int)response.StatusCode);
                }
                long stored = response.Content.Headers.ContentLength ?? -1;
                if (stored != bytes)
                    return ArchivePutResult.Failed(key, "size_mismatch", "stored object '" + key + "' is " + stored + " bytes, local content is " + bytes);
                string storedSha = response.Headers.TryGetValues(Sha256MetadataHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() ?? String.Empty : String.Empty;
                if (!String.Equals(storedSha, sha256, StringComparison.OrdinalIgnoreCase))
                    return ArchivePutResult.Failed(key, "sha256_mismatch", "stored object '" + key + "' records SHA-256 '" + storedSha + "', local content is " + sha256);
            }

            return new ArchivePutResult { Key = key, Verified = true, Bytes = bytes, Sha256 = sha256 };
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string? key, SortedDictionary<string, string>? query, Credentials credentials,
            string payloadSha256, Dictionary<string, string>? extraSignedHeaders)
        {
            if (!_Settings.IsConfigured) throw new InvalidOperationException("archive is not configured: endpoint, bucket and credentialsFile are required");
            Uri endpoint = new Uri(_Settings.Endpoint);
            string host;
            string canonicalPath;
            string encodedKey = key == null ? String.Empty : String.Join("/", key.Split('/').Select(segment => UriEncode(segment, false)));
            if (_Settings.ForcePathStyle)
            {
                host = endpoint.IsDefaultPort ? endpoint.Host : endpoint.Host + ":" + endpoint.Port;
                canonicalPath = "/" + UriEncode(_Settings.Bucket, false) + (key == null ? "" : "/" + encodedKey);
            }
            else
            {
                host = _Settings.Bucket + "." + (endpoint.IsDefaultPort ? endpoint.Host : endpoint.Host + ":" + endpoint.Port);
                canonicalPath = "/" + encodedKey;
            }
            string basePath = endpoint.AbsolutePath.TrimEnd('/');
            canonicalPath = basePath + canonicalPath;

            string canonicalQuery = query == null ? String.Empty
                : String.Join("&", query.Select(pair => UriEncode(pair.Key, true) + "=" + UriEncode(pair.Value, true)));

            DateTime now = _Clock();
            string amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            SortedDictionary<string, string> headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                { "host", host },
                { "x-amz-content-sha256", payloadSha256 },
                { "x-amz-date", amzDate }
            };
            if (extraSignedHeaders != null)
                foreach (KeyValuePair<string, string> pair in extraSignedHeaders) headers[pair.Key.ToLowerInvariant()] = pair.Value.Trim();

            string signedHeaders = String.Join(";", headers.Keys);
            string canonicalHeaders = String.Concat(headers.Select(pair => pair.Key + ":" + pair.Value + "\n"));
            string canonicalRequest = method.Method + "\n" + canonicalPath + "\n" + canonicalQuery + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadSha256;
            string scope = date + "/" + _Settings.Region + "/" + _Service + "/aws4_request";
            string stringToSign = _Algorithm + "\n" + amzDate + "\n" + scope + "\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

            byte[] signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), date), _Settings.Region), _Service), "aws4_request");
            string signature = Hex(Hmac(signingKey, stringToSign));

            UriBuilder builder = new UriBuilder(endpoint.Scheme, _Settings.ForcePathStyle ? endpoint.Host : _Settings.Bucket + "." + endpoint.Host, endpoint.Port);
            string uri = builder.Uri.GetLeftPart(UriPartial.Authority) + canonicalPath + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : "");
            HttpRequestMessage request = new HttpRequestMessage(method, uri);
            request.Headers.Host = host;
            foreach (KeyValuePair<string, string> pair in headers)
            {
                if (pair.Key == "host") continue;
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
            request.Headers.TryAddWithoutValidation("Authorization",
                _Algorithm + " Credential=" + credentials.AccessKeyId + "/" + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature);
            return request;
        }

        private Credentials ReadCredentials()
        {
            string path = _Settings.CredentialsFile;
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("archive credentials file '" + path + "' does not exist");
            string? id = null;
            string? secret = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string name = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (String.Equals(name, "accessKeyId", StringComparison.OrdinalIgnoreCase)) id = value;
                else if (String.Equals(name, "secretAccessKey", StringComparison.OrdinalIgnoreCase)) secret = value;
            }
            if (String.IsNullOrEmpty(id) || String.IsNullOrEmpty(secret))
                throw new InvalidOperationException("archive credentials file '" + path + "' must hold accessKeyId and secretAccessKey");
            return new Credentials(id, secret);
        }

        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private static string ErrorCode(string body)
        {
            if (String.IsNullOrWhiteSpace(body)) return String.Empty;
            int start = body.IndexOf("<Code>", StringComparison.Ordinal);
            int end = body.IndexOf("</Code>", StringComparison.Ordinal);
            return start >= 0 && end > start ? body.Substring(start + 6, end - start - 6) : String.Empty;
        }

        // True when a 403 body names a download or transaction cap (the error code or message), rather
        // than an auth, permission or signature refusal. The store spells the condition several ways, so
        // the match is on the cap vocabulary common to them.
        internal static bool IsCapExceeded(string body)
        {
            if (String.IsNullOrWhiteSpace(body)) return false;
            string lower = body.ToLowerInvariant();
            return lower.Contains("cap exceeded")
                || lower.Contains("cap_exceeded")
                || lower.Contains("capexceeded")
                || lower.Contains("cap is exceeded")
                || lower.Contains("transaction cap")
                || lower.Contains("transaction_cap")
                || lower.Contains("transactioncap")
                || lower.Contains("download cap")
                || lower.Contains("storage cap")
                || lower.Contains("usage cap");
        }

        private static byte[] Hmac(byte[] key, string data)
        {
            return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        }

        private static string Hex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // RFC 3986 unreserved characters pass; everything else is percent-encoded, as Signature
        // Version 4 requires. A path keeps its slashes because the caller encodes per segment.
        private static string UriEncode(string value, bool encodeSlash)
        {
            StringBuilder builder = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(value))
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                    builder.Append(c);
                else if (c == '/' && !encodeSlash)
                    builder.Append(c);
                else
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private sealed record Credentials(string AccessKeyId, string SecretAccessKey);

        #endregion
    }
}
