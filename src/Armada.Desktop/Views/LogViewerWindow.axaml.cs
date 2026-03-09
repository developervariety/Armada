namespace Armada.Desktop.Views
{
    using Avalonia.Controls;
    using Avalonia.Input;
    using Avalonia.Interactivity;
    using Armada.Desktop.ViewModels;

    /// <summary>
    /// Captain log viewer window.
    /// </summary>
    public partial class LogViewerWindow : Window
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public LogViewerWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Instantiate with view model.
        /// </summary>
        public LogViewerWindow(LogViewerViewModel viewModel) : this()
        {
            DataContext = viewModel;
            Title = "Captain Log: " + viewModel.CaptainName;
            Closed += (s, e) => viewModel.Dispose();
        }

        private async void OnRefreshClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is LogViewerViewModel vm)
            {
                await vm.LoadLogAsync();
            }
        }

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is LogViewerViewModel vm && Clipboard != null)
            {
                await Clipboard.SetTextAsync(vm.LogContent);
            }
        }
    }
}
