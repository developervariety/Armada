namespace Armada.Tui.Theming
{
    using System;
    using TUIKit;
    using TUIKit.Content;
    using TUIKit.Modals;
    using TUIKit.Widgets;

    /// <summary>
    /// Pushes an <see cref="ArmadaTheme"/> into a widget. Armada widgets take the palette directly. A TUIKit widget
    /// that follows the application theme (<see cref="TUIKit.Theming.IThemeable"/>) first gets the palette as a TUIKit
    /// theme (<see cref="ThemeService.BuildTuiKitTheme"/>); the Armada roles TUIKit has no theme role for (input
    /// fields, diff lines, chart colors, dialog frames) are then set on the widget's style properties. Unknown widget
    /// types are left unchanged.
    /// Thread-safe (stateless); call on the UI loop thread because widgets are not thread-safe.
    /// </summary>
    public static class ThemeApplicator
    {
        #region Public-Methods

        /// <summary>
        /// Apply the palette to a TUIKit widget, modal, or <see cref="IThemeable"/>.
        /// </summary>
        /// <param name="target">Widget or modal; null is ignored.</param>
        /// <param name="theme">Palette.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
        public static void Apply(object? target, ArmadaTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (target == null) return;
            if (target is IThemeable themeable)
            {
                themeable.ApplyTheme(theme);
                return;
            }

            if (target is TUIKit.Theming.IThemeable kit) kit.ApplyTheme(ThemeService.BuildTuiKitTheme(theme));
            switch (target)
            {
                case TextField field:
                    field.NormalStyle = theme.Input;
                    return;
                case TextEditor editor:
                    editor.NormalStyle = theme.Input;
                    return;
                case Pane pane:
                    pane.Background = theme.Text;
                    return;
                case DiffView diff:
                    diff.AddedStyle = theme.Success;
                    diff.RemovedStyle = theme.Error;
                    diff.ContextStyle = theme.Text;
                    return;
                case DefinitionList list:
                    list.LabelStyle = theme.Muted;
                    list.ValueStyle = theme.Text;
                    list.SectionStyle = theme.Accent;
                    return;
                case Checkbox checkbox:
                    checkbox.HoverStyle = theme.Accent;
                    return;
                case StatusBar status:
                    status.KeyStyle = theme.StatusKey;
                    status.LabelStyle = theme.StatusBar;
                    return;
                case LineChart line:
                    line.Color = theme.Accent.Foreground;
                    return;
                case BarChart bars:
                    bars.Color = theme.Success.Foreground;
                    return;
                case Sparkline spark:
                    spark.LineColor = theme.Accent.Foreground;
                    return;
                case DialogModal dialog:
                    dialog.BackgroundStyle = theme.Dialog;
                    dialog.BorderStyleColor = theme.DialogBorder;
                    return;
                default:
                    return;
            }
        }

        #endregion
    }
}
