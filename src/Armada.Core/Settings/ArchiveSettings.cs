namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings for the history archive: an S3-compatible bucket that receives raw history before
    /// Armada frees it locally, and the compact trace records that outlive the raw copy. History is
    /// never deleted without a verified copy unless <see cref="KeepHistoryWithoutArchive"/> is turned
    /// off. The section hot-reloads; the sink is built from the current values at each use.
    /// </summary>
    public class ArchiveSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether history is uploaded to the bucket before it is freed locally. Defaults to false.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Base URL of the S3-compatible endpoint, for example <c>https://s3.example.com</c>.
        /// </summary>
        public string Endpoint
        {
            get => _Endpoint;
            set => _Endpoint = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim().TrimEnd('/');
        }

        /// <summary>
        /// Signing region of the endpoint. Defaults to <c>us-east-1</c>.
        /// </summary>
        public string Region
        {
            get => _Region;
            set => _Region = String.IsNullOrWhiteSpace(value) ? "us-east-1" : value.Trim();
        }

        /// <summary>
        /// Bucket that holds the archive.
        /// </summary>
        public string Bucket
        {
            get => _Bucket;
            set => _Bucket = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        /// <summary>
        /// Address the bucket as a path segment (<c>endpoint/bucket/key</c>) rather than as a host
        /// name. Most S3-compatible services accept path style. Defaults to true.
        /// </summary>
        public bool ForcePathStyle { get; set; } = true;

        /// <summary>
        /// Absolute path of a file holding the access key, one <c>name=value</c> per line:
        /// <c>accessKeyId=...</c> and <c>secretAccessKey=...</c>. The file is read at each use and its
        /// content is never logged; the key never belongs in the settings file.
        /// </summary>
        public string CredentialsFile
        {
            get => _CredentialsFile;
            set => _CredentialsFile = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        /// <summary>
        /// Days a raw history object is kept in the bucket before Armada deletes it. Clamped to
        /// 1-3650. Defaults to 90.
        /// </summary>
        public int RawRetentionDays
        {
            get => _RawRetentionDays;
            set => _RawRetentionDays = Math.Max(1, Math.Min(3650, value));
        }

        /// <summary>
        /// Days a trace record is kept in the bucket and in the local trace table. Clamped to
        /// 1-3650. Defaults to 365.
        /// </summary>
        public int TraceRetentionDays
        {
            get => _TraceRetentionDays;
            set => _TraceRetentionDays = Math.Max(1, Math.Min(3650, value));
        }

        /// <summary>
        /// Prefix in front of every object key, so one bucket can hold several installations.
        /// Defaults to <c>armada</c>; an empty value writes keys at the bucket root.
        /// </summary>
        public string KeyPrefix
        {
            get => _KeyPrefix;
            set => _KeyPrefix = value == null ? String.Empty : value.Trim().Trim('/');
        }

        /// <summary>
        /// When the archive is disabled or not configured, keep every history item that is past its
        /// retention instead of deleting it. Defaults to true, so history is never lost by default.
        /// Turn it off only on an installation that wants history deleted without a copy.
        /// </summary>
        public bool KeepHistoryWithoutArchive { get; set; } = true;

        /// <summary>
        /// Folders of captain session transcripts that are archived, then freed, once their files are
        /// older than the root's retention. Empty by default. Credentials inside them are never
        /// uploaded (see <see cref="DenyPatterns"/>).
        /// </summary>
        public List<ArchiveSessionRoot> SessionRoots
        {
            get => _SessionRoots;
            set => _SessionRoots = value ?? new List<ArchiveSessionRoot>();
        }

        /// <summary>
        /// Extra file-name patterns that are never uploaded, in addition to the built-in deny list
        /// (credential, secret and settings files). <c>*</c> matches any run of characters.
        /// </summary>
        public List<string> DenyPatterns
        {
            get => _DenyPatterns;
            set => _DenyPatterns = value ?? new List<string>();
        }

        /// <summary>
        /// True when every value the sink needs is present.
        /// </summary>
        public bool IsConfigured =>
            !String.IsNullOrEmpty(Endpoint) && !String.IsNullOrEmpty(Bucket) && !String.IsNullOrEmpty(CredentialsFile);

        #endregion

        #region Private-Members

        private string _Endpoint = String.Empty;
        private string _Region = "us-east-1";
        private string _Bucket = String.Empty;
        private string _CredentialsFile = String.Empty;
        private int _RawRetentionDays = 90;
        private int _TraceRetentionDays = 365;
        private string _KeyPrefix = "armada";
        private List<ArchiveSessionRoot> _SessionRoots = new List<ArchiveSessionRoot>();
        private List<string> _DenyPatterns = new List<string>();

        #endregion
    }
}
