namespace Armada.Tui.Text
{
    using System;
    using System.Collections.Generic;
    using Armada.Tui.Theming;
    using TUIKit;
    using TUIKit.Content;

    /// <summary>
    /// Caches Markdown laid out for the TUI, for views that redraw many Markdown blocks on every frame (chat and
    /// planning transcripts, detail rows). Wraps TUIKit's <see cref="MarkdownLayoutCache"/> (least-recently-used,
    /// one width at a time) and keeps one render options instance per theme, so a theme switch or an ASCII toggle
    /// lays the text out again. Not thread-safe (the options memo belongs to the UI thread).
    /// </summary>
    public class ArmadaMarkdownCache
    {
        #region Public-Members

        /// <summary>
        /// Number of laid-out blocks currently cached.
        /// </summary>
        public int Count
        {
            get { return _Layouts.Count; }
        }

        #endregion

        #region Private-Members

        private readonly MarkdownLayoutCache _Layouts = new MarkdownLayoutCache();
        private ArmadaTheme? _Theme = null;
        private string _ThemeSignature = "";
        private MarkdownRenderOptions? _Options = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Markdown laid out at a width in a theme, from the cache when the same text was laid out at the same width
        /// in the same theme. Empty or whitespace input renders no lines.
        /// </summary>
        /// <param name="markdown">Markdown source, or null.</param>
        /// <param name="width">Available width in columns (values below 1 are treated as 1).</param>
        /// <param name="theme">Palette.</param>
        /// <returns>Rendered rows, none wider than the width. Do not modify.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
        public IReadOnlyList<StyledText> GetRows(string? markdown, int width, ArmadaTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (String.IsNullOrWhiteSpace(markdown)) return new List<StyledText>();
            return _Layouts.GetRows(markdown!, Math.Max(1, width), OptionsFor(theme));
        }

        /// <summary>
        /// Drop every cached layout and the theme options (call after editing a theme's colors in place).
        /// </summary>
        public void Clear()
        {
            _Layouts.Clear();
            _Theme = null;
            _ThemeSignature = "";
            _Options = null;
        }

        #endregion

        #region Private-Methods

        private MarkdownRenderOptions OptionsFor(ArmadaTheme theme)
        {
            string signature = theme.Name + "|" + theme.AsciiGlyphs + "|" + theme.AsciiBorders;
            if (_Options != null && ReferenceEquals(_Theme, theme) && _ThemeSignature == signature) return _Options;

            // A new options instance misses every old entry (the cache compares options by reference); clear them
            // so a theme switch does not keep the previous theme's layouts alive until they age out.
            _Layouts.Clear();
            _Theme = theme;
            _ThemeSignature = signature;
            _Options = ArmadaMarkdown.Options(theme);
            return _Options;
        }

        #endregion
    }
}
