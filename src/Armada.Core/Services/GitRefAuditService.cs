namespace Armada.Core.Services
{
    using System.Diagnostics;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>Collect committed Git ref deletions made outside the platform Git API.</summary>
    public class GitRefAuditService
    {
        private const string _Marker = "# Armada ref transaction audit v1";
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>Construct a collector for registered vessel repositories.</summary>
        /// <param name="database">Event and entity store.</param>
        /// <param name="settings">Repository locations.</param>
        /// <param name="logging">Named audit failure reporting.</param>
        public GitRefAuditService(DatabaseDriver database, ArmadaSettings settings, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>Install the hook and collect a bounded batch from each existing repository.</summary>
        /// <param name="token">Cancellation token.</param>
        public async Task SweepAsync(CancellationToken token = default)
        {
            foreach (Vessel vessel in await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false))
            {
                string path = vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
                if (!Directory.Exists(path)) continue;
                try
                {
                    string common = await InstallAsync(path, token).ConfigureAwait(false);
                    await CollectAsync(vessel, common, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _Logging.Warn("[GitRefAuditService] ref_audit_unavailable for " + vessel.Id + ": " + ex.Message);
                }
            }
        }

        /// <summary>Install without replacing an operator-owned hook; return the common Git directory.</summary>
        /// <param name="repository">Registered repository or linked worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The absolute common Git directory.</returns>
        public static async Task<string> InstallAsync(string repository, CancellationToken token = default)
        {
            string common = await GitAsync(repository, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            string hooks = await GitAsync(repository, token, "rev-parse", "--path-format=absolute", "--git-path", "hooks").ConfigureAwait(false);
            Directory.CreateDirectory(hooks);
            string hook = Path.Combine(hooks, "reference-transaction");
            if (File.Exists(hook))
            {
                string existing = await File.ReadAllTextAsync(hook, token).ConfigureAwait(false);
                if (!existing.Contains(_Marker, StringComparison.Ordinal))
                    throw new InvalidOperationException("ref_audit_hook_conflict: " + hook);
                if (existing == _Hook) return common;
            }
            Directory.CreateDirectory(Path.Combine(common, "armada-ref-audit"));
            string temporary = hook + "." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, _Hook, new UTF8Encoding(false), token).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(temporary, hook, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return common;
        }

        /// <summary>Write mission attribution outside tracked worktree files.</summary>
        /// <param name="worktree">Provisioned mission worktree.</param>
        /// <param name="missionId">Mission identifier, when assigned.</param>
        /// <param name="captainId">Assigned captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task SetContextAsync(string worktree, string? missionId, string captainId, CancellationToken token = default)
        {
            string gitDirectory = await GitAsync(worktree, token, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(gitDirectory, "armada-ref-context"),
                (missionId ?? String.Empty) + "\n" + captainId + "\n", token).ConfigureAwait(false);
        }

        private async Task CollectAsync(Vessel vessel, string common, CancellationToken token)
        {
            string spool = Path.Combine(common, "armada-ref-audit");
            if (!Directory.Exists(spool)) return;
            foreach (string file in Directory.EnumerateFiles(spool, "*.ready").OrderBy(path => path, StringComparer.Ordinal).Take(200))
            {
                token.ThrowIfCancellationRequested();
                FileInfo info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 16384)
                {
                    _Logging.Warn("[GitRefAuditService] ref_audit_record_rejected: " + info.Name);
                    File.Move(file, file + ".rejected");
                    continue;
                }
                string[] fields = (await File.ReadAllTextAsync(file, token).ConfigureAwait(false)).Split('\0');
                if (fields.Length != 10 || fields[0] != "1" || !fields[3].StartsWith("refs/", StringComparison.Ordinal)
                    || (fields[2].Length != 40 && fields[2].Length != 64) || !fields[2].All(Uri.IsHexDigit)
                    || !DateTime.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime observed))
                {
                    _Logging.Warn("[GitRefAuditService] ref_audit_record_invalid: " + info.Name);
                    File.Move(file, file + ".rejected");
                    continue;
                }
                string id = "evt_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(common + "\n" + Path.GetFileName(file)))).ToLowerInvariant()[..24];
                if (await _Database.Events.ReadAsync(id, token).ConfigureAwait(false) == null)
                {
                    Mission? mission = String.IsNullOrEmpty(fields[7]) ? null : await _Database.Missions.ReadAsync(fields[7], token).ConfigureAwait(false);
                    bool attributed = mission != null && mission.VesselId == vessel.Id && mission.CaptainId == fields[8];
                    ArmadaEvent record = new ArmadaEvent("git.ref_deleted",
                        "Git transaction deleted " + fields[3] + " by uid " + fields[5] + " pid " + fields[6]
                        + (attributed ? " mission " + mission!.Id : " (no verified mission attribution)"));
                    record.Id = id;
                    record.TenantId = vessel.TenantId;
                    record.UserId = vessel.UserId;
                    record.VesselId = vessel.Id;
                    record.MissionId = attributed ? mission!.Id : null;
                    record.CaptainId = attributed ? mission!.CaptainId : null;
                    record.VoyageId = attributed ? mission!.VoyageId : null;
                    record.EntityType = "git_ref";
                    record.EntityId = fields[3];
                    record.CreatedUtc = observed;
                    record.Payload = JsonSerializer.Serialize(new
                    {
                        source = "git_reference_transaction", old_sha = fields[2], reference = fields[3],
                        working_directory = fields[4], uid = fields[5], git_pid = fields[6],
                        mission_attributed = attributed
                    });
                    await _Database.Events.CreateAsync(record, token).ConfigureAwait(false);
                }
                File.Delete(file);
            }
        }

        private static async Task<string> GitAsync(string path, CancellationToken token, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                WorkingDirectory = path, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("git_start_failed"))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(token);
                Task<string> error = process.StandardError.ReadToEndAsync(token);
                try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                string result = await output.ConfigureAwait(false);
                string reason = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("ref_audit_git_failed: " + reason.Trim());
                return result.Trim();
            }
        }

        private const string _Hook = """
#!/bin/sh
# Armada ref transaction audit v1
case "$1" in prepared|committed|aborted) ;; *) exit 0 ;; esac
set -eu
common=$(git rev-parse --path-format=absolute --git-common-dir)
spool="$common/armada-ref-audit"
mkdir -p "$spool"
while read -r old new ref; do
  case "$new" in ''|*[!0]*) continue ;; esac
  case "$ref" in refs/*) ;; *) continue ;; esac
  key=$(printf '%s' "$ref" | git hash-object --stdin)
  pointer="$spool/prepared.$PPID.$key"
  if [ "$1" = prepared ]; then
    # Git can report an all-zero old value for a deletion without an expected tip.
    # Resolve it while the ref still exists, before the transaction commits.
    case "$old" in *[!0]*) ;; *) old=$(git rev-parse --verify "$ref" 2>/dev/null) || continue ;; esac
    gitdir=$(git rev-parse --absolute-git-dir)
    mission=''
    captain=''
    if [ -f "$gitdir/armada-ref-context" ]; then
      { IFS= read -r mission || :; IFS= read -r captain || :; } < "$gitdir/armada-ref-context"
    fi
    temporary=$(mktemp "$spool/transaction.XXXXXXXXXXXX")
    printf '%s\0' 1 "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$old" "$ref" "$PWD" "$(id -u)" "$PPID" "$mission" "$captain" > "$temporary"
    printf '%s\n' "${temporary##*/}" > "$pointer"
  elif [ -f "$pointer" ]; then
    IFS= read -r basename < "$pointer"
    case "$basename" in transaction.*) ;; *) echo 'ref_audit_invalid_pointer' >&2; exit 1 ;; esac
    case "$basename" in */*) echo 'ref_audit_invalid_pointer' >&2; exit 1 ;; esac
    if [ "$1" = committed ]; then
      mv "$spool/$basename" "$spool/$basename.ready"
    else
      rm -f "$spool/$basename"
    fi
    rm -f "$pointer"
  fi
done
""" + "\n";
    }
}
