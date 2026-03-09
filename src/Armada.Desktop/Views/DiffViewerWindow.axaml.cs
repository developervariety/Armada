namespace Armada.Desktop.Views
{
    using Avalonia.Controls;
    using Avalonia.Input;
    using Avalonia.Interactivity;
    using Armada.Desktop.ViewModels;

    /// <summary>
    /// Mission diff viewer window.
    /// </summary>
    public partial class DiffViewerWindow : Window
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public DiffViewerWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Instantiate with view model.
        /// </summary>
        public DiffViewerWindow(DiffViewerViewModel viewModel) : this()
        {
            DataContext = viewModel;
            Title = "Diff: " + viewModel.MissionTitle;
        }

        private async void OnRefreshClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DiffViewerViewModel vm)
            {
                await vm.LoadDiffAsync();
            }
        }

        private async void OnCopyResponseClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DiffViewerViewModel vm && Clipboard != null)
            {
                await Clipboard.SetTextAsync(vm.DiffContent);
            }
        }

        private async void OnCopyDiffClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DiffViewerViewModel vm && Clipboard != null)
            {
                await Clipboard.SetTextAsync(vm.ParsedDiffContent);
            }
        }
    }
}
