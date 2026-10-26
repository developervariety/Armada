namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Models;

    public sealed partial class VesselCheckoutImportService
    {
        private List<string> ReadAllowedRoots()
        {
            return new List<string>(_Settings.VesselImport.AllowedRoots);
        }

        private static string? FindCheckoutRoot(List<string> roots, string checkoutPath, out string canonicalCheckoutPath)
        {
            canonicalCheckoutPath = String.Empty;
            foreach (string root in roots)
            {
                string? error = VesselPathPolicy.ValidateCheckoutPath(root, checkoutPath, out string canonicalRoot, out string candidate);
                if (error == null)
                {
                    canonicalCheckoutPath = candidate;
                    return canonicalRoot;
                }
            }
            return null;
        }

        private async Task<Dictionary<string, string>> BuildExistingPathMapAsync(
            CancellationToken token,
            List<string>? pathIssues = null)
        {
            StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Dictionary<string, string> existingPaths = new Dictionary<string, string>(comparer);
            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            foreach (Vessel vessel in vessels)
            {
                AddExistingPath(existingPaths, vessel.LocalPath, vessel.Id, pathIssues);
                AddExistingPath(existingPaths, vessel.WorkingDirectory, vessel.Id, pathIssues);
            }
            return existingPaths;
        }

        private static void AddExistingPath(
            Dictionary<string, string> existingPaths,
            string? path,
            string vesselId,
            List<string>? pathIssues)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                string lexicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                AddPathKey(existingPaths, lexicalPath, vesselId);
                string? physicalPath = ResolvePhysicalDirectoryPath(lexicalPath);
                if (physicalPath != null) AddPathKey(existingPaths, physicalPath, vesselId);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException
                || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                pathIssues?.Add("An existing vessel path could not be canonicalized.");
            }
        }

        private static void AddPathKey(Dictionary<string, string> existingPaths, string path, string vesselId)
        {
            if (!existingPaths.ContainsKey(path)) existingPaths.Add(path, vesselId);
        }

        private static string? ResolvePhysicalDirectoryPath(string path)
        {
            if (!Directory.Exists(path)) return null;
            string fullPath = Path.GetFullPath(path);
            string? current = Path.GetPathRoot(fullPath);
            if (String.IsNullOrWhiteSpace(current)) return null;
            string remainder = fullPath.Substring(current.Length);
            string[] segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current)) return null;
                FileSystemInfo? target = new DirectoryInfo(current).ResolveLinkTarget(true);
                if (target != null) current = Path.GetFullPath(target.FullName);
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }

        private static void AddDiscoveryError(VesselCheckoutDiscovery discovery, string message)
        {
            const int MaxErrors = 20;
            if (discovery.Errors.Count < MaxErrors - 1)
            {
                discovery.Errors.Add(message);
                return;
            }
            if (discovery.Errors.Count == MaxErrors - 1)
                discovery.Errors.Add("Additional checkout discovery errors were omitted.");
        }
    }
}
