namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// An ADVISORY flag attached to a dock-boundary scan result by a second pass that runs behind the
    /// deterministic scanner. A flag asks a person to look at one added hunk; it is never a finding,
    /// never changes <see cref="DockBoundaryScanResult.Passed"/>, and never fails a merge entry or a
    /// mission. A clean deterministic scan carrying flags still lands.
    ///
    /// The flag names the file, the suspected class, and the confidence. It never carries the hunk
    /// text or any matched value.
    /// </summary>
    public sealed class DockBoundaryAdvisoryFlag
    {
        #region Public-Members

        /// <summary>
        /// Stable code of the advisory pass that raised this flag.
        /// </summary>
        public string Code
        {
            get => _Code;
            set => _Code = value ?? "";
        }

        /// <summary>
        /// Repository-relative path of the file whose added hunk was flagged, or null when the flag
        /// applies to the whole diff.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// The suspected class of private context, for example <c>operator_note</c>,
        /// <c>customer_detail</c>, <c>orchestration_id</c>, or <c>host_or_path</c>. Advisory only.
        /// </summary>
        public string Kind
        {
            get => _Kind;
            set => _Kind = value ?? "";
        }

        /// <summary>
        /// The confidence the advisory pass reported, in [0, 1].
        /// </summary>
        public double Confidence
        {
            get => _Confidence;
            set => _Confidence = Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// Human-readable message naming the file and the suspected class. Never contains the hunk
        /// text or any matched value.
        /// </summary>
        public string Message
        {
            get => _Message;
            set => _Message = value ?? "";
        }

        #endregion

        #region Private-Members

        private string _Code = "";
        private string _Kind = "";
        private string _Message = "";
        private double _Confidence;

        #endregion
    }
}
