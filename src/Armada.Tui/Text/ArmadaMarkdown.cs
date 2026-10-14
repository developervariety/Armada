namespace Armada.Tui.Text
{
    using System;
    using System.Collections.Generic;
    using Armada.Tui.Theming;
    using TUIKit;
    using TUIKit.Content;

    /// <summary>
    /// Renders Markdown for the TUI with TUIKit's width-aware engine: the block is laid out at the width it is shown
    /// at (tables sized from content, nested lists and quotes keep their indent), colored from the Armada theme, and
    /// drawn with ASCII glyphs when the theme asks for them. Thread-safe (stateless).
    /// </summary>
    public static class ArmadaMarkdown
    {
        #region Public-Methods

        /// <summary>
        /// Render Markdown laid out at a width. Empty or whitespace input renders no lines.
        /// </summary>
        /// <param name="markdown">Markdown source, or null.</param>
        /// <param name="width">Available width in columns (values below 1 are treated as 1).</param>
        /// <param name="theme">Palette.</param>
        /// <returns>Rendered rows, none wider than the width.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
        public static IReadOnlyList<StyledText> Render(string? markdown, int width, ArmadaTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (String.IsNullOrWhiteSpace(markdown)) return new List<StyledText>();
            return MarkdownRenderer.Render(markdown!, Math.Max(1, width), Options(theme));
        }

        /// <summary>
        /// The part of a reply that is still streaming that is safe to lay out: a trailing table row that has not
        /// received its newline is held back (as TUIKit's <c>StreamingTranscript</c> does), so a half-received row
        /// does not show as a short cell and resize the columns on every chunk.
        /// </summary>
        /// <param name="markdown">Markdown received so far, or null.</param>
        /// <returns>The text to render.</returns>
        public static string Streaming(string? markdown)
        {
            if (String.IsNullOrEmpty(markdown)) return "";
            int newline = markdown!.LastIndexOf('\n');
            string tail = markdown.Substring(newline + 1);
            if (!tail.TrimStart().StartsWith("|", StringComparison.Ordinal)) return markdown;
            return newline < 0 ? "" : markdown.Substring(0, newline + 1);
        }

        /// <summary>
        /// Render options for a theme: styles from its palette and the ASCII preset when it uses ASCII glyphs or borders.
        /// </summary>
        /// <param name="theme">Palette.</param>
        /// <returns>New options.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
        public static MarkdownRenderOptions Options(ArmadaTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            MarkdownRenderOptions options = theme.AsciiGlyphs || theme.AsciiBorders ? MarkdownRenderOptions.Ascii() : new MarkdownRenderOptions();
            options.Styles = Styles(theme);
            return options;
        }

        #endregion

        #region Private-Methods

        private static MarkdownStyles Styles(ArmadaTheme theme)
        {
            // TUIKit's MarkdownStyles.FromTheme mapping, with foreground and attributes only, so the view's own
            // background (panel, dialog, cursor row) shows through.
            MarkdownStyles styles = MarkdownStyles.Default;
            styles.Text = Fg(theme.Text);
            styles.Heading1 = Fg(theme.Accent).WithAttribute(CellAttributes.Bold, true);
            styles.Heading = Fg(theme.Info).WithAttribute(CellAttributes.Bold, true);
            styles.CodeBlock = Fg(theme.Success);
            styles.InlineCode = Fg(theme.Warning);
            styles.Bullet = Fg(theme.Accent);
            styles.Quote = Fg(theme.Muted);
            styles.Rule = Fg(theme.Border);
            styles.TaskOpen = Fg(theme.Accent);
            styles.TaskDone = Fg(theme.Success);
            styles.Link = Fg(theme.Link).WithAttribute(CellAttributes.Underline, true);
            styles.LinkUrl = Fg(theme.Muted);
            styles.CodeLanguage = Fg(theme.Muted);
            styles.TableHeader = Fg(theme.Accent).WithAttribute(CellAttributes.Bold, true);
            styles.TableBorder = Fg(theme.Border);
            return styles;
        }

        private static CellStyle Fg(CellStyle style)
        {
            return new CellStyle(style.Foreground, Color.Default, style.Attributes);
        }

        #endregion
    }
}
