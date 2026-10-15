namespace Armada.Tui.Widgets
{
    using System;
    using System.Collections.Generic;
    using Armada.Tui.Text;
    using TUIKit;
    using TUIKit.Input;
    using TUIKit.Widgets;

    /// <summary>
    /// Single-line text field: TUIKit's <see cref="TextField"/> (grapheme-aware editing, shell line-editing keys, prefill
    /// selection, per-grapheme masking with <c>Ctrl+R</c> reveal, a length limit, validation, and submit) in the Armada
    /// palette, with a translated placeholder. Keys: Left/Right, Home/End, Ctrl+A/Ctrl+E, Ctrl+Left/Ctrl+Right by word,
    /// Backspace/Delete, Ctrl+U deletes to the start, Ctrl+W deletes a word, Enter submits. Ctrl+K is left to the shell
    /// (the command palette), so the field never deletes to the end. A value set with <see cref="Prefill"/> is shown
    /// selected: the first typed character or paste replaces it, Backspace, Delete, and Ctrl+U clear it, and a caret
    /// movement keeps it for editing. Pasted line breaks and tabs become spaces. Not thread-safe.
    /// </summary>
    public class TextInput : ArmadaWidget, IPasteTarget, ITextEntry, ITextEntryKeys, IKeyHintSource
    {
        #region Public-Members

        /// <inheritdoc />
        public virtual bool AcceptsText
        {
            get { return true; }
        }

        /// <summary>
        /// Current text. Setting it moves the caret to the end, clears a prefill selection, and raises
        /// <see cref="ValueChanged"/> when it differs. A value longer than <see cref="MaxLength"/> is kept whole (the
        /// limit applies to typing and pastes).
        /// </summary>
        public string Value
        {
            get { return _Field.Value; }
            set { _Field.Value = value ?? ""; }
        }

        /// <summary>
        /// True while a value set with <see cref="Prefill"/> is still selected (untouched by the user), so the next typed
        /// character or paste replaces it instead of appending to it.
        /// </summary>
        public bool PrefillSelected
        {
            get { return _Field.IsAllSelected; }
        }

        /// <summary>
        /// Placeholder (English, translated when shown) drawn muted when empty.
        /// </summary>
        public string Placeholder { get; set; } = "";

        /// <summary>
        /// Mask the text (passwords, API keys): one <c>*</c> per grapheme.
        /// </summary>
        public bool Masked
        {
            get { return _Field.IsMasked; }
            set { _Field.MaskChar = value ? '*' : '\0'; }
        }

        /// <summary>
        /// Show a masked value in clear text (toggled with Ctrl+R).
        /// </summary>
        public bool Revealed
        {
            get { return _Field.IsRevealed; }
            set { _Field.IsRevealed = value; }
        }

        /// <summary>
        /// Maximum length in graphemes for typing and pastes; 0 is unlimited. Clamped to 0..100000.
        /// </summary>
        public int MaxLength
        {
            get { return _Field.MaxLength; }
            set { _Field.MaxLength = Math.Clamp(value, 0, 100000); }
        }

        /// <summary>
        /// Validation: returns an error message (English, translated when shown) or null when valid.
        /// </summary>
        public Func<string, string?>? Validator
        {
            get { return _Field.Validator; }
            set { _Field.Validator = value; }
        }

        /// <summary>
        /// Last validation error, or null. Once set, it is re-checked on every change.
        /// </summary>
        public string? Error
        {
            get { return _Field.Error; }
        }

        /// <inheritdoc />
        public KeyHint? LeaveHint
        {
            get { return null; }
        }

        /// <summary>
        /// Raised after the text changes.
        /// </summary>
        public event EventHandler<ValueChangedEventArgs<string>>? ValueChanged;

        /// <summary>
        /// Raised when Enter is pressed. When no handler is attached Enter is not consumed.
        /// </summary>
        public event EventHandler? Submitted
        {
            add { _Field.Submitted += value; }
            remove { _Field.Submitted -= value; }
        }

        #endregion

        #region Private-Members

        private static readonly KeyChord _RevealKey = new KeyChord(KeyCode.Character, 'r', KeyModifiers.Ctrl);
        private readonly TextField _Field = new TextField();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty field.
        /// </summary>
        public TextInput()
        {
            _Field.UseLineEditingKeys = true;
            _Field.RevealKey = _RevealKey;
            _Field.ValueChanged += OnFieldValueChanged;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the validator and store the result in <see cref="Error"/>.
        /// </summary>
        /// <returns>True when valid.</returns>
        public bool Validate()
        {
            return _Field.Validate();
        }

        /// <summary>
        /// Set a suggested value (for example a documented default password) that the user can accept with Enter or
        /// replace by typing: it starts selected, so typing does not append to it.
        /// </summary>
        /// <param name="value">Suggested value.</param>
        public void Prefill(string? value)
        {
            Value = value ?? "";
            _Field.SelectAll();
        }

        /// <summary>
        /// Insert text at the caret (line breaks and tabs become spaces).
        /// </summary>
        /// <param name="text">Text.</param>
        public void Insert(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            _Field.Insert(text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));
        }

        /// <inheritdoc />
        public bool HandlePaste(string text)
        {
            Insert(text);
            return true;
        }

        /// <inheritdoc />
        public bool ConsumesChord(KeyChord chord)
        {
            if (IsPaletteChord(chord.Code, chord.Rune, chord.Modifiers)) return false;
            return _Field.ConsumesChord(chord);
        }

        /// <inheritdoc />
        public IReadOnlyList<KeyHint>? GetKeyHints()
        {
            // Only the reveal key: screens already describe Enter in their own words.
            if (!Masked) return null;
            return new List<KeyHint> { new KeyHint(_RevealKey, Revealed ? "Hide" : "Show").WhileTyping() };
        }

        /// <inheritdoc />
        public override bool HandleKey(KeyEvent key)
        {
            // Alt chords and stray control runes belong to the screen (menus, shortcuts), never to the text.
            if (key.Code == KeyCode.Character && ((key.Modifiers & KeyModifiers.Alt) != 0 || key.Rune < 32)) return false;
            if (IsPaletteChord(key.Code, key.Rune, key.Modifiers)) return false;
            return _Field.HandleKey(key);
        }

        /// <inheritdoc />
        public override bool HandleMouse(MouseEvent mouse)
        {
            return _Field.HandleMouse(mouse);
        }

        /// <inheritdoc />
        public override Size Measure(Size available)
        {
            return new Size(available.Width, 1);
        }

        /// <inheritdoc />
        public override void Render(ISurface surface)
        {
            if (surface.Size.Width < 1 || surface.Size.Height < 1) return;
            CellStyle style = IsFocused ? Theme.InputFocused : Theme.Input;
            _Field.NormalStyle = style;
            _Field.PlaceholderStyle = style.WithForeground(Theme.Muted.Foreground);
            _Field.SelectionStyle = IsFocused ? style.WithAttribute(CellAttributes.Reverse, true) : style;
            _Field.Placeholder = String.IsNullOrEmpty(Placeholder) ? null : T(Placeholder);
            _Field.IsFocused = IsFocused;
            _Field.Render(new SurfaceView(surface, new Rect(0, 0, surface.Size.Width, 1)));
        }

        #endregion

        #region Private-Methods

        private static bool IsPaletteChord(KeyCode code, int rune, KeyModifiers modifiers)
        {
            return code == KeyCode.Character && modifiers == KeyModifiers.Ctrl && Char.ToLowerInvariant((char)rune) == 'k';
        }

        private void OnFieldValueChanged(object? sender, ValueChangedEventArgs<string> e)
        {
            if (Error != null) _Field.Validate();
            EventHandler<ValueChangedEventArgs<string>>? handler = ValueChanged;
            if (handler != null) handler(this, new ValueChangedEventArgs<string>(e.OldValue, e.NewValue));
        }

        #endregion
    }
}
