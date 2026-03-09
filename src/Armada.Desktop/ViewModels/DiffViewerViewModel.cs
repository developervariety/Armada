namespace Armada.Desktop.ViewModels
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using ReactiveUI;
    using Avalonia.Threading;
    using Armada.Desktop.Services;

    /// <summary>
    /// View model for the mission diff viewer window.
    /// </summary>
    public class DiffViewerViewModel : ViewModelBase
    {
        #region Private-Members

        private ArmadaConnectionService _Connection;
        private string _MissionId;
        private string _MissionTitle;
        private bool _IsLoading;
        private string _ErrorMessage = "";
        private string _DiffContent = "";
        private string _ParsedDiffContent = "";
        private bool _HasParsedDiff;

        #endregion

        #region Public-Members

        /// <summary>Mission title for display.</summary>
        public string MissionTitle
        {
            get => _MissionTitle;
            set => this.RaiseAndSetIfChanged(ref _MissionTitle, value);
        }

        /// <summary>Raw/JSON content for display.</summary>
        public string DiffContent
        {
            get => _DiffContent;
            set => this.RaiseAndSetIfChanged(ref _DiffContent, value);
        }

        /// <summary>Expanded diff output extracted from JSON Diff property.</summary>
        public string ParsedDiffContent
        {
            get => _ParsedDiffContent;
            set => this.RaiseAndSetIfChanged(ref _ParsedDiffContent, value);
        }

        /// <summary>Whether a parsed diff section is available.</summary>
        public bool HasParsedDiff
        {
            get => _HasParsedDiff;
            set => this.RaiseAndSetIfChanged(ref _HasParsedDiff, value);
        }

        /// <summary>Whether the diff is loading.</summary>
        public bool IsLoading
        {
            get => _IsLoading;
            set => this.RaiseAndSetIfChanged(ref _IsLoading, value);
        }

        /// <summary>Error message if diff is unavailable.</summary>
        public string ErrorMessage
        {
            get => _ErrorMessage;
            set => this.RaiseAndSetIfChanged(ref _ErrorMessage, value);
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connection">Connection service.</param>
        /// <param name="missionId">Mission ID to diff.</param>
        /// <param name="missionTitle">Mission title for display.</param>
        public DiffViewerViewModel(ArmadaConnectionService connection, string missionId, string missionTitle)
        {
            _Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _MissionId = missionId ?? throw new ArgumentNullException(nameof(missionId));
            _MissionTitle = missionTitle ?? missionId;

            _ = LoadDiffAsync();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Load or reload the diff.
        /// </summary>
        public async Task LoadDiffAsync()
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = true;
                ErrorMessage = "";
                DiffContent = "";
                ParsedDiffContent = "";
                HasParsedDiff = false;
            });

            try
            {
                string? diff = await _Connection.GetApiClient().GetMissionDiffAsync(_MissionId).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (string.IsNullOrEmpty(diff))
                    {
                        ErrorMessage = "No diff available. The worktree may have been reclaimed after mission completion.";
                        return;
                    }

                    // Try to parse as JSON
                    string trimmed = diff.TrimStart();
                    if ((trimmed.StartsWith("{") || trimmed.StartsWith("[")) && !trimmed.StartsWith("---") && !trimmed.StartsWith("+++"))
                    {
                        try
                        {
                            JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(diff);
                            string pretty = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
                            DiffContent = pretty;

                            // Extract the Diff property if present and expand escaped newlines
                            if (parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("Diff", out JsonElement diffProp) && diffProp.ValueKind == JsonValueKind.String)
                            {
                                string diffValue = diffProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(diffValue))
                                {
                                    ParsedDiffContent = diffValue;
                                    HasParsedDiff = true;
                                }
                            }

                            return;
                        }
                        catch
                        {
                            // Not valid JSON, fall through to raw display
                        }
                    }

                    DiffContent = diff;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ErrorMessage = "Error loading diff: " + ex.Message);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => IsLoading = false);
            }
        }

        #endregion
    }

    /// <summary>
    /// A single line in a diff display.
    /// </summary>
    public class DiffLine
    {
        /// <summary>Line text.</summary>
        public string Text { get; set; } = "";

        /// <summary>Line type for coloring.</summary>
        public DiffLineType LineType { get; set; } = DiffLineType.Context;
    }

    /// <summary>
    /// Type of diff line for color coding.
    /// </summary>
    public enum DiffLineType
    {
        /// <summary>Context (unchanged) line.</summary>
        Context,

        /// <summary>Added line (+).</summary>
        Added,

        /// <summary>Removed line (-).</summary>
        Removed,

        /// <summary>Hunk header (@@).</summary>
        Hunk,

        /// <summary>File header (diff, index, ---, +++).</summary>
        Header
    }
}
