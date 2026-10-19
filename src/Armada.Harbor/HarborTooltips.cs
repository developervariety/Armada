namespace Armada.Harbor
{
    using System;
    using System.Runtime.CompilerServices;
    using Avalonia;
    using Avalonia.Controls;
    using Avalonia.Controls.Primitives;
    using Avalonia.Interactivity;
    using Avalonia.LogicalTree;
    using Avalonia.Reactive;

    /// <summary>
    /// Gives every label, text box, button, check box, list, and other standard control in Harbor a hover tooltip.
    /// A control that already has one (set in code or XAML) keeps it; any other control gets one from its own text,
    /// which follows the text as it changes. Text boxes and number boxes are described by the label in front of them.
    /// Parts of control templates are skipped, so a button's inner text does not get a second tooltip. Install once,
    /// before any window opens; use on the UI thread.
    /// </summary>
    public static class HarborTooltips
    {
        #region Private-Members

        private static readonly ConditionalWeakTable<Control, Tracked> _Tracked = new ConditionalWeakTable<Control, Tracked>();
        private static bool _Installed = false;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Install the class handler that backfills tooltips as controls load.
        /// </summary>
        public static void Install()
        {
            if (_Installed) return;
            _Installed = true;
            Control.LoadedEvent.AddClassHandler<Control>(OnLoaded);
        }

        /// <summary>
        /// The tooltip Harbor would generate for a control right now, or null when it has nothing to say. Exposed for
        /// tests.
        /// </summary>
        /// <param name="control">Control.</param>
        /// <returns>Text, or null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="control"/> is null.</exception>
        public static string? Describe(Control control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            switch (control)
            {
                case TextBox box:
                    return Example(LabelFor(box), box.PlaceholderText);
                case NumericUpDown number:
                    return Combine(LabelFor(number), number.Value.HasValue ? number.Value.Value.ToString(System.Globalization.CultureInfo.CurrentCulture) : null);
                case ComboBox combo:
                    return Combine(LabelFor(combo), TextOf(combo.SelectedItem));
                case Slider slider:
                    return Combine(LabelFor(slider), slider.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
                case HeaderedContentControl headered:
                    return TextOf(headered.Header) ?? TextOf(headered.Content);
                case ContentControl content:
                    return TextOf(content.Content);
                case TextBlock text:
                    return Clean(text.Text);
                default:
                    return null;
            }
        }

        #endregion

        #region Private-Methods

        private static void OnLoaded(Control control, RoutedEventArgs e)
        {
            if (control.TemplatedParent != null) return;
            if (!(control is TextBlock || control is TextBox || control is NumericUpDown || control is ComboBox || control is Slider || control is Button || control is HeaderedContentControl || control is ToggleButton)) return;
            if (_Tracked.TryGetValue(control, out Tracked? _)) return;
            if (ToolTip.GetTip(control) != null) return;

            Tracked tracked = new Tracked();
            _Tracked.Add(control, tracked);
            Refresh(control, tracked);

            // Follow changes to whatever the description is built from.
            AvaloniaProperty? source = control switch
            {
                TextBlock => TextBlock.TextProperty,
                TextBox => TextBox.PlaceholderTextProperty,
                NumericUpDown => NumericUpDown.ValueProperty,
                ComboBox => SelectingItemsControl.SelectedItemProperty,
                Slider => RangeBase.ValueProperty,
                ContentControl => ContentControl.ContentProperty,
                _ => null
            };
            if (source != null) control.GetObservable(source).Subscribe(new AnonymousObserver<object?>(_ => Refresh(control, tracked)));
        }

        private static void Refresh(Control control, Tracked tracked)
        {
            object? current = ToolTip.GetTip(control);

            // Someone else set a tooltip after Harbor did: theirs wins from now on.
            if (current != null && !Equals(current, tracked.Applied)) return;
            string? text = Describe(control);
            tracked.Applied = text;
            ToolTip.SetTip(control, text);
        }

        private static string? LabelFor(Control control)
        {
            // Harbor lays fields out as a label followed by its field in the same panel.
            if (control.GetLogicalParent() is Panel panel)
            {
                int index = panel.Children.IndexOf(control);
                for (int i = index - 1; i >= 0; i--)
                {
                    if (panel.Children[i] is TextBlock label && !String.IsNullOrWhiteSpace(label.Text)) return Clean(label.Text);
                    if (panel.Children[i] is TextBox || panel.Children[i] is ComboBox) break;
                }
            }

            string? name = AutomationName(control);
            return name;
        }

        private static string? AutomationName(Control control)
        {
            string? name = Avalonia.Automation.AutomationProperties.GetName(control);
            return String.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static string? TextOf(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return Clean(s);
                case TextBlock text:
                    return Clean(text.Text);
                case Control control:
                    foreach (ILogical child in control.GetLogicalDescendants())
                        if (child is TextBlock inner && !String.IsNullOrWhiteSpace(inner.Text)) return Clean(inner.Text);
                    return AutomationName(control);
                default:
                    return Clean(value.ToString());
            }
        }

        private static string? Combine(string? label, string? detail)
        {
            if (label == null) return detail;
            if (detail == null || String.Equals(label, detail, StringComparison.Ordinal)) return label;
            return label + ": " + detail;
        }

        private static string? Example(string? label, string? watermark)
        {
            // A placeholder is an example of what to type, not a description: say so instead of echoing it after the label.
            string? sample = Clean(watermark);
            if (sample == null) return label;
            if (label == null) return "For example: " + sample;
            return label + " (for example: " + sample + ")";
        }

        private static string? Clean(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            string trimmed = text!.Trim();
            return trimmed.Length > 500 ? trimmed.Substring(0, 500) + "..." : trimmed;
        }

        #endregion

        #region Private-Classes

        private sealed class Tracked
        {
            public object? Applied { get; set; } = null;
        }

        #endregion
    }
}
