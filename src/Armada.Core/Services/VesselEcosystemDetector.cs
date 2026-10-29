namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Detects the build ecosystems a vessel uses from marker files in its working directory. The top level and
    /// every directory one level down are scanned; node_modules, bin, obj, vendor, target and dot-directories
    /// (.git and friends) are skipped. A directory one level down only adds ecosystems the top level does not
    /// already have, so a multi-module root (Maven, Gradle, Cargo workspace, solution) is reported once.
    /// Detection never throws: a missing or unreadable working directory yields an empty list, and an
    /// unreadable subdirectory is skipped.
    /// </summary>
    public static class VesselEcosystemDetector
    {
        #region Public-Members

        /// <summary>
        /// Directory names never scanned (compared case-insensitively). Names starting with a dot are skipped too.
        /// </summary>
        public static IReadOnlyList<string> IgnoredDirectories
        {
            get { return _IgnoredDirectories; }
        }

        #endregion

        #region Private-Members

        private static readonly List<string> _IgnoredDirectories = new List<string> { "node_modules", "bin", "obj", "vendor", "target", ".git" };
        private static readonly Regex _RequirementsPattern = new Regex(@"^requirements.*\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _FlutterPattern = new Regex(@"^\s*flutter\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Detect ecosystems in a working directory. The top level comes first, then subdirectories in ordinal
        /// name order; within a directory ecosystems follow <see cref="VesselEcosystemEnum"/> order.
        /// </summary>
        /// <param name="workingDirectory">Absolute path of the vessel working directory; null, empty or missing yields an empty list.</param>
        /// <returns>Detected ecosystems; never null.</returns>
        public static List<DetectedEcosystem> Detect(string? workingDirectory)
        {
            List<DetectedEcosystem> results = new List<DetectedEcosystem>();
            if (String.IsNullOrWhiteSpace(workingDirectory)) return results;

            string root;
            try
            {
                root = Path.GetFullPath(workingDirectory);
                if (!Directory.Exists(root)) return results;
            }
            catch (Exception)
            {
                return results;
            }

            List<DetectedEcosystem>? top = DetectInDirectory(root, ".");
            if (top == null) return results;
            results.AddRange(top);

            HashSet<VesselEcosystemEnum> topLevel = new HashSet<VesselEcosystemEnum>(top.Select(e => e.Ecosystem));
            foreach (string child in ListSubdirectories(root))
            {
                List<DetectedEcosystem>? nested = DetectInDirectory(Path.Combine(root, child), child);
                if (nested == null) continue;
                foreach (DetectedEcosystem detected in nested)
                {
                    if (!topLevel.Contains(detected.Ecosystem)) results.Add(detected);
                }
            }

            return results;
        }

        /// <summary>
        /// Render detected ecosystems as a comma-separated list of distinct names in detection order, for example
        /// "Node, Container". Empty for null or no ecosystems.
        /// </summary>
        /// <param name="ecosystems">Detected ecosystems.</param>
        /// <returns>Comma-separated names; never null.</returns>
        public static string Format(IEnumerable<DetectedEcosystem>? ecosystems)
        {
            if (ecosystems == null) return String.Empty;
            List<string> names = new List<string>();
            foreach (DetectedEcosystem detected in ecosystems)
            {
                if (detected == null) continue;
                string name = detected.Ecosystem.ToString();
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }

            return String.Join(", ", names);
        }

        /// <summary>
        /// Read the script names a package.json or composer.json declares. Returns an empty set for a missing,
        /// unreadable or malformed file.
        /// </summary>
        /// <param name="manifestPath">Absolute path of the manifest.</param>
        /// <returns>Script names (case-sensitive); never null.</returns>
        public static HashSet<string> ReadManifestScripts(string manifestPath)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            RepoScriptManifest? manifest = ReadManifest(manifestPath);
            if (manifest?.Scripts == null) return names;
            foreach (string key in manifest.Scripts.Keys) names.Add(key);
            return names;
        }

        #endregion

        #region Private-Methods

        private static List<string> ListSubdirectories(string root)
        {
            List<string> names = new List<string>();
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (String.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (_IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    names.Add(name);
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static List<DetectedEcosystem>? DetectInDirectory(string directory, string relative)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in Directory.GetFiles(directory)) files.Add(Path.GetFileName(file));
                foreach (string dir in Directory.GetDirectories(directory)) dirs.Add(Path.GetFileName(dir));
            }
            catch (Exception)
            {
                return null;
            }

            List<DetectedEcosystem> found = new List<DetectedEcosystem>();

            List<string> dotnet = files.Where(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (dotnet.Count > 0) found.Add(New(VesselEcosystemEnum.DotNet, "dotnet", relative, dotnet));

            if (files.Contains("pom.xml"))
            {
                bool wrapper = files.Contains("mvnw");
                found.Add(New(VesselEcosystemEnum.Maven, wrapper ? "mvnw" : "mvn", relative, Present(files, "pom.xml", "mvnw", "mvnw.cmd")));
            }

            if (files.Contains("build.gradle") || files.Contains("build.gradle.kts"))
            {
                bool wrapper = files.Contains("gradlew") || files.Contains("gradlew.bat");
                found.Add(New(VesselEcosystemEnum.Gradle, wrapper ? "gradlew" : "gradle", relative,
                    Present(files, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradlew", "gradlew.bat")));
            }

            if (files.Contains("package.json"))
            {
                List<string> markers = Present(files, "package.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock", "package-lock.json", "npm-shrinkwrap.json");
                found.Add(New(VesselEcosystemEnum.Node, SelectNodeTool(files, Path.Combine(directory, "package.json")), relative, markers));
            }

            List<string> requirements = files.Where(f => _RequirementsPattern.IsMatch(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Contains("pyproject.toml") || files.Contains("setup.py") || requirements.Count > 0)
            {
                string tool = files.Contains("uv.lock") ? "uv" : (files.Contains("poetry.lock") ? "poetry" : "pip");
                List<string> markers = Present(files, "pyproject.toml", "setup.py", "uv.lock", "poetry.lock");
                markers.AddRange(requirements);
                found.Add(New(VesselEcosystemEnum.Python, tool, relative, markers));
            }

            if (files.Contains("Gemfile"))
            {
                List<string> markers = Present(files, "Gemfile", "Gemfile.lock", ".rspec");
                if (dirs.Contains("spec")) markers.Add("spec");
                found.Add(New(VesselEcosystemEnum.Ruby, "bundler", relative, markers));
            }

            if (files.Contains("go.mod")) found.Add(New(VesselEcosystemEnum.Go, "go", relative, Present(files, "go.mod", "go.sum")));
            if (files.Contains("Cargo.toml")) found.Add(New(VesselEcosystemEnum.Rust, "cargo", relative, Present(files, "Cargo.toml", "Cargo.lock")));
            if (files.Contains("composer.json")) found.Add(New(VesselEcosystemEnum.Php, "composer", relative, Present(files, "composer.json", "composer.lock")));

            // A Makefile is a C/C++ marker only when nothing else claims the directory: Go, Node and Python repos
            // commonly keep a Makefile as a task runner, which the resolver reports as a repo script instead.
            bool hasMakefile = files.Contains("Makefile") || files.Contains("GNUmakefile");
            string? cppTool = files.Contains("CMakeLists.txt") ? "cmake" : (files.Contains("meson.build") ? "meson" : null);
            if (cppTool == null && hasMakefile && found.Count == 0) cppTool = "make";
            if (cppTool != null)
            {
                found.Add(New(VesselEcosystemEnum.Cpp, cppTool, relative,
                    Present(files, "CMakeLists.txt", "meson.build", "Makefile", "GNUmakefile", "vcpkg.json", "conanfile.txt", "conanfile.py")));
            }

            if (files.Contains("Package.swift")) found.Add(New(VesselEcosystemEnum.Swift, "swift", relative, Present(files, "Package.swift", "Package.resolved")));

            if (files.Contains("pubspec.yaml"))
            {
                string tool = IsFlutter(Path.Combine(directory, "pubspec.yaml")) ? "flutter" : "dart";
                found.Add(New(VesselEcosystemEnum.Dart, tool, relative, Present(files, "pubspec.yaml", "pubspec.lock")));
            }

            if (files.Contains("mix.exs")) found.Add(New(VesselEcosystemEnum.Elixir, "mix", relative, Present(files, "mix.exs", "mix.lock")));

            if (files.Contains("Dockerfile") || files.Contains("Containerfile"))
            {
                found.Add(New(VesselEcosystemEnum.Container, "docker", relative, Present(files, "Dockerfile", "Containerfile")));
            }

            return found;
        }

        private static DetectedEcosystem New(VesselEcosystemEnum ecosystem, string tool, string relative, List<string> markers)
        {
            return new DetectedEcosystem
            {
                Ecosystem = ecosystem,
                Tool = tool,
                RootPath = relative.Replace('\\', '/'),
                Markers = markers
            };
        }

        private static List<string> Present(HashSet<string> files, params string[] names)
        {
            List<string> present = new List<string>();
            foreach (string name in names)
            {
                if (files.Contains(name)) present.Add(name);
            }

            return present;
        }

        private static string SelectNodeTool(HashSet<string> files, string packageJsonPath)
        {
            if (files.Contains("pnpm-lock.yaml")) return "pnpm";
            if (files.Contains("yarn.lock")) return "yarn";
            if (files.Contains("bun.lockb") || files.Contains("bun.lock")) return "bun";
            if (files.Contains("package-lock.json") || files.Contains("npm-shrinkwrap.json")) return "npm";

            // No lockfile: honor the corepack "packageManager" field (for example "pnpm@9.1.0") when readable.
            RepoScriptManifest? manifest = ReadManifest(packageJsonPath);
            string? declared = manifest?.PackageManager;
            if (!String.IsNullOrWhiteSpace(declared))
            {
                string name = declared.Split('@')[0].Trim().ToLowerInvariant();
                if (name == "pnpm" || name == "yarn" || name == "bun" || name == "npm") return name;
            }

            return "npm";
        }

        private static bool IsFlutter(string pubspecPath)
        {
            try
            {
                return _FlutterPattern.IsMatch(File.ReadAllText(pubspecPath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static RepoScriptManifest? ReadManifest(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<RepoScriptManifest>(File.ReadAllText(path), _JsonOptions);
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}
