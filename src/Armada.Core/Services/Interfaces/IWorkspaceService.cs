namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Service for safe workspace browsing and editing inside a vessel working directory.
    /// </summary>
    public interface IWorkspaceService
    {
        /// <summary>
        /// List one workspace directory.
        /// </summary>
        Task<WorkspaceTreeResult> GetTreeAsync(Vessel vessel, string? path = null, CancellationToken token = default);

        /// <summary>
        /// Read one workspace file.
        /// </summary>
        Task<WorkspaceFileResponse> GetFileAsync(Vessel vessel, string path, CancellationToken token = default);

        /// <summary>
        /// Save one workspace file.
        /// </summary>
        Task<WorkspaceSaveResult> SaveFileAsync(Vessel vessel, WorkspaceSaveRequest request, CancellationToken token = default);

        /// <summary>
        /// Create one workspace directory.
        /// </summary>
        Task<WorkspaceOperationResult> CreateDirectoryAsync(Vessel vessel, WorkspaceCreateDirectoryRequest request, CancellationToken token = default);

        /// <summary>
        /// Rename or move one workspace entry.
        /// </summary>
        Task<WorkspaceOperationResult> RenameAsync(Vessel vessel, WorkspaceRenameRequest request, CancellationToken token = default);

        /// <summary>
        /// Delete one workspace entry.
        /// </summary>
        Task<WorkspaceOperationResult> DeleteAsync(Vessel vessel, string path, CancellationToken token = default);

        /// <summary>
        /// Search text files in a workspace.
        /// </summary>
        Task<WorkspaceSearchResult> SearchAsync(Vessel vessel, string query, int maxResults = 200, CancellationToken token = default);

        /// <summary>
        /// Inspect the current working tree changes for a workspace.
        /// </summary>
        Task<WorkspaceChangesResult> GetChangesAsync(Vessel vessel, CancellationToken token = default);

        /// <summary>
        /// Get high-level workspace status for one vessel.
        /// </summary>
        Task<WorkspaceStatusResult> GetStatusAsync(
            Vessel vessel,
            IReadOnlyList<WorkspaceActiveMission>? activeMissions = null,
            CancellationToken token = default);
    }
}
