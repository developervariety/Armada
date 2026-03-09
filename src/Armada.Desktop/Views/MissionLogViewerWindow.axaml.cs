namespace Armada.Desktop.Views
{
    using Avalonia.Controls;
    using Avalonia.Input;
    using Avalonia.Interactivity;
    using Armada.Desktop.ViewModels;

    /// <summary>
    /// Mission log viewer window.
    /// </summary>
    public partial class MissionLogViewerWindow : Window
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public MissionLogViewerWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Instantiate with view model.
        /// </summary>
        public MissionLogViewerWindow(MissionLogViewerViewModel viewModel) : this()
        {
            DataContext = viewModel;
            Title = "Mission Log: " + viewModel.MissionTitle;
            Closed += (s, e) => viewModel.Dispose();
        }

        private async void OnRefreshClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MissionLogViewerViewModel vm)
            {
                await vm.LoadLogAsync();
            }
        }

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MissionLogViewerViewModel vm && Clipboard != null)
            {
                await Clipboard.SetTextAsync(vm.LogContent);
            }
        }
    }
}
