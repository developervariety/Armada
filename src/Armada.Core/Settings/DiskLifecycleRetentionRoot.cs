namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// One operator directory the disk-lifecycle reconciler keeps bounded, such as a folder of
    /// deploy database dumps or rehearsal scratch folders. Each direct child (file or folder) is
    /// one item: it is reclaimable when nothing inside it was written for <see cref="RetentionHours"/>
    /// and it is not among the <see cref="KeepNewest"/> most recently written children. A child that
    /// holds an <c>.armada-keep</c> file, or is a symlink, is never touched.
    /// </summary>
    public class DiskLifecycleRetentionRoot
    {
        #region Public-Members

        /// <summary>
        /// Absolute path of the directory whose direct children are kept bounded. The directory
        /// itself is never removed. Empty or relative paths are skipped.
        /// </summary>
        public string Path
        {
            get => _Path;
            set => _Path = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        /// <summary>
        /// Hours a child must sit with no write anywhere inside it before it is reclaimable.
        /// Clamped to 1-8760. Defaults to 72.
        /// </summary>
        public int RetentionHours
        {
            get => _RetentionHours;
            set => _RetentionHours = Math.Max(1, Math.Min(8760, value));
        }

        /// <summary>
        /// Number of most recently written children always kept, whatever their age.
        /// Clamped to 0-1000. Defaults to 3.
        /// </summary>
        public int KeepNewest
        {
            get => _KeepNewest;
            set => _KeepNewest = Math.Max(0, Math.Min(1000, value));
        }

        #endregion

        #region Private-Members

        private string _Path = String.Empty;
        private int _RetentionHours = 72;
        private int _KeepNewest = 3;

        #endregion
    }
}
