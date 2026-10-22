namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// One folder of captain session transcripts the archive keeps bounded. Every file under it that
    /// matches <see cref="Pattern"/> and was not written for <see cref="RetentionDays"/> is uploaded,
    /// verified, and then removed locally. Files on the deny list are never uploaded or removed.
    /// </summary>
    public class ArchiveSessionRoot
    {
        #region Public-Members

        /// <summary>
        /// Absolute path of the folder. Empty or relative paths are skipped.
        /// </summary>
        public string Path
        {
            get => _Path;
            set => _Path = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        /// <summary>
        /// Name used for the folder in object keys, for example <c>codex-1</c>. Defaults to the
        /// folder's own name.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => _Name = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        /// <summary>
        /// File-name pattern of transcript files, searched in every subfolder. Defaults to <c>*</c>.
        /// </summary>
        public string Pattern
        {
            get => _Pattern;
            set => _Pattern = String.IsNullOrWhiteSpace(value) ? "*" : value.Trim();
        }

        /// <summary>
        /// Days a file must sit unwritten before it is archived and freed. Clamped to 1-3650.
        /// Defaults to 30.
        /// </summary>
        public int RetentionDays
        {
            get => _RetentionDays;
            set => _RetentionDays = Math.Max(1, Math.Min(3650, value));
        }

        #endregion

        #region Private-Members

        private string _Path = String.Empty;
        private string _Name = String.Empty;
        private string _Pattern = "*";
        private int _RetentionDays = 30;

        #endregion
    }
}
