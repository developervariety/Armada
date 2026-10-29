namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;

    /// <summary>
    /// Suggests build, test and lint commands from marker files in a vessel working directory.
    /// The scan reads the checkout only. It does not create or change a workflow profile or a vessel command.
    /// </summary>
    public static class VesselToolchainDetector
    {
        private const int MaxFiles = 2000;
        private const int MaxDepth = 2;

        private static readonly HashSet<string> _SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "bower_components", "vendor", "packages", "target", "bin", "obj", "dist", "build", "__pycache__"
        };

        private static readonly EcosystemRule[] _Rules = new EcosystemRule[]
        {
            new EcosystemRule("DotNet", "dotnet build", "dotnet test", "dotnet format --verify-no-changes", IsDotNetMarker),
            new EcosystemRule("Node", "npm run build", "npm test", "npm run lint", name => name.Equals("package.json", StringComparison.OrdinalIgnoreCase)),
            new EcosystemRule("Python", "python -m compileall .", "python -m pytest", "ruff check .", IsPythonMarker),
            new EcosystemRule("Go", "go build ./...", "go test ./...", "go vet ./...", name => name.Equals("go.mod", StringComparison.OrdinalIgnoreCase)),
            new EcosystemRule("Rust", "cargo build", "cargo test", "cargo clippy --all-targets -- -D warnings", name => name.Equals("Cargo.toml", StringComparison.Ordinal))
        };

        /// <summary>
        /// Read marker files and return suggested commands. A missing directory is not readable.
        /// The result never includes the directory path.
        /// </summary>
        /// <param name="workingDirectory">Vessel working directory. Null or blank is not readable.</param>
        /// <returns>The suggestion.</returns>
        public static ToolchainSuggestion Detect(string? workingDirectory)
        {
            ToolchainSuggestion suggestion = new ToolchainSuggestion();
            if (String.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                suggestion.Reason = "The vessel has no readable working directory.";
                return suggestion;
            }

            string root;
            try
            {
                root = Path.GetFullPath(workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is IOException)
            {
                suggestion.Reason = "The working directory could not be read.";
                return suggestion;
            }

            List<string> files = new List<string>();
            bool truncated = false;
            try
            {
                Walk(root, 0, files, ref truncated);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                suggestion.Reason = "The working directory could not be read.";
                return suggestion;
            }

            suggestion.CheckoutReadable = true;
            suggestion.Truncated = truncated;
            foreach (EcosystemRule rule in _Rules)
            {
                List<string> markers = new List<string>();
                foreach (string file in files)
                {
                    if (!rule.Matches(Path.GetFileName(file))) continue;
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") continue;
                    markers.Add(relative);
                }

                if (markers.Count == 0) continue;
                markers.Sort(StringComparer.Ordinal);
                suggestion.Ecosystems.Add(new ToolchainEcosystemSuggestion
                {
                    Ecosystem = rule.Name,
                    Markers = markers,
                    BuildCommand = rule.BuildCommand,
                    TestCommand = rule.TestCommand,
                    LintCommand = rule.LintCommand
                });
            }

            if (suggestion.Ecosystems.Count == 0)
            {
                suggestion.Reason = truncated
                    ? "No supported ecosystem marker was found before the scan stopped."
                    : "No supported ecosystem marker was found.";
            }

            return suggestion;
        }

        private static void Walk(string directory, int depth, List<string> files, ref bool truncated)
        {
            if (truncated) return;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            List<string> subdirectories = new List<string>();
            foreach (string entry in children)
            {
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    return;
                }

                if (IsDirectory(entry))
                {
                    string name = Path.GetFileName(entry);
                    if (name.StartsWith(".", StringComparison.Ordinal) || _SkippedDirectories.Contains(name)) continue;
                    if (IsReparsePoint(entry)) continue;
                    subdirectories.Add(entry);
                    continue;
                }

                files.Add(entry);
            }

            if (depth >= MaxDepth) return;
            foreach (string subdirectory in subdirectories)
            {
                Walk(subdirectory, depth + 1, files, ref truncated);
                if (truncated) return;
            }
        }

        private static bool IsDotNetMarker(string name)
        {
            if (name.Equals("global.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)) return true;
            string extension = Path.GetExtension(name);
            return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectory(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return true;
            }
        }

        private static bool IsPythonMarker(string name)
        {
            return name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("setup.py", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("setup.cfg", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class EcosystemRule
        {
            internal EcosystemRule(string name, string buildCommand, string testCommand, string lintCommand, Func<string, bool> matches)
            {
                Name = name;
                BuildCommand = buildCommand;
                TestCommand = testCommand;
                LintCommand = lintCommand;
                Matches = matches;
            }

            internal string Name { get; }
            internal string BuildCommand { get; }
            internal string TestCommand { get; }
            internal string LintCommand { get; }
            internal Func<string, bool> Matches { get; }
        }
    }
}
