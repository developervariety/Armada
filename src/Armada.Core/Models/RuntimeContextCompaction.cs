namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One context-compaction pass made by a runtime that holds its conversation in process. It records what the
    /// pass removed, so the cost of compaction is visible instead of silent.
    /// </summary>
    public class RuntimeContextCompaction
    {
        #region Public-Members

        /// <summary>
        /// Runtime that compacted its conversation.
        /// </summary>
        public string Runtime { get; set; } = "";

        /// <summary>
        /// Model the conversation was held for.
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// True for the ceiling pass, which also truncates diagnostic and test-total lines; false for the ordinary
        /// pass, which keeps them.
        /// </summary>
        public bool Forced { get; set; } = false;

        /// <summary>
        /// Number of tool results whose content this pass replaced with a bounded head.
        /// </summary>
        public int CompactedResults
        {
            get
            {
                return _CompactedResults;
            }
            set
            {
                _CompactedResults = Math.Max(0, value);
            }
        }

        /// <summary>
        /// Serialized conversation size, in UTF-8 bytes, before this pass.
        /// </summary>
        public long BytesBefore
        {
            get
            {
                return _BytesBefore;
            }
            set
            {
                _BytesBefore = Math.Max(0, value);
            }
        }

        /// <summary>
        /// Serialized conversation size, in UTF-8 bytes, after this pass.
        /// </summary>
        public long BytesAfter
        {
            get
            {
                return _BytesAfter;
            }
            set
            {
                _BytesAfter = Math.Max(0, value);
            }
        }

        /// <summary>
        /// UTF-8 bytes of tool-result content this pass removed, net of the kept heads and markers.
        /// </summary>
        public long DroppedBytes
        {
            get
            {
                return _DroppedBytes;
            }
            set
            {
                _DroppedBytes = Math.Max(0, value);
            }
        }

        /// <summary>
        /// Removed tool-result bytes per tool name. A result with no tool name is counted under <c>tool</c>.
        /// </summary>
        public Dictionary<string, long> DroppedBytesByTool
        {
            get
            {
                return _DroppedBytesByTool;
            }
            set
            {
                _DroppedBytesByTool = value ?? new Dictionary<string, long>(StringComparer.Ordinal);
            }
        }

        #endregion

        #region Private-Members

        private int _CompactedResults = 0;
        private long _BytesBefore = 0;
        private long _BytesAfter = 0;
        private long _DroppedBytes = 0;
        private Dictionary<string, long> _DroppedBytesByTool = new Dictionary<string, long>(StringComparer.Ordinal);

        #endregion
    }
}
