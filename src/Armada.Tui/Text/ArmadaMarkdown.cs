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
        /// The part of a reply that is still streaming that is safe to lay out (TUIKit's
        /// <see cref="MarkdownRenderer.StreamingPrefix"/>): a table row that has not received its newline, and a table
        /// header that has not received its delimiter row, are held back, so a table never shows short cells or
        /// flashes as text while it arrives.
        /// </summary>
        /// <param name="markdown">Markdown received so far, or null.</param>
        /// <returns>The text to render.</returns>
        public static string Streaming(string? markdown)
        {
            if (String.IsNullOrEmpty(markdown)) return "";
            return MarkdownRenderer.StreamingPrefix(markdown!);
        }

        /// <summary>
        /// Render options for a theme: TUIKit's theme-derived styles with foreground colors only (so the view's own
        /// background, a panel, a dialog, or a cursor row, shows through), the ASCII glyphs when the theme uses ASCII
        /// glyphs or borders, and links in the Armada link color.
        /// </summary>
        /// <param name="theme">Palette.</param>
        /// <returns>New options.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
        public static MarkdownRenderOptions Options(ArmadaTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            MarkdownRenderOptions options = new MarkdownRenderOptions().WithTheme(ThemeService.BuildTuiKitTheme(theme));
            options.Styles.Link = new CellStyle(theme.Link.Foreground, Color.Default, theme.Link.Attributes).WithAttribute(CellAttributes.Underline, true);
            return options;
        }

        #endregion
    }
}
