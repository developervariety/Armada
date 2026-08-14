namespace Test.Shared.Infrastructure
{
    using System;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Creates disposable, fully-valid local git repositories for service tests that shell out to real
    /// git. Every call yields a fresh, isolated working tree (and, when requested, a bare clone) that is
    /// byte-for-byte identical to a prepared template.
    ///
    /// Many service suites rebuild the same base repository -- <c>git init -b main</c>, user config, a
    /// single <c>README.md</c> "Initial commit" -- before each case's real work. That boilerplate costs
    /// six or seven git process spawns (~2s) per case. Instead, the prepared layout is built exactly once
    /// into a template directory, and each repository is a fast recursive directory copy of that template
    /// (~milliseconds). Because the entire <c>.git</c> directory is copied and a plain repository stores
    /// no absolute paths in its metadata, every copy is a fully independent, valid git repository with a
    /// <c>main</c> branch pointing at one "Initial commit".
    /// </summary>
    public static class TestGitRepoHelper
    {
        #region Private-Members

        private static readonly object _TemplateLock = new object();
        private static string? _TemplateRoot;

        private const string _WorkingSubdirectory = "work";
        private const string _BareSubdirectory = "bare.git";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy the prepared working repository into a fresh, tracked temp directory and return its path.
        /// The result is an independent git repository on branch <c>main</c> with a single "Initial commit"
        /// that adds <c>README.md</c>, and with test user name/email already configured so further commits
        /// succeed without additional setup.
        /// </summary>
        /// <returns>The absolute path to the copied working repository.</returns>
        public static string CreateWorkingRepoCopy()
        {
            string template = EnsureTemplate();
            string destination = TestTemp.NewDirectory("gitrepo");
            CopyDirectory(Path.Combine(template, _WorkingSubdirectory), destination);
            return destination;
        }

        /// <summary>
        /// Copy the prepared bare repository into a fresh, tracked temp directory and return its path. The
        /// result is an independent bare git repository cloned from the same working repository that
        /// <see cref="CreateWorkingRepoCopy"/> produces, so its <c>main</c> branch points at the identical
        /// "Initial commit".
        /// </summary>
        /// <returns>The absolute path to the copied bare repository.</returns>
        public static string CreateBareRepoCopy()
        {
            string template = EnsureTemplate();
            string destination = TestTemp.NewDirectory("gitbare");
            CopyDirectory(Path.Combine(template, _BareSubdirectory), destination);
            return destination;
        }

        #endregion

        #region Private-Methods

        private static string EnsureTemplate()
        {
            string? existing = _TemplateRoot;
            if (existing != null && Directory.Exists(existing)) return existing;

            lock (_TemplateLock)
            {
                if (_TemplateRoot != null && Directory.Exists(_TemplateRoot)) return _TemplateRoot;

                string root = TestTemp.NewDirectory("gitrepo_template");
                string workingDirectory = Path.Combine(root, _WorkingSubdirectory);
                string bareDirectory = Path.Combine(root, _BareSubdirectory);

                Directory.CreateDirectory(workingDirectory);
                RunGit(workingDirectory, "init", "-b", "main");
                RunGit(workingDirectory, "config", "user.name", "Armada Tests");
                RunGit(workingDirectory, "config", "user.email", "armada-tests@example.com");
                File.WriteAllText(Path.Combine(workingDirectory, "README.md"), "hello\n");
                RunGit(workingDirectory, "add", "README.md");
                RunGit(workingDirectory, "commit", "-m", "Initial commit");
                RunGit(root, "clone", "--bare", workingDirectory, bareDirectory);

                _TemplateRoot = root;
                return root;
            }
        }

        private static void RunGit(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = new Process { StartInfo = startInfo };
            process.Start();

            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "git " + String.Join(" ", arguments) + " failed (exit " + process.ExitCode + "): " + standardError.Trim());
            }
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                File.Copy(file, destinationFile, true);
            }

            foreach (string subdirectory in Directory.GetDirectories(sourceDirectory))
            {
                string destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(subdirectory));
                CopyDirectory(subdirectory, destinationSubdirectory);
            }
        }

        #endregion
    }
}
