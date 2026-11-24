namespace Armada.Core.Services.Archive
{
    // An immutable per-candidate owner result; never reused as authorization after upload.
    internal sealed record HistoryOwnerResolution(string? MissionId, string? Failure);
}
