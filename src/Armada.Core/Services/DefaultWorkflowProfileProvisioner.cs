namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Gives every vessel a workflow profile out of the box. A vessel that no profile applies to (none scoped to it, its
    /// fleet, or every vessel) gets one built from the matching <see cref="WorkflowProfileTemplates"/> template, chosen
    /// from the repository's files: the vessel's working directory, else its bare clone, else a shallow clone of its
    /// repository URL that fetches the file list but no file contents. A vessel Armada could not recognize gets the
    /// generic profile (only a whitespace and conflict-marker check), which is replaced once its toolchain can be recognized. Profiles a person created
    /// or edited are never touched. Run on startup and after a vessel is created. Thread-safe; one run at a time.
    /// </summary>
    public class DefaultWorkflowProfileProvisioner
    {
        #region Private-Members

        private const int _MaxPaths = 50000;
        private const int _CloneTimeoutMs = 120000;
        private readonly string _Header = "[DefaultWorkflowProfiles] ";
        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _Profiles;
        private readonly IHostCommandExecutor _Commands;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database.</param>
        /// <param name="profiles">Workflow profile service (resolution).</param>
        /// <param name="logging">Logging.</param>
        /// <param name="commands">Executor for git, or null for the local host.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        public DefaultWorkflowProfileProvisioner(DatabaseDriver database, WorkflowProfileService profiles, LoggingModule logging, IHostCommandExecutor? commands = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Commands = commands ?? new LocalHostCommandExecutor();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Give every vessel without an applicable workflow profile one, and upgrade generic ones Armada can now recognize.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of profiles created or upgraded.</returns>
        public async Task<int> SweepAsync(CancellationToken token = default)
        {
            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            int changed = 0;
            foreach (Vessel vessel in vessels)
            {
                token.ThrowIfCancellationRequested();
                if (await EnsureAsync(vessel, token).ConfigureAwait(false) != null) changed++;
            }

            if (changed > 0) _Logging.Info(_Header + "created or upgraded " + changed + " workflow profile(s) for " + vessels.Count + " vessel(s)");
            return changed;
        }

        /// <summary>
        /// Make sure a workflow profile applies to a vessel: create one from the matching template when none does, or
        /// upgrade the vessel's generic profile when its toolchain is now recognizable.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The profile created or upgraded, or null when nothing changed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="vessel"/> is null.</exception>
        public async Task<WorkflowProfile?> EnsureAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            await _Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                AuthContext system = new AuthContext { IsAuthenticated = true, IsAdmin = true, TenantId = vessel.TenantId };
                WorkflowProfile? resolved = await _Profiles.ResolveForVesselAsync(system, vessel, null, token).ConfigureAwait(false);
                bool ownGeneric = resolved != null
                    && String.Equals(resolved.SourceTemplate, WorkflowProfileTemplates.Generic, StringComparison.Ordinal)
                    && resolved.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(resolved.VesselId, vessel.Id, StringComparison.Ordinal);
                if (resolved != null && !ownGeneric) return null;

                RepositoryLayout? layout = await ReadLayoutAsync(vessel, token).ConfigureAwait(false);
                string key = WorkflowProfileTemplates.Match(layout);
                WorkflowProfile built = WorkflowProfileTemplates.Build(vessel, layout, key);

                if (ownGeneric)
                {
                    if (key == WorkflowProfileTemplates.Generic) return null;
                    built.Id = resolved!.Id;
                    built.CreatedUtc = resolved.CreatedUtc;
                    built.LastUpdateUtc = DateTime.UtcNow;
                    _Logging.Info(_Header + "vessel " + vessel.Id + " now recognized as " + key + "; upgrading its generic profile");
                    return await _Database.WorkflowProfiles.UpdateAsync(built, token).ConfigureAwait(false);
                }

                _Logging.Info(_Header + "vessel " + vessel.Id + " (" + vessel.Name + ") had no workflow profile; created one from the " + key + " template");
                return await _Database.WorkflowProfiles.CreateAsync(built, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not provision a workflow profile for vessel " + vessel.Id + ": " + ex.Message);
                return null;
            }
            finally
            {
                _Gate.Release();
            }
        }

        /// <summary>
        /// Read a vessel's file list: its working directory on this host, else its bare clone, else a shallow clone of
        /// its repository URL without file contents. Null when none could be read.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Layout, or null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="vessel"/> is null.</exception>
        public async Task<RepositoryLayout?> ReadLayoutAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            if (!String.IsNullOrWhiteSpace(vessel.WorkingDirectory) && Directory.Exists(vessel.WorkingDirectory))
            {
                List<string> files = ListWorkingDirectory(vessel.WorkingDirectory!);
                if (files.Count > 0) return new RepositoryLayout(files, "the working directory");
            }

            if (!String.IsNullOrWhiteSpace(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
            {
                List<string>? tree = await ListTreeAsync(vessel.LocalPath!, Ref(vessel), token).ConfigureAwait(false);
                if (tree != null && tree.Count > 0) return new RepositoryLayout(tree, "the bare clone");
            }

            if (!String.IsNullOrWhiteSpace(vessel.RepoUrl) && !vessel.RepoUrl!.Contains("example.invalid", StringComparison.OrdinalIgnoreCase))
            {
                string temp = Path.Combine(Path.GetTempPath(), "armada-layout-" + Guid.NewGuid().ToString("N"));
                try
                {
                    HostCommandResult clone = await _Commands.RunAsync(new HostCommandRequest
                    {
                        Executable = "git",
                        Arguments = new List<string> { "clone", "--depth", "1", "--filter=blob:none", "--no-checkout", "--quiet", vessel.RepoUrl!, temp },
                        WorkingDirectory = String.Empty,
                        TimeoutMs = _CloneTimeoutMs
                    }, token).ConfigureAwait(false);
                    if (clone.ExitCode == 0)
                    {
                        List<string>? tree = await ListTreeAsync(temp, "HEAD", token).ConfigureAwait(false);
                        if (tree != null && tree.Count > 0) return new RepositoryLayout(tree, "the repository");
                    }
                    else
                    {
                        _Logging.Debug(_Header + "could not read " + vessel.RepoUrl + ": " + (clone.StandardError ?? "").Trim());
                    }
                }
                finally
                {
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                }
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private static string Ref(Vessel vessel)
        {
            return String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "HEAD" : vessel.DefaultBranch!;
        }

        private async Task<List<string>?> ListTreeAsync(string repoPath, string reference, CancellationToken token)
        {
            HostCommandResult result = await _Commands.RunAsync(new HostCommandRequest
            {
                Executable = "git",
                Arguments = new List<string> { "-C", repoPath, "ls-tree", "-r", "--name-only", reference },
                WorkingDirectory = String.Empty,
                TimeoutMs = 60000
            }, token).ConfigureAwait(false);
            if (result.ExitCode != 0 && reference != "HEAD")
                return await ListTreeAsync(repoPath, "HEAD", token).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            return (result.StandardOutput ?? String.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(_MaxPaths).ToList();
        }

        private static List<string> ListWorkingDirectory(string root)
        {
            List<string> files = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && files.Count < _MaxPaths)
            {
                string dir = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir))
                    {
                        files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                        if (files.Count >= _MaxPaths) break;
                    }

                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string name = Path.GetFileName(sub);
                        if (name == ".git" || name == "node_modules" || name == "bin" || name == "obj") continue;
                        pending.Push(sub);
                    }
                }
                catch (Exception)
                {
                    // An unreadable directory is skipped.
                }
            }

            return files;
        }

        #endregion
    }
}
