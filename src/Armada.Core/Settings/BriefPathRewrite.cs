namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// One path prefix rewrite applied to operator-written text in a captain brief. A path written as the
    /// admiral host sees it (a host volume) is rewritten to the path a captain resolves (the container mount),
    /// so a brief never names a path the captain cannot open.
    /// </summary>
    public class BriefPathRewrite
    {
        #region Public-Members

        /// <summary>
        /// Absolute path prefix as written in objectives and mission descriptions. Trailing separators are
        /// ignored; a blank value disables the rewrite.
        /// </summary>
        public string From
        {
            get => _From;
            set => _From = (value ?? String.Empty).Trim().TrimEnd('/', '\\');
        }

        /// <summary>
        /// Absolute path prefix a captain resolves for the same location. Trailing separators are ignored.
        /// </summary>
        public string To
        {
            get => _To;
            set => _To = (value ?? String.Empty).Trim().TrimEnd('/', '\\');
        }

        #endregion

        #region Private-Members

        private string _From = String.Empty;
        private string _To = String.Empty;

        #endregion
    }
}
