namespace Armada.Core.Services
{
    using System;
    using System.IO;

    /// <summary>Resolves declared artifact files and directories within their owning checkout.</summary>
    internal static class SiblingArtifactPath
    {
        internal static string? Resolve(string root, string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
            try
            {
                string? resolved = PathContainment.TryResolve(root, relativePath);
                return resolved != null && !String.Equals(Path.TrimEndingDirectorySeparator(resolved),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.Ordinal) ? resolved : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        internal static SiblingArtifactKindEnum Kind(string? path)
        {
            if (path == null) return SiblingArtifactKindEnum.Missing;
            if (Directory.Exists(path)) return SiblingArtifactKindEnum.Directory;
            return File.Exists(path) ? SiblingArtifactKindEnum.File : SiblingArtifactKindEnum.Missing;
        }
    }
}
