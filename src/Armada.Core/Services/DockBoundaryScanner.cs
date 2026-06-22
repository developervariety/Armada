namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Evaluates a unified diff and changed-path set against dock-boundary rules:
    /// protected-path globs, CORE_RULE_5 secret patterns, and configured private-identifier
    /// denylists for public repositories. Returns structured, blocking findings with
    /// actionable messages. Secret bytes and private identifier values are never echoed
    /// in any returned field.
    /// </summary>
    public sealed class DockBoundaryScanner
    {
        #region Public-Methods

        /// <summary>
        /// Run the boundary scan for a single diff snapshot.
        /// </summary>
        /// <param name="unifiedDiff">
        /// Captured unified diff text (output of git diff). Null or empty is accepted
        /// gracefully; protected-path checking will still run against changedFilePaths when supplied.
        /// </param>
        /// <param name="changedFilePaths">
        /// Optional explicit set of repository-relative changed paths. When non-null these
        /// are merged with paths extracted from the diff to ensure complete coverage even
        /// when the diff is partial or absent.
        /// </param>
        /// <param name="vesselId">Vessel identifier used for public-repo classification.</param>
        /// <param name="vesselName">Vessel display name used for public-repo classification.</param>
        /// <param name="vesselRepoUrl">Repository URL used for public-repo classification.</param>
        /// <param name="configuredProtectedPaths">
        /// Per-vessel declared protected glob patterns merged with the built-in set.
        /// Null is equivalent to an empty list.
        /// </param>
        /// <param name="settings">Dock-boundary scan configuration. Must not be null.</param>
        /// <returns>Scan result containing the pass verdict and any blocking findings.</returns>
        public DockBoundaryScanResult Scan(
            string? unifiedDiff,
            IEnumerable<string>? changedFilePaths,
            string? vesselId,
            string? vesselName,
            string? vesselRepoUrl,
            IList<string>? configuredProtectedPaths,
            DockBoundarySettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            DockBoundaryScanResult result = new DockBoundaryScanResult { Passed = true };

            // Merge explicit paths and paths parsed from the diff into one de-duplicated set.
            HashSet<string> allPaths = BuildEffectivePaths(unifiedDiff, changedFilePaths);

            // --- Protected-path check ---
            string? protectedViolation = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(
                allPaths, configuredProtectedPaths);

            if (protectedViolation != null)
            {
                result.Passed = false;
                result.Findings.Add(new DockBoundaryFinding
                {
                    Kind = DockBoundaryFindingKindEnum.ProtectedPath,
                    Path = protectedViolation,
                    FindingLabel = protectedViolation,
                    Message = "Protected path '" + protectedViolation +
                              "' was modified. Use a [CLAUDE.MD-PROPOSAL] block to propose changes instead."
                });
            }

            // --- Secret scan and private-identifier scan require diff content ---
            if (String.IsNullOrEmpty(unifiedDiff))
            {
                return result;
            }

            bool isPublicVessel = IsPublicVessel(vesselId, vesselName, vesselRepoUrl, settings);
            bool runSecrets = settings.SecretScanEnabled;
            bool runPrivateIds = settings.PrivateIdentifierScanEnabled && isPublicVessel;

            if (!runSecrets && !runPrivateIds)
            {
                return result;
            }

            Dictionary<string, List<string>> fileAddedLines = ParseAddedLinesByFile(unifiedDiff);
            List<Regex> compiledPrivateIdPatterns = BuildPrivateIdPatterns(settings, runPrivateIds);

            foreach (KeyValuePair<string, List<string>> fileEntry in fileAddedLines)
            {
                string filePath = fileEntry.Key;
                List<string> addedLines = fileEntry.Value;

                if (runSecrets)
                {
                    ScanForSecrets(filePath, addedLines, result);
                }

                if (runPrivateIds)
                {
                    ScanForPrivateIdentifiers(filePath, addedLines, settings, compiledPrivateIdPatterns, result);
                }
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private static HashSet<string> BuildEffectivePaths(
            string? unifiedDiff,
            IEnumerable<string>? changedFilePaths)
        {
            HashSet<string> combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (changedFilePaths != null)
            {
                foreach (string p in changedFilePaths)
                {
                    if (!String.IsNullOrWhiteSpace(p)) combined.Add(p.Trim());
                }
            }

            if (!String.IsNullOrEmpty(unifiedDiff))
            {
                IReadOnlyList<string> fromDiff = ProtectedPathsValidator.ExtractChangedFilesFromDiff(unifiedDiff);
                foreach (string p in fromDiff)
                {
                    if (!String.IsNullOrWhiteSpace(p)) combined.Add(p);
                }
            }

            return combined;
        }

        private static bool IsPublicVessel(
            string? vesselId,
            string? vesselName,
            string? vesselRepoUrl,
            DockBoundarySettings settings)
        {
            if (settings.PublicRepoPatterns == null || settings.PublicRepoPatterns.Count == 0)
            {
                return false;
            }

            foreach (string pattern in settings.PublicRepoPatterns)
            {
                if (String.IsNullOrWhiteSpace(pattern)) continue;
                string p = pattern.Trim();

                bool idMatch = !String.IsNullOrEmpty(vesselId) &&
                               vesselId!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                bool nameMatch = !String.IsNullOrEmpty(vesselName) &&
                                 vesselName!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                bool urlMatch = !String.IsNullOrEmpty(vesselRepoUrl) &&
                                vesselRepoUrl!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;

                if (idMatch || nameMatch || urlMatch) return true;
            }

            return false;
        }

        private static Dictionary<string, List<string>> ParseAddedLinesByFile(string unifiedDiff)
        {
            Dictionary<string, List<string>> result =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            string currentFile = "";
            string[] rawLines = unifiedDiff.Split('\n');

            foreach (string rawLine in rawLines)
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    // Re-use the existing diff-header parser to extract the destination path.
                    IReadOnlyList<string> parsedPaths =
                        ProtectedPathsValidator.ExtractChangedFilesFromDiff(line + "\n");
                    currentFile = parsedPaths.Count > 0 ? parsedPaths[parsedPaths.Count - 1] : "";

                    if (!String.IsNullOrEmpty(currentFile) && !result.ContainsKey(currentFile))
                    {
                        result[currentFile] = new List<string>();
                    }
                    continue;
                }

                if (String.IsNullOrEmpty(currentFile)) continue;
                if (line.Length == 0 || line[0] != '+') continue;
                if (line.StartsWith("+++", StringComparison.Ordinal)) continue;

                // Strip the leading '+' and store the raw content (without secret bytes going into return value).
                result[currentFile].Add(line.Substring(1));
            }

            return result;
        }

        private static void ScanForSecrets(
            string filePath,
            List<string> addedLines,
            DockBoundaryScanResult result)
        {
            foreach (string addedLine in addedLines)
            {
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(addedLine);
                foreach (string rule in fired)
                {
                    result.Passed = false;
                    result.Findings.Add(new DockBoundaryFinding
                    {
                        Kind = DockBoundaryFindingKindEnum.Secret,
                        Path = filePath,
                        FindingLabel = rule,
                        Message = "Secret pattern '" + rule + "' matched an added line in '" +
                                  filePath + "'. Remove or redact the secret before committing."
                    });
                }
            }
        }

        private static List<Regex> BuildPrivateIdPatterns(DockBoundarySettings settings, bool runPrivateIds)
        {
            List<Regex> compiled = new List<Regex>();
            if (!runPrivateIds || settings.PrivateIdentifiers == null) return compiled;

            foreach (DockBoundaryPrivateIdentifierEntry entry in settings.PrivateIdentifiers)
            {
                if (String.IsNullOrWhiteSpace(entry.Pattern)) continue;
                try
                {
                    compiled.Add(new Regex(entry.Pattern.Trim(), RegexOptions.Compiled | RegexOptions.IgnoreCase));
                }
                catch (ArgumentException)
                {
                    // Silently skip malformed patterns; the operator is notified at config-load time.
                }
            }

            return compiled;
        }

        private static void ScanForPrivateIdentifiers(
            string filePath,
            List<string> addedLines,
            DockBoundarySettings settings,
            List<Regex> compiledPatterns,
            DockBoundaryScanResult result)
        {
            if (compiledPatterns.Count == 0) return;
            if (settings.PrivateIdentifiers == null) return;

            // Pair each compiled regex with its label for reporting. The label is all that
            // is surfaced in the finding; the actual matched text is never included.
            int idx = 0;
            List<string> activeLabels = new List<string>();
            foreach (DockBoundaryPrivateIdentifierEntry entry in settings.PrivateIdentifiers)
            {
                if (!String.IsNullOrWhiteSpace(entry.Pattern)) activeLabels.Add(entry.Label);
            }

            if (activeLabels.Count != compiledPatterns.Count) return;

            for (int i = 0; i < compiledPatterns.Count; i++)
            {
                Regex pattern = compiledPatterns[i];
                string label = activeLabels[i];
                bool alreadyReported = false;

                foreach (string addedLine in addedLines)
                {
                    if (alreadyReported) break;
                    if (pattern.IsMatch(addedLine))
                    {
                        alreadyReported = true;
                        result.Passed = false;
                        result.Findings.Add(new DockBoundaryFinding
                        {
                            Kind = DockBoundaryFindingKindEnum.PrivateIdentifier,
                            Path = filePath,
                            FindingLabel = label,
                            Message = "Private identifier '" + label + "' matched an added line in '" +
                                      filePath + "'. This identifier must not appear in public repositories."
                        });
                    }
                }

                idx++;
            }
        }

        #endregion
    }
}
