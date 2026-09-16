namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;

    /// <summary>
    /// Resolves the typed-decision provider key without ever storing it in settings. The environment variable named
    /// by <see cref="TypedDecisionSettings.ApiKeyEnv"/> wins; otherwise the key file under the data directory's
    /// <c>secrets</c> folder is read. The key value is never logged, recorded, or returned by this class to any
    /// caller other than the client that sends it.
    /// </summary>
    public sealed class TypedDecisionKeyStore
    {
        #region Public-Members

        /// <summary>The key came from the environment variable.</summary>
        public const string SourceEnvironment = "env";

        /// <summary>The key came from the key file.</summary>
        public const string SourceFile = "file";

        /// <summary>The reason the effective global mode is Off when no key resolves.</summary>
        public const string ReasonNoKey = "typed_decisions_no_key";

        /// <summary>The key file name inside the secrets folder.</summary>
        public const string KeyFileName = "typesafe-api-key";

        /// <summary>The longest key accepted.</summary>
        public const int MaxKeyLength = 4096;

        /// <summary>The absolute key file path.</summary>
        public string KeyFilePath { get; }

        #endregion

        #region Private-Members

        private static readonly TimeSpan _FileCheckInterval = TimeSpan.FromSeconds(15);
        private readonly Func<string, string?> _ReadEnvironment;
        private readonly object _Lock = new object();
        private bool _FileCached;
        private string? _FileKey;
        private DateTime _FileCheckedUtc = DateTime.MinValue;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a key store for a data directory.</summary>
        /// <param name="dataDirectory">The Admiral data directory; the key file path is derived from it.</param>
        /// <param name="readEnvironment">Reads an environment variable; null uses the process environment.</param>
        public TypedDecisionKeyStore(string dataDirectory, Func<string, string?>? readEnvironment = null)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException(nameof(dataDirectory));
            KeyFilePath = KeyFilePathFor(dataDirectory);
            _ReadEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        }

        #endregion

        #region Public-Methods

        /// <summary>The key file path for a data directory.</summary>
        /// <param name="dataDirectory">The Admiral data directory.</param>
        /// <returns>The absolute key file path.</returns>
        public static string KeyFilePathFor(string dataDirectory)
        {
            return Path.GetFullPath(Path.Combine(dataDirectory, "secrets", KeyFileName));
        }

        /// <summary>
        /// Resolve the key and its source. Returns null with a null source when no key is available.
        /// </summary>
        /// <param name="settings">Typed-decision settings, for the environment variable name.</param>
        /// <param name="source"><see cref="SourceEnvironment"/>, <see cref="SourceFile"/>, or null.</param>
        /// <returns>The key, or null.</returns>
        public string? ResolveKey(TypedDecisionSettings settings, out string? source)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string? fromEnvironment = EnvironmentKey(settings);
            if (fromEnvironment != null)
            {
                source = SourceEnvironment;
                return fromEnvironment;
            }
            string? fromFile = FileKey();
            source = fromFile != null ? SourceFile : null;
            return fromFile;
        }

        /// <summary>Whether a key resolves, and from where.</summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="source">The key source, or null.</param>
        /// <returns>True when a key is available.</returns>
        public bool HasKey(TypedDecisionSettings settings, out string? source)
        {
            return ResolveKey(settings, out source) != null;
        }

        /// <summary>Whether the environment variable supplies a key.</summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <returns>True when the variable is set and not blank.</returns>
        public bool EnvironmentSuppliesKey(TypedDecisionSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return EnvironmentKey(settings) != null;
        }

        /// <summary>
        /// Write the key file: the secrets folder is created with owner-only access (0700) and the file is written
        /// owner read-write (0600) through a temporary file and an atomic replace.
        /// </summary>
        /// <param name="apiKey">The key; surrounding whitespace is removed.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the file is in place.</returns>
        /// <exception cref="ArgumentException">The key is empty, too long, or contains control characters.</exception>
        public async Task WriteKeyAsync(string? apiKey, CancellationToken token = default)
        {
            string key = (apiKey ?? String.Empty).Trim();
            if (key.Length == 0 || key.Length > MaxKeyLength) throw new ArgumentException("The API key must be 1 to " + MaxKeyLength + " characters.");
            foreach (char c in key)
                if (Char.IsControl(c)) throw new ArgumentException("The API key must not contain control characters.");

            string directory = Path.GetDirectoryName(KeyFilePath)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string temporary = Path.Combine(directory, "." + KeyFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            FileStreamOptions options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (FileStream stream = new FileStream(temporary, options))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(key);
                    await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                File.Move(temporary, KeyFilePath, true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            Invalidate();
        }

        /// <summary>Remove the key file.</summary>
        /// <returns>True when a file was removed.</returns>
        public bool DeleteKeyFile()
        {
            bool existed = File.Exists(KeyFilePath);
            if (existed) File.Delete(KeyFilePath);
            Invalidate();
            return existed;
        }

        /// <summary>Discard the cached file read, so the next resolution reads the file again.</summary>
        public void Invalidate()
        {
            lock (_Lock)
            {
                _FileCached = false;
                _FileKey = null;
            }
        }

        #endregion

        #region Private-Methods

        private string? EnvironmentKey(TypedDecisionSettings settings)
        {
            if (String.IsNullOrWhiteSpace(settings.ApiKeyEnv)) return null;
            string? value = _ReadEnvironment(settings.ApiKeyEnv);
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private string? FileKey()
        {
            lock (_Lock)
            {
                // A file changed outside the API is picked up within the check interval; API writes invalidate at once.
                if (_FileCached && DateTime.UtcNow - _FileCheckedUtc < _FileCheckInterval) return _FileKey;
                string? key = null;
                try
                {
                    if (File.Exists(KeyFilePath))
                    {
                        FileInfo info = new FileInfo(KeyFilePath);
                        if (info.Length > 0 && info.Length <= MaxKeyLength * 4)
                        {
                            string text = File.ReadAllText(KeyFilePath).Trim();
                            if (text.Length > 0) key = text;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // An unreadable key file is no key: the effective mode reports Off with the no-key reason.
                    key = null;
                }
                _FileKey = key;
                _FileCached = true;
                _FileCheckedUtc = DateTime.UtcNow;
                return key;
            }
        }

        #endregion
    }
}
