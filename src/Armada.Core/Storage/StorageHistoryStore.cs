namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    /// <summary>
    /// Keeps the bounded growth history in one JSON file so it survives a restart without a database table.
    /// Writes replace the file atomically; a file that cannot be read restarts the history empty and says so.
    /// </summary>
    public class StorageHistoryStore
    {
        #region Public-Members

        /// <summary>File name of the history file inside the data directory.</summary>
        public const string FileName = "storage-history.json";

        /// <summary>Full path of the history file.</summary>
        public string Path { get; }

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions { WriteIndented = false };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="path">Full path of the history file.</param>
        public StorageHistoryStore(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            Path = path;
        }

        #endregion

        #region Public-Methods

        /// <summary>Read the stored history, oldest first.</summary>
        /// <param name="readable">False when the file exists but could not be read; the returned list is then empty.</param>
        /// <returns>The stored samples.</returns>
        public List<StorageHistoryPoint> Load(out bool readable)
        {
            readable = true;
            if (!File.Exists(Path)) return new List<StorageHistoryPoint>();
            try
            {
                List<StorageHistoryPoint>? points = JsonSerializer.Deserialize<List<StorageHistoryPoint>>(File.ReadAllText(Path), _JsonOptions);
                if (points == null) throw new JsonException("The history file holds no list.");
                return points.OrderBy(p => p.MeasuredUtc).ToList();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                readable = false;
                return new List<StorageHistoryPoint>();
            }
        }

        /// <summary>Append a sample, drop the oldest samples beyond the capacity and write the file.</summary>
        /// <param name="history">Current history, oldest first.</param>
        /// <param name="point">Sample to append.</param>
        /// <param name="capacity">Maximum samples kept.</param>
        /// <param name="saved">False when the file could not be written; the returned list is still current.</param>
        /// <returns>The bounded history including the new sample.</returns>
        public List<StorageHistoryPoint> Append(List<StorageHistoryPoint> history, StorageHistoryPoint point, int capacity, out bool saved)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            if (point == null) throw new ArgumentNullException(nameof(point));
            List<StorageHistoryPoint> next = new List<StorageHistoryPoint>(history) { point };
            if (next.Count > capacity) next = next.GetRange(next.Count - capacity, capacity);
            saved = true;
            string temporary = Path + ".tmp";
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, _JsonOptions));
                File.Move(temporary, Path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                saved = false;
                try { File.Delete(temporary); }
                catch (IOException) { /* The leftover temporary file is replaced by the next write. */ }
                catch (UnauthorizedAccessException) { /* The leftover temporary file is replaced by the next write. */ }
            }
            return next;
        }

        #endregion
    }
}
