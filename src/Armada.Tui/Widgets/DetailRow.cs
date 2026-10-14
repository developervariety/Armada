namespace Armada.Tui.Widgets
{
    using System;
    using Armada.Tui.Theming;
    using TUIKit;

    /// <summary>
    /// One row of a <see cref="DetailView"/>.
    /// </summary>
    public class DetailRow
    {
        #region Public-Members

        /// <summary>
        /// English label or section title.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Value, or null.
        /// </summary>
        public string? Value { get; }

        /// <summary>
        /// Value style selector, or null.
        /// </summary>
        public Func<ArmadaTheme, CellStyle>? Style { get; }

        /// <summary>
        /// True for a section heading.
        /// </summary>
        public bool IsSection { get; }

        /// <summary>
        /// True when the value is Markdown, laid out at the value column's width.
        /// </summary>
        public bool IsMarkdown { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="label">Label.</param>
        /// <param name="value">Value.</param>
        /// <param name="style">Style selector.</param>
        /// <param name="isSection">Section heading.</param>
        public DetailRow(string label, string? value, Func<ArmadaTheme, CellStyle>? style, bool isSection)
            : this(label, value, style, isSection, false)
        {
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="label">Label.</param>
        /// <param name="value">Value.</param>
        /// <param name="style">Style selector.</param>
        /// <param name="isSection">Section heading.</param>
        /// <param name="isMarkdown">The value is Markdown.</param>
        public DetailRow(string label, string? value, Func<ArmadaTheme, CellStyle>? style, bool isSection, bool isMarkdown)
        {
            Label = label ?? "";
            Value = value;
            Style = style;
            IsSection = isSection;
            IsMarkdown = isMarkdown;
        }

        #endregion
    }
}
