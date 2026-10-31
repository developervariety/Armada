namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Reads a model endpoint's API key from its server-only credential file when the endpoint is mapped in
    /// <see cref="ModelEndpointCredentialSettings"/>. The key lives only in an in-memory copy of the endpoint used to
    /// build one client; the endpoint record, tools and logs never carry it. Every refusal names its reason and never
    /// includes file content.
    /// </summary>
    public class ModelEndpointCredentialResolver
    {
        #region Public-Members

        /// <summary>
        /// Largest credential file accepted, in bytes. An API key is far smaller; a larger file is not a key file.
        /// </summary>
        public const int MaximumFileBytes = 8192;

        #endregion

        #region Private-Members

        private readonly ModelEndpointCredentialSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Credential file settings.</param>
        public ModelEndpointCredentialResolver(ModelEndpointCredentialSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The endpoint to build a client from. An unmapped endpoint is returned unchanged. A mapped endpoint is
        /// returned as an in-memory copy carrying the key read from its file.
        /// </summary>
        /// <param name="endpoint">Endpoint.</param>
        /// <returns>The endpoint, or a copy carrying the file key.</returns>
        /// <exception cref="ModelEndpointCredentialException">The endpoint is mapped and its file cannot be used.</exception>
        public ModelEndpoint Resolve(ModelEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (!_Settings.Files.TryGetValue(endpoint.Id, out string? fileName)) return endpoint;

            // One source per endpoint: a stored key beside a mapped file would leave which key is billed unclear.
            if (!String.IsNullOrWhiteSpace(endpoint.ApiKey))
                throw Refuse(endpoint, "it has both a stored API key and a credential file; remove the stored key");

            string path = ResolvePath(endpoint, fileName);
            return endpoint.CopyWithApiKey(ReadKey(endpoint, path));
        }

        #endregion

        #region Private-Methods

        private string ResolvePath(ModelEndpoint endpoint, string? fileName)
        {
            string? directory = _Settings.Directory;
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw Refuse(endpoint, "the credential directory is not set to an absolute path");

            if (String.IsNullOrWhiteSpace(fileName)
                || fileName == "." || fileName == ".."
                || fileName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                throw Refuse(endpoint, "its credential file name is not a single file name");

            if (OperatingSystem.IsWindows())
                throw Refuse(endpoint, "credential file permissions can be checked only on a Unix host");

            DirectoryInfo dir = new DirectoryInfo(directory);
            if (!dir.Exists) throw Refuse(endpoint, "the credential directory does not exist");
            if (dir.LinkTarget != null) throw Refuse(endpoint, "the credential directory is a symbolic link");

            return Path.Combine(dir.FullName, fileName);
        }

        private static string ReadKey(ModelEndpoint endpoint, string path)
        {
            FileInfo file = new FileInfo(path);
            if (file.LinkTarget != null) throw Refuse(endpoint, "its credential file is a symbolic link");
            if (!file.Exists) throw Refuse(endpoint, "its credential file does not exist");

            // Owner read only (0400). A file the process can read with no group or other bits set belongs to the
            // process user, so this also proves ownership without a platform call.
            UnixFileMode mode = File.GetUnixFileMode(path);
            if (mode != UnixFileMode.UserRead)
                throw Refuse(endpoint, "its credential file mode must be 0400 (owner read only)");

            if (file.Length > MaximumFileBytes) throw Refuse(endpoint, "its credential file is larger than " + MaximumFileBytes + " bytes");

            string key;
            try
            {
                key = File.ReadAllText(path, Encoding.UTF8).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw Refuse(endpoint, "its credential file cannot be read (" + ex.GetType().Name + ")");
            }

            if (key.Length == 0) throw Refuse(endpoint, "its credential file is empty");
            if (key.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw Refuse(endpoint, "its credential file holds more than one line");
            return key;
        }

        private static ModelEndpointCredentialException Refuse(ModelEndpoint endpoint, string reason)
        {
            return new ModelEndpointCredentialException(endpoint.Id, reason);
        }

        #endregion
    }

    /// <summary>
    /// A mapped endpoint's credential file cannot be used. The message names the endpoint and the reason only.
    /// </summary>
    public class ModelEndpointCredentialException : InvalidOperationException
    {
        /// <summary>
        /// Endpoint id.
        /// </summary>
        public string EndpointId { get; }

        /// <summary>
        /// Why the file cannot be used.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="endpointId">Endpoint id.</param>
        /// <param name="reason">Why the file cannot be used.</param>
        public ModelEndpointCredentialException(string endpointId, string reason)
            : base("Model endpoint " + endpointId + " cannot use its credential file: " + reason + ".")
        {
            EndpointId = endpointId;
            Reason = reason;
        }
    }
}
