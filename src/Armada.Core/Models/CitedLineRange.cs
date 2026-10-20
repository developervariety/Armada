namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A file and line range cited in mission or objective text, such as <c>Services/Foo.cs:12-20</c>,
    /// <c>Foo.cs:12</c> or <c>../Sibling/Foo.cs:12</c>, with the symbol the text names for it: written as
    /// <c>`Symbol` (`path:line`)</c> or <c>`Symbol` at `path:line`</c>, or the one backticked identifier or
    /// call expression in a sentence that cites nothing else.
    /// </summary>
    public class CitedLineRange
    {
        #region Public-Members

        /// <summary>
        /// The path as the text spells it.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// The first cited line, 1-based.
        /// </summary>
        public int StartLine { get; set; } = 1;

        /// <summary>
        /// The last cited line, 1-based; equal to <see cref="StartLine"/> for a single-line citation.
        /// </summary>
        public int EndLine { get; set; } = 1;

        /// <summary>
        /// The symbol the text pairs with the citation, or null when it names none.
        /// </summary>
        public string? Symbol { get; set; } = null;

        /// <summary>
        /// The citation as written, for example <c>Services/Foo.cs:12-20</c>.
        /// </summary>
        public string Text
        {
            get
            {
                return StartLine == EndLine ? Path + ":" + StartLine : Path + ":" + StartLine + "-" + EndLine;
            }
        }

        #endregion
    }
}
