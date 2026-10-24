namespace Armada.Core.Harbor
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Enums;

    /// <summary>Stable host-configured verification command keys used by checks and definition-of-done gates.</summary>
    public static class HarborVerificationProtocol
    {
        /// <summary>Configured build command key for a definition-of-done gate.</summary>
        public const string DefinitionOfDoneBuild = "dod-build";

        /// <summary>Configured unit-test command key for a definition-of-done gate.</summary>
        public const string DefinitionOfDoneUnitTest = "dod-unit-test";

        /// <summary>Configured isolated unit-test rerun command key for a definition-of-done gate.</summary>
        public const string DefinitionOfDoneUnitTestRerun = "dod-rerun-unit-test";

        /// <summary>Get the fixed configured command key for one structured Check type.</summary>
        /// <param name="type">Check type.</param>
        /// <returns>Fixed host grant key.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The Check type has no command representation.</exception>
        public static string CheckCommandKey(CheckRunTypeEnum type)
        {
            return type switch
            {
                CheckRunTypeEnum.Lint => "check-lint",
                CheckRunTypeEnum.Build => "check-build",
                CheckRunTypeEnum.UnitTest => "check-unit-test",
                CheckRunTypeEnum.IntegrationTest => "check-integration-test",
                CheckRunTypeEnum.E2ETest => "check-e2e-test",
                CheckRunTypeEnum.Migration => "check-migration",
                CheckRunTypeEnum.SecurityScan => "check-security-scan",
                CheckRunTypeEnum.Performance => "check-performance",
                CheckRunTypeEnum.Package => "check-package",
                CheckRunTypeEnum.DeploymentVerification => "check-deployment-verification",
                CheckRunTypeEnum.RollbackVerification => "check-rollback-verification",
                CheckRunTypeEnum.PublishArtifact => "check-publish-artifact",
                CheckRunTypeEnum.ReleaseVersioning => "check-release-versioning",
                CheckRunTypeEnum.Changelog => "check-changelog",
                CheckRunTypeEnum.Deploy => "check-deploy",
                CheckRunTypeEnum.Rollback => "check-rollback",
                CheckRunTypeEnum.SmokeTest => "check-smoke-test",
                CheckRunTypeEnum.HealthCheck => "check-health-check",
                CheckRunTypeEnum.Custom => "check-custom",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "This Check type has no host verification command key.")
            };
        }

        /// <summary>Get the host-configured isolated rerun command key for a supported test Check.</summary>
        /// <param name="type">Test Check type.</param>
        /// <returns>Fixed host grant key.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The Check type has no isolated rerun command.</exception>
        public static string CheckRerunCommandKey(CheckRunTypeEnum type)
        {
            return type switch
            {
                CheckRunTypeEnum.UnitTest => "check-rerun-unit-test",
                CheckRunTypeEnum.IntegrationTest => "check-rerun-integration-test",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "This Check type has no isolated rerun command key.")
            };
        }

        /// <summary>Hash the exact UTF-8 bytes of a configured command for host-side equality checks.</summary>
        /// <param name="command">Already resolved workflow command.</param>
        /// <returns>Lowercase hexadecimal SHA-256 digest.</returns>
        public static string CommandSha256(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) throw new ArgumentException("A verification command is required.", nameof(command));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
        }
    }
}
