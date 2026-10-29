namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Picks the command to run for an intent on a vessel. Order: (1) the vessel override, (2) a repo-declared
    /// script (package.json scripts, Makefile or justfile target, composer.json scripts, the Gradle wrapper),
    /// (3) the <see cref="EcosystemCommandCatalog"/> default for the first detected ecosystem that supports the
    /// intent, with osv-scanner as the universal Audit fallback and gitleaks for Secrets. The result names its
    /// source so callers can report it. Resolution never throws for unreadable or malformed repository files.
    /// </summary>
    public static class EcosystemCommandResolver
    {
        #region Private-Members

        private static readonly string[] _MakefileNames = new string[] { "Makefile", "makefile", "GNUmakefile" };
        private static readonly string[] _JustfileNames = new string[] { "justfile", "Justfile", ".justfile" };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The repo script / Makefile target / justfile recipe name for an intent: build, test, lint, audit,
        /// outdated, licenses or secrets.
        /// </summary>
        /// <param name="intent">Intent.</param>
        /// <returns>Script name.</returns>
        public static string GetScriptName(EcosystemCommandIntentEnum intent)
        {
            return intent.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// The vessel's own command for an intent: Build is <see cref="Vessel.DefinitionOfDoneBuildCommand"/>,
        /// Test is <see cref="Vessel.TestCommand"/> (falling back to <see cref="Vessel.DefinitionOfDoneTestCommand"/>),
        /// Lint is <see cref="Vessel.LintCommand"/> and Audit is <see cref="Vessel.AuditCommand"/>. Other intents have
        /// no override. Blank values count as unset.
        /// </summary>
        /// <param name="vessel">Vessel; null has no overrides.</param>
        /// <param name="intent">Intent.</param>
        /// <param name="field">Name of the field the override came from, or null.</param>
        /// <returns>The trimmed override, or null.</returns>
        public static string? GetOverride(Vessel? vessel, EcosystemCommandIntentEnum intent, out string? field)
        {
            field = null;
            if (vessel == null) return null;
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build:
                    return Pick(vessel.DefinitionOfDoneBuildCommand, nameof(Vessel.DefinitionOfDoneBuildCommand), ref field);
                case EcosystemCommandIntentEnum.Test:
                    return Pick(vessel.TestCommand, nameof(Vessel.TestCommand), ref field)
                        ?? Pick(vessel.DefinitionOfDoneTestCommand, nameof(Vessel.DefinitionOfDoneTestCommand), ref field);
                case EcosystemCommandIntentEnum.Lint:
                    return Pick(vessel.LintCommand, nameof(Vessel.LintCommand), ref field);
                case EcosystemCommandIntentEnum.Audit:
                    return Pick(vessel.AuditCommand, nameof(Vessel.AuditCommand), ref field);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Resolve the command for an intent.
        /// </summary>
        /// <param name="vessel">Vessel whose overrides apply; null for none.</param>
        /// <param name="ecosystems">Detected ecosystems, usually from <see cref="VesselEcosystemDetector.Detect"/>; null for none.</param>
        /// <param name="intent">Intent.</param>
        /// <param name="workingDirectory">Working directory holding the repo scripts; null or missing skips the repo-script step.</param>
        /// <param name="windows">True to return Windows commands, false for POSIX.</param>
        /// <returns>Resolved command; never null. Source is NotSupported (and Command empty) when nothing applies.</returns>
        public static ResolvedEcosystemCommand Resolve(
            Vessel? vessel,
            IReadOnlyList<DetectedEcosystem>? ecosystems,
            EcosystemCommandIntentEnum intent,
            string? workingDirectory,
            bool windows)
        {
            List<DetectedEcosystem> detected = new List<DetectedEcosystem>();
            if (ecosystems != null)
            {
                foreach (DetectedEcosystem e in ecosystems)
                {
                    if (e != null) detected.Add(e);
                }
            }

            string? field;
            string? overrideCommand = GetOverride(vessel, intent, out field);
            if (overrideCommand != null)
            {
                return new ResolvedEcosystemCommand
                {
                    Intent = intent,
                    Source = EcosystemCommandSourceEnum.VesselOverride,
                    Command = overrideCommand,
                    Detail = "Vessel." + field
                };
            }

            ResolvedEcosystemCommand? script = ResolveRepoScript(detected, intent, workingDirectory, windows);
            if (script != null) return script;

            foreach (DetectedEcosystem e in detected)
            {
                EcosystemCommand entry = EcosystemCommandCatalog.Get(e, intent);
                if (entry.Supported) return FromCatalog(entry, intent, e.RootPath, e.Ecosystem + " default (" + e.Tool + ")", windows);
            }

            if (intent == EcosystemCommandIntentEnum.Audit)
                return FromCatalog(EcosystemCommandCatalog.UniversalAudit(), intent, ".", "Universal audit fallback (osv-scanner)", windows);
            if (intent == EcosystemCommandIntentEnum.Secrets)
                return FromCatalog(EcosystemCommandCatalog.Secrets(), intent, ".", "Secrets scan (gitleaks)", windows);

            return new ResolvedEcosystemCommand
            {
                Intent = intent,
                Source = EcosystemCommandSourceEnum.NotSupported,
                Detail = detected.Count == 0 ? "No ecosystems detected" : "No detected ecosystem supports " + intent
            };
        }

        /// <summary>
        /// Resolve the command for an intent on a vessel, detecting ecosystems in its working directory and using the
        /// current platform's command variants.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="intent">Intent.</param>
        /// <returns>Resolved command; never null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="vessel"/> is null.</exception>
        public static ResolvedEcosystemCommand Resolve(Vessel vessel, EcosystemCommandIntentEnum intent)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            List<DetectedEcosystem> ecosystems = VesselEcosystemDetector.Detect(vessel.WorkingDirectory);
            return Resolve(vessel, ecosystems, intent, vessel.WorkingDirectory, OperatingSystem.IsWindows());
        }

        /// <summary>
        /// Render a resolved command as one shell line: the command itself when it runs at the working directory
        /// root, otherwise prefixed with a change into its root path. Empty when not supported.
        /// </summary>
        /// <param name="resolved">Resolved command.</param>
        /// <param name="windows">True for Windows path separators.</param>
        /// <returns>Shell line; never null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolved"/> is null.</exception>
        public static string ToShellLine(ResolvedEcosystemCommand resolved, bool windows)
        {
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));
            if (String.IsNullOrEmpty(resolved.Command)) return String.Empty;
            if (resolved.RootPath == "." || resolved.Source == EcosystemCommandSourceEnum.VesselOverride) return resolved.Command;
            string root = windows ? resolved.RootPath.Replace('/', '\\') : resolved.RootPath;
            return "cd " + root + " && " + resolved.Command;
        }

        #endregion

        #region Private-Methods

        private static string? Pick(string? value, string name, ref string? field)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            field = name;
            return value.Trim();
        }

        private static ResolvedEcosystemCommand? ResolveRepoScript(List<DetectedEcosystem> detected, EcosystemCommandIntentEnum intent, string? workingDirectory, bool windows)
        {
            string name = GetScriptName(intent);
            bool haveDirectory = !String.IsNullOrWhiteSpace(workingDirectory) && SafeDirectoryExists(workingDirectory!);

            if (haveDirectory)
            {
                // 1. package.json scripts.
                foreach (DetectedEcosystem e in detected)
                {
                    if (e.Ecosystem != VesselEcosystemEnum.Node) continue;
                    HashSet<string> scripts = VesselEcosystemDetector.ReadManifestScripts(Combine(workingDirectory!, e.RootPath, "package.json"));
                    if (!scripts.Contains(name)) continue;
                    string pm = String.IsNullOrEmpty(e.Tool) ? "npm" : e.Tool;
                    return Script(intent, pm + " run " + name, VesselEcosystemEnum.Node, e.RootPath, "package.json script " + name);
                }

                // 2. Makefile or justfile target at the root, then at each ecosystem root.
                List<string> roots = new List<string> { "." };
                foreach (DetectedEcosystem e in detected)
                {
                    if (!roots.Contains(e.RootPath)) roots.Add(e.RootPath);
                }

                foreach (string root in roots)
                {
                    foreach (string makefile in _MakefileNames)
                    {
                        if (HasTarget(Combine(workingDirectory!, root, makefile), name, false))
                            return Script(intent, "make " + name, null, root, makefile + " target " + name);
                    }

                    foreach (string justfile in _JustfileNames)
                    {
                        if (HasTarget(Combine(workingDirectory!, root, justfile), name, true))
                            return Script(intent, "just " + name, null, root, justfile + " recipe " + name);
                    }
                }

                // 3. composer.json scripts. run-script, because audit/outdated/licenses are built-in composer commands.
                foreach (DetectedEcosystem e in detected)
                {
                    if (e.Ecosystem != VesselEcosystemEnum.Php) continue;
                    HashSet<string> scripts = VesselEcosystemDetector.ReadManifestScripts(Combine(workingDirectory!, e.RootPath, "composer.json"));
                    if (!scripts.Contains(name)) continue;
                    return Script(intent, "composer run-script " + name, VesselEcosystemEnum.Php, e.RootPath, "composer.json script " + name);
                }
            }

            // 4. The Gradle wrapper checked into the repository.
            foreach (DetectedEcosystem e in detected)
            {
                if (e.Ecosystem != VesselEcosystemEnum.Gradle || !String.Equals(e.Tool, "gradlew", StringComparison.OrdinalIgnoreCase)) continue;
                EcosystemCommand entry = EcosystemCommandCatalog.Get(e, intent);
                // Only commands that invoke the wrapper count; the osv-scanner audit fallback is still a catalog default.
                if (!entry.Supported || !entry.PosixCommand.StartsWith("./gradlew ", StringComparison.Ordinal)) continue;
                ResolvedEcosystemCommand wrapper = FromCatalog(entry, intent, e.RootPath, "Gradle wrapper", windows);
                wrapper.Source = EcosystemCommandSourceEnum.RepoScript;
                return wrapper;
            }

            return null;
        }

        private static ResolvedEcosystemCommand Script(EcosystemCommandIntentEnum intent, string command, VesselEcosystemEnum? ecosystem, string root, string detail)
        {
            return new ResolvedEcosystemCommand
            {
                Intent = intent,
                Source = EcosystemCommandSourceEnum.RepoScript,
                Command = command,
                Ecosystem = ecosystem,
                RootPath = root,
                Detail = detail
            };
        }

        private static ResolvedEcosystemCommand FromCatalog(EcosystemCommand entry, EcosystemCommandIntentEnum intent, string root, string detail, bool windows)
        {
            return new ResolvedEcosystemCommand
            {
                Intent = intent,
                Source = EcosystemCommandSourceEnum.EcosystemDefault,
                Command = entry.GetCommand(windows),
                Ecosystem = entry.Ecosystem,
                RootPath = root,
                Detail = detail,
                RequiredTools = new List<string>(entry.GetRequiredTools(windows)),
                RequiresNetwork = entry.RequiresNetwork,
                AuditParserId = entry.AuditParserId
            };
        }

        private static bool HasTarget(string path, string name, bool justfile)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string escaped = Regex.Escape(name);
                // make: "name:" or "name dep:" lists, never "name := value". just: optional "@" quiet prefix and parameters.
                string pattern = justfile
                    ? @"^@?" + escaped + @"([ \t][^:=\r\n]*)?:(?!=)"
                    : @"^([^:=#\s][^:=#\r\n]*[ \t])?" + escaped + @"([ \t][^:=#\r\n]*)?:(?!=)";
                Regex regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
                return regex.IsMatch(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeDirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Combine(string workingDirectory, string root, string file)
        {
            if (String.IsNullOrEmpty(root) || root == ".") return Path.Combine(workingDirectory, file);
            return Path.Combine(workingDirectory, root.Replace('/', Path.DirectorySeparatorChar), file);
        }

        #endregion
    }
}
