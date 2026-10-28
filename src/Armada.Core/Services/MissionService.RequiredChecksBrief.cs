namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<string> BuildRequiredChecksBriefAsync(Mission mission, Vessel vessel, CancellationToken token)
        {
            const string rules = "The platform owns native Check records and applies their full workflow environment. "
                + "The configured validation commands below are a reference, not additional stage requirements. "
                + "Run only validation required by your mission and repository instructions. "
                + "Do not execute a redacted or truncated command. Slop runs in the platform; no dock binary is required. "
                + "For missing inputs, report their names and all resulting skips; a manual run without them is not full-profile proof. "
                + "Do not read operator settings, credentials or operator logs to discover input values.\n\n";
            StringBuilder section = new StringBuilder("## Native Required Checks\n\n" + rules);
            try
            {
                AuthContext owner = AuthContext.Authenticated(mission.TenantId ?? vessel.TenantId ?? "default",
                    mission.UserId ?? vessel.UserId ?? "default", true, true, "system");
                WorkflowProfile? profile = await new WorkflowProfileService(_Database, _Logging)
                    .ResolveForVesselAsync(owner, vessel, token: token).ConfigureAwait(false);
                if (profile == null) return String.Empty;
                string label = String.Concat(RedactProfileCommand(profile.Name, profile.EnvironmentVariables)
                    .Select(character => Char.IsControl(character) ? ' ' : character));
                section.Append("Profile: ").Append(label.Length <= 160 ? label : label.Substring(0, 160) + " [TRUNCATED]").Append("\n\n");
                foreach (WorkflowProfileCommandPreview preview in WorkflowProfileService.BuildCommandPreviews(profile)
                    .Where(command => IsCaptainValidationCommand(command.CheckType)).Take(5))
                {
                    string command = RedactProfileCommand(preview.Command, profile.EnvironmentVariables);
                    section.Append(preview.CheckType).Append(": ").Append(command.Length <= 600
                        ? command : command.Substring(0, 600) + " [TRUNCATED; do not execute]").Append('\n');
                }
                List<string> names = (profile.EnvironmentVariables ?? new Dictionary<string, string>()).Keys
                    .OrderBy(name => name, StringComparer.Ordinal).ToList();
                section.Append("\nConfigured environment names (values withheld): ");
                section.Append(String.Join(", ", names.Take(16).Select(name => SafeEnvironmentName(name))));
                if (names.Count > 16) section.Append(" [additional names omitted]");
                section.Append('\n');
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "could not render required-check reference: " + exception.GetType().Name);
                section.Append("The workflow reference is unavailable; report this instead of reading operator state.\n");
            }
            return section.ToString();
        }

        private static bool IsCaptainValidationCommand(CheckRunTypeEnum type) => type == CheckRunTypeEnum.Lint
            || type == CheckRunTypeEnum.Build || type == CheckRunTypeEnum.UnitTest
            || type == CheckRunTypeEnum.IntegrationTest || type == CheckRunTypeEnum.E2ETest;

        private static string RedactProfileCommand(string command, Dictionary<string, string>? environment)
        {
            // Fail closed for common inline credential forms before introducing a new model-visible surface.
            if (Regex.IsMatch(command,
                @"authorization|password|passwd|\bpwd\b|secret|token|api[_-]?key|credential|bearer|private[_-]?key|access[_-]?key|\blogin\b|(?:^|\s)(?:-[uHbUpEeK]|--(?:user|header|cookie|proxy-user))|://[^/\s]*@",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return "[WITHHELD: credential-bearing native command; do not execute]";
            string result = command;
            foreach (string value in (environment ?? new Dictionary<string, string>()).Values
                .Where(value => !String.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
                result = result.Replace(value, "[PROFILE_VALUE; do not execute]", StringComparison.Ordinal);
            return SecretRedactor.Redact(result);
        }

        private static string SafeEnvironmentName(string name)
        {
            if (name.Length == 0 || name.Length > 128 || !(Char.IsAsciiLetter(name[0]) || name[0] == '_')
                || name.Any(character => !(Char.IsAsciiLetterOrDigit(character) || character == '_')))
                return "[invalid environment name omitted]";
            return name;
        }
    }
}
