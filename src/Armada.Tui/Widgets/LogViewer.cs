namespace Armada.Tui.Widgets
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Tui.Text;
    using Armada.Tui.Theming;
    using TUIKit;
    using TUIKit.Content;
    using TUIKit.Input;

    /// <summary>
    /// Log viewer for mission and captain logs, rebuild logs, and check output: follow toggle (<c>f</c>), search,
    /// and a readable mode (<c>r</c>) that strips ANSI escapes and blank noise; with <see cref="Markdown"/> set, readable
    /// mode lays the log out as Markdown at the view's width. Lines beyond <see cref="MaxLines"/>
    /// are dropped from the top. Not thread-safe.
    /// </summary>
    public class LogViewer : ScrollTextView
    {
        #region Public-Members

        /// <summary>
        /// Maximum retained lines (memory cap). Default 20000; clamped to 100..1000000.
        /// </summary>
        public int MaxLines
        {
            get { return _MaxLines; }
            set { _MaxLines = Math.Clamp(value, 100, 1000000); }
        }

        /// <summary>
        /// Readable mode: strip ANSI and collapse repeated blank lines. Toggle with <c>r</c>.
        /// </summary>
        public bool Readable { get; set; } = true;

        /// <summary>
        /// Render the log as Markdown in readable mode (mission logs, as the dashboard does); <c>r</c> shows the raw
        /// text and copy always copies it. Default false.
        /// </summary>
        public bool Markdown
        {
            get { return _Markdown; }
            set
            {
                _Markdown = value;
                Invalidate();
            }
        }

        /// <inheritdoc />
        public override string PlainText
        {
            get { return String.Join("\n", _Lines); }
        }

        /// <summary>
        /// Retained line count.
        /// </summary>
        public int LineCount
        {
            get { return _Lines.Count; }
        }

        #endregion

        #region Private-Members

        private readonly List<string> _Lines = new List<string>();
        private int _MaxLines = 20000;
        private bool _Markdown = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate (following the tail).
        /// </summary>
        public LogViewer()
        {
            AllowFollow = true;
            Follow = true;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Replace the content.
        /// </summary>
        /// <param name="text">Log text.</param>
        public void SetText(string? text)
        {
            _Lines.Clear();
            Append(text);
        }

        /// <summary>
        /// Append text (split into lines).
        /// </summary>
        /// <param name="text">Text.</param>
        public void Append(string? text)
        {
            if (String.IsNullOrEmpty(text)) return;
            _Lines.AddRange(text!.Replace("\r\n", "\n").Split('\n'));
            if (_Lines.Count > _MaxLines) _Lines.RemoveRange(0, _Lines.Count - _MaxLines);
            Invalidate();
        }

        /// <inheritdoc />
        public override bool HandleKey(KeyEvent key)
        {
            if (!Searching && key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.None && key.Rune == 'r')
            {
                Readable = !Readable;
                Invalidate();
                return true;
            }

            return base.HandleKey(key);
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override IReadOnlyList<StyledText> BuildLines()
        {
            return Source().Select(l => StyledText.From(l)).ToList();
        }

        /// <inheritdoc />
        protected override IReadOnlyList<StyledText> BuildLines(int width)
        {
            if (_Markdown && Readable) return ArmadaMarkdown.Render(String.Join("\n", Source()), width, Theme);
            return BuildLines();
        }

        /// <inheritdoc />
        protected override void OnThemeChanged(ArmadaTheme theme)
        {
            Invalidate();
        }

        /// <inheritdoc />
        protected override string EmptyText()
        {
            return "No log output yet.";
        }

        #endregion

        #region Private-Methods

        private IEnumerable<string> Source()
        {
            IEnumerable<string> source = _Lines;
            if (Readable)
            {
                List<string> cleaned = new List<string>();
                bool lastBlank = false;
                foreach (string line in _Lines)
                {
                    string stripped = AnsiStripper.Strip(line).TrimEnd();
                    bool blank = stripped.Length == 0;
                    if (blank && lastBlank) continue;
                    cleaned.Add(stripped);
                    lastBlank = blank;
                }

                source = cleaned;
            }
            else
            {
                source = _Lines.Select(AnsiStripper.Strip);
            }

            return source;
        }

        #endregion
    }
}
