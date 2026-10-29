namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Client.Models;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="VesselEcosystemDetector"/>, <see cref="EcosystemCommandCatalog"/>,
    /// <see cref="EcosystemCommandResolver"/>, the new fleet action template variables and the vessel
    /// TestCommand/LintCommand/AuditCommand fields: detection per ecosystem in temp directories, lockfile tool
    /// selection, ignored directories, empty/missing/unreadable directories, malformed manifests, catalog
    /// completeness, POSIX vs Windows variants, resolution order and database round trip.
    /// </summary>
    public sealed class VesselEcosystemSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.VesselEcosystem";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region Detector

            AddSingle(cases, "detect_dotnet_sln", VesselEcosystemEnum.DotNet, "dotnet", "App.sln");
            AddSingle(cases, "detect_dotnet_slnx", VesselEcosystemEnum.DotNet, "dotnet", "App.slnx");
            AddSingle(cases, "detect_dotnet_csproj", VesselEcosystemEnum.DotNet, "dotnet", "App.csproj");
            AddSingle(cases, "detect_dotnet_fsproj", VesselEcosystemEnum.DotNet, "dotnet", "App.fsproj");
            AddSingle(cases, "detect_maven", VesselEcosystemEnum.Maven, "mvn", "pom.xml");
            AddSingle(cases, "detect_maven_wrapper", VesselEcosystemEnum.Maven, "mvnw", "pom.xml", "mvnw");
            AddSingle(cases, "detect_gradle", VesselEcosystemEnum.Gradle, "gradle", "build.gradle");
            AddSingle(cases, "detect_gradle_kts_wrapper", VesselEcosystemEnum.Gradle, "gradlew", "build.gradle.kts", "gradlew");
            AddSingle(cases, "detect_node_default_npm", VesselEcosystemEnum.Node, "npm", "package.json");
            AddSingle(cases, "detect_node_npm_lock", VesselEcosystemEnum.Node, "npm", "package.json", "package-lock.json");
            AddSingle(cases, "detect_node_pnpm", VesselEcosystemEnum.Node, "pnpm", "package.json", "pnpm-lock.yaml");
            AddSingle(cases, "detect_node_yarn", VesselEcosystemEnum.Node, "yarn", "package.json", "yarn.lock");
            AddSingle(cases, "detect_node_bun", VesselEcosystemEnum.Node, "bun", "package.json", "bun.lockb");
            AddSingle(cases, "detect_python_pyproject_pip", VesselEcosystemEnum.Python, "pip", "pyproject.toml");
            AddSingle(cases, "detect_python_requirements", VesselEcosystemEnum.Python, "pip", "requirements-dev.txt");
            AddSingle(cases, "detect_python_setup_py", VesselEcosystemEnum.Python, "pip", "setup.py");
            AddSingle(cases, "detect_python_uv", VesselEcosystemEnum.Python, "uv", "pyproject.toml", "uv.lock");
            AddSingle(cases, "detect_python_poetry", VesselEcosystemEnum.Python, "poetry", "pyproject.toml", "poetry.lock");
            AddSingle(cases, "detect_ruby", VesselEcosystemEnum.Ruby, "bundler", "Gemfile");
            AddSingle(cases, "detect_go", VesselEcosystemEnum.Go, "go", "go.mod");
            AddSingle(cases, "detect_rust", VesselEcosystemEnum.Rust, "cargo", "Cargo.toml");
            AddSingle(cases, "detect_php", VesselEcosystemEnum.Php, "composer", "composer.json");
            AddSingle(cases, "detect_cpp_cmake", VesselEcosystemEnum.Cpp, "cmake", "CMakeLists.txt");
            AddSingle(cases, "detect_cpp_meson", VesselEcosystemEnum.Cpp, "meson", "meson.build");
            AddSingle(cases, "detect_cpp_makefile", VesselEcosystemEnum.Cpp, "make", "Makefile");
            AddSingle(cases, "detect_swift", VesselEcosystemEnum.Swift, "swift", "Package.swift");
            AddSingle(cases, "detect_dart", VesselEcosystemEnum.Dart, "dart", "pubspec.yaml");
            AddSingle(cases, "detect_elixir", VesselEcosystemEnum.Elixir, "mix", "mix.exs");
            AddSingle(cases, "detect_container", VesselEcosystemEnum.Container, "docker", "Dockerfile");

            cases.Add(Case("detect_node_package_manager_field", "Without a lockfile the package.json packageManager field picks the tool", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("package.json", "{ \"name\": \"x\", \"packageManager\": \"yarn@4.1.0\" }");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(1, found.Count);
                    AssertEqual("yarn", found[0].Tool);
                }
            }));

            cases.Add(Case("detect_dart_flutter", "A pubspec.yaml with a flutter section selects flutter", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("pubspec.yaml", "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(1, found.Count);
                    AssertEqual(VesselEcosystemEnum.Dart, found[0].Ecosystem);
                    AssertEqual("flutter", found[0].Tool);
                }
            }));

            cases.Add(Case("detect_makefile_with_other_ecosystem_is_not_cpp", "A Makefile next to go.mod is a task runner, not a C/C++ marker", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("go.mod", "module x\n");
                    repo.Write("Makefile", "test:\n\tgo test ./...\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(1, found.Count);
                    AssertEqual(VesselEcosystemEnum.Go, found[0].Ecosystem);
                }
            }));

            cases.Add(Case("detect_multi_ecosystem", "Several ecosystems at the top level and one level down are all reported with their roots", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("package.json", "{}");
                    repo.Write("pnpm-lock.yaml", "");
                    repo.Write("Dockerfile", "FROM scratch\n");
                    repo.Write("backend/go.mod", "module backend\n");
                    repo.Write("tools/Cargo.toml", "[package]\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(4, found.Count);
                    AssertEqual(VesselEcosystemEnum.Node, found[0].Ecosystem);
                    AssertEqual(".", found[0].RootPath);
                    AssertEqual("pnpm", found[0].Tool);
                    AssertEqual(VesselEcosystemEnum.Container, found[1].Ecosystem);
                    AssertEqual(VesselEcosystemEnum.Go, found[2].Ecosystem);
                    AssertEqual("backend", found[2].RootPath);
                    AssertEqual(VesselEcosystemEnum.Rust, found[3].Ecosystem);
                    AssertEqual("tools", found[3].RootPath);
                    AssertEqual("Node, Container, Go, Rust", VesselEcosystemDetector.Format(found));
                }
            }));

            cases.Add(Case("detect_subdirectory_only_and_duplicate_suppressed", "A module repeating a top-level ecosystem is not reported twice; a nested-only ecosystem is", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("pom.xml", "<project/>");
                    repo.Write("core/pom.xml", "<project/>");
                    repo.Write("web/package.json", "{}");
                    repo.Write("web/yarn.lock", "");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(2, found.Count);
                    AssertEqual(VesselEcosystemEnum.Maven, found[0].Ecosystem);
                    AssertEqual(".", found[0].RootPath);
                    AssertEqual(VesselEcosystemEnum.Node, found[1].Ecosystem);
                    AssertEqual("web", found[1].RootPath);
                    AssertEqual("yarn", found[1].Tool);
                }
            }));

            cases.Add(Case("detect_ignored_directories", "node_modules, bin, obj, vendor, target and dot-directories are never scanned", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("node_modules/package.json", "{}");
                    repo.Write("bin/App.csproj", "<Project/>");
                    repo.Write("obj/App.csproj", "<Project/>");
                    repo.Write("vendor/composer.json", "{}");
                    repo.Write("target/Cargo.toml", "");
                    repo.Write(".git/go.mod", "");
                    repo.Write(".devcontainer/Dockerfile", "FROM scratch\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(0, found.Count, "ignored directories detected: " + VesselEcosystemDetector.Format(found));
                }
            }));

            cases.Add(Case("detect_two_levels_down_not_scanned", "Markers two levels down are out of range", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("services/api/go.mod", "module api\n");
                    AssertEqual(0, VesselEcosystemDetector.Detect(repo.Path).Count);
                }
            }));

            cases.Add(Case("detect_empty_repo", "An empty repository has no ecosystems", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("README.md", "hello");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(0, found.Count);
                    AssertEqual(String.Empty, VesselEcosystemDetector.Format(found));
                }
            }));

            cases.Add(Case("detect_missing_or_null_directory", "A null, blank or missing working directory yields an empty list without throwing", TestTags.Negative, () =>
            {
                AssertEqual(0, VesselEcosystemDetector.Detect(null).Count);
                AssertEqual(0, VesselEcosystemDetector.Detect("   ").Count);
                AssertEqual(0, VesselEcosystemDetector.Detect(Path.Combine(Path.GetTempPath(), "armada-missing-" + Guid.NewGuid().ToString("N"))).Count);
            }));

            cases.Add(Case("detect_working_directory_is_a_file", "A working directory that is a file yields an empty list", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    string file = repo.Write("go.mod", "module x\n");
                    AssertEqual(0, VesselEcosystemDetector.Detect(file).Count);
                }
            }));

            cases.Add(Case("detect_unreadable_directories", "An unreadable subdirectory is skipped and an unreadable root yields an empty list", TestTags.Negative, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("go.mod", "module x\n");
                    repo.Write("locked/Cargo.toml", "");
                    string locked = Path.Combine(repo.Path, "locked");
                    File.SetUnixFileMode(locked, UnixFileMode.None);
                    try
                    {
                        if (CanList(locked)) return; // running as root: permissions are not enforced
                        List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                        AssertEqual(1, found.Count);
                        AssertEqual(VesselEcosystemEnum.Go, found[0].Ecosystem);

                        File.SetUnixFileMode(repo.Path, UnixFileMode.None);
                        AssertEqual(0, VesselEcosystemDetector.Detect(repo.Path).Count);
                    }
                    finally
                    {
                        File.SetUnixFileMode(repo.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
            }));

            cases.Add(Case("detect_malformed_package_json", "A malformed package.json still detects Node with npm and yields no repo scripts", TestTags.Negative, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("package.json", "{ \"scripts\": { \"test\": ");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(1, found.Count);
                    AssertEqual("npm", found[0].Tool);
                    AssertEqual(0, VesselEcosystemDetector.ReadManifestScripts(Path.Combine(repo.Path, "package.json")).Count);

                    ResolvedEcosystemCommand test = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Test, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, test.Source);
                    AssertEqual("npm test", test.Command);
                }
            }));

            #endregion

            #region Catalog

            cases.Add(Case("catalog_every_pair_has_command_or_not_supported", "Every ecosystem and intent has a command or an explicit NotSupported", TestTags.Positive, () =>
            {
                foreach (VesselEcosystemEnum ecosystem in Enum.GetValues<VesselEcosystemEnum>())
                {
                    foreach (EcosystemCommandIntentEnum intent in Enum.GetValues<EcosystemCommandIntentEnum>())
                    {
                        EcosystemCommand entry = EcosystemCommandCatalog.Get(ecosystem, intent);
                        string label = ecosystem + "/" + intent;
                        AssertEqual(intent, entry.Intent, label);
                        if (entry.Supported)
                        {
                            AssertFalse(String.IsNullOrWhiteSpace(entry.PosixCommand), label + " posix");
                            AssertFalse(String.IsNullOrWhiteSpace(entry.WindowsCommand), label + " windows");
                            AssertTrue(entry.RequiredTools.Count > 0, label + " tools");
                            AssertTrue(entry.WindowsRequiredTools.Count > 0, label + " windows tools");
                            if (intent == EcosystemCommandIntentEnum.Audit) AssertFalse(String.IsNullOrEmpty(entry.AuditParserId), label + " parser");
                            else AssertNull(entry.AuditParserId, label + " parser");
                        }
                        else
                        {
                            AssertEqual(String.Empty, entry.PosixCommand, label);
                            AssertEqual(String.Empty, entry.WindowsCommand, label);
                            AssertFalse(String.IsNullOrWhiteSpace(entry.Notes), label + " notes");
                        }
                    }
                }
            }));

            cases.Add(Case("catalog_spec_defaults", "Catalog defaults match the documented commands", TestTags.Positive, () =>
            {
                AssertEqual("dotnet build", Posix(VesselEcosystemEnum.DotNet, EcosystemCommandIntentEnum.Build));
                AssertEqual("dotnet format --verify-no-changes", Posix(VesselEcosystemEnum.DotNet, EcosystemCommandIntentEnum.Lint));
                AssertEqual("dotnet list package --vulnerable --include-transitive", Posix(VesselEcosystemEnum.DotNet, EcosystemCommandIntentEnum.Audit));
                AssertEqual("dotnet list package --outdated", Posix(VesselEcosystemEnum.DotNet, EcosystemCommandIntentEnum.Outdated));
                AssertEqual("mvn -B -DskipTests package", Posix(VesselEcosystemEnum.Maven, EcosystemCommandIntentEnum.Build));
                AssertEqual("mvn -B versions:display-dependency-updates", Posix(VesselEcosystemEnum.Maven, EcosystemCommandIntentEnum.Outdated));
                AssertEqual(EcosystemCommandCatalog.OsvScannerCommand, Posix(VesselEcosystemEnum.Maven, EcosystemCommandIntentEnum.Audit));
                AssertEqual("npm run build --if-present", Posix(VesselEcosystemEnum.Node, EcosystemCommandIntentEnum.Build));
                AssertEqual("pnpm audit", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Node, EcosystemCommandIntentEnum.Audit, "pnpm").PosixCommand);
                AssertEqual("pnpm-audit", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Node, EcosystemCommandIntentEnum.Audit, "pnpm").AuditParserId);
                AssertEqual("pytest", Posix(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Test));
                AssertEqual("ruff check", Posix(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Lint));
                AssertEqual("pip-audit", Posix(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Audit));
                AssertEqual("uv run pytest", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Test, "uv").PosixCommand);
                AssertEqual("poetry show --outdated", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Outdated, "poetry").PosixCommand);
                AssertEqual("bundle exec rake test", Posix(VesselEcosystemEnum.Ruby, EcosystemCommandIntentEnum.Test));
                AssertEqual("bundle exec rspec", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Ruby, EcosystemCommandIntentEnum.Test, "bundler", new[] { ".rspec" }).PosixCommand);
                AssertEqual("bundle-audit check --update", Posix(VesselEcosystemEnum.Ruby, EcosystemCommandIntentEnum.Audit));
                AssertEqual("go vet ./...", Posix(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Lint));
                AssertEqual("govulncheck ./...", Posix(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Audit));
                AssertEqual("go list -m -u all", Posix(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Outdated));
                AssertEqual("cargo clippy -- -D warnings", Posix(VesselEcosystemEnum.Rust, EcosystemCommandIntentEnum.Lint));
                AssertEqual("cargo audit", Posix(VesselEcosystemEnum.Rust, EcosystemCommandIntentEnum.Audit));
                AssertEqual("composer audit", Posix(VesselEcosystemEnum.Php, EcosystemCommandIntentEnum.Audit));
                AssertEqual("composer outdated --direct", Posix(VesselEcosystemEnum.Php, EcosystemCommandIntentEnum.Outdated));
                AssertEqual("cmake -S . -B build && cmake --build build", Posix(VesselEcosystemEnum.Cpp, EcosystemCommandIntentEnum.Build));
                AssertEqual("ctest --test-dir build", Posix(VesselEcosystemEnum.Cpp, EcosystemCommandIntentEnum.Test));
                AssertEqual("swift test", Posix(VesselEcosystemEnum.Swift, EcosystemCommandIntentEnum.Test));
                AssertEqual("flutter test", EcosystemCommandCatalog.Get(VesselEcosystemEnum.Dart, EcosystemCommandIntentEnum.Test, "flutter").PosixCommand);
                AssertEqual("dart pub outdated", Posix(VesselEcosystemEnum.Dart, EcosystemCommandIntentEnum.Outdated));
                AssertEqual("mix format --check-formatted", Posix(VesselEcosystemEnum.Elixir, EcosystemCommandIntentEnum.Lint));
                AssertEqual("mix deps.audit", Posix(VesselEcosystemEnum.Elixir, EcosystemCommandIntentEnum.Audit));
                AssertEqual("hadolint Dockerfile", Posix(VesselEcosystemEnum.Container, EcosystemCommandIntentEnum.Lint));
                AssertEqual("trivy fs .", Posix(VesselEcosystemEnum.Container, EcosystemCommandIntentEnum.Audit));
                AssertEqual(EcosystemCommandCatalog.GitleaksCommand, Posix(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Secrets));
                AssertTrue(EcosystemCommandCatalog.Get(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Audit).RequiresNetwork, "audit needs the network");
                AssertFalse(EcosystemCommandCatalog.Get(VesselEcosystemEnum.Go, EcosystemCommandIntentEnum.Secrets).RequiresNetwork, "gitleaks is offline");
            }));

            cases.Add(Case("catalog_cpp_audit_needs_manifest", "C/C++ audit is NotSupported without vcpkg.json or a conanfile and osv-scanner with one", TestTags.Negative, () =>
            {
                AssertFalse(EcosystemCommandCatalog.Get(VesselEcosystemEnum.Cpp, EcosystemCommandIntentEnum.Audit, "cmake").Supported, "no manifest");
                AssertEqual(EcosystemCommandCatalog.OsvScannerCommand, EcosystemCommandCatalog.Get(VesselEcosystemEnum.Cpp, EcosystemCommandIntentEnum.Audit, "cmake", new[] { "vcpkg.json" }).PosixCommand);
                AssertTrue(EcosystemCommandCatalog.Get(VesselEcosystemEnum.Cpp, EcosystemCommandIntentEnum.Audit, "cmake", new[] { "conanfile.py" }).Supported, "conanfile");

                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("CMakeLists.txt", "");
                    repo.Write("vcpkg.json", "{}");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertTrue(EcosystemCommandCatalog.Get(found[0], EcosystemCommandIntentEnum.Audit).Supported, "detected vcpkg.json marker");
                }
            }));

            cases.Add(Case("catalog_windows_vs_posix", "Windows and POSIX variants differ where the shells do", TestTags.Positive, () =>
            {
                EcosystemCommand gradle = EcosystemCommandCatalog.Get(VesselEcosystemEnum.Gradle, EcosystemCommandIntentEnum.Test, "gradlew");
                AssertEqual("./gradlew test", gradle.GetCommand(false));
                AssertEqual("gradlew.bat test", gradle.GetCommand(true));
                EcosystemCommand plainGradle = EcosystemCommandCatalog.Get(VesselEcosystemEnum.Gradle, EcosystemCommandIntentEnum.Test);
                AssertEqual("gradle test", plainGradle.GetCommand(true));
                EcosystemCommand maven = EcosystemCommandCatalog.Get(VesselEcosystemEnum.Maven, EcosystemCommandIntentEnum.Test, "mvnw");
                AssertEqual("./mvnw -B test", maven.GetCommand(false));
                AssertEqual("mvnw.cmd -B test", maven.GetCommand(true));
                EcosystemCommand python = EcosystemCommandCatalog.Get(VesselEcosystemEnum.Python, EcosystemCommandIntentEnum.Build, "pip");
                AssertEqual("python3 -m build", python.GetCommand(false));
                AssertEqual("python -m build", python.GetCommand(true));
                AssertEqual("python3", python.GetRequiredTools(false)[0]);
                AssertEqual("python", python.GetRequiredTools(true)[0]);
                EcosystemCommand php = EcosystemCommandCatalog.Get(VesselEcosystemEnum.Php, EcosystemCommandIntentEnum.Test);
                AssertEqual("vendor/bin/phpunit", php.GetCommand(false));
                AssertEqual("vendor\\bin\\phpunit", php.GetCommand(true));
            }));

            #endregion

            #region Resolver

            cases.Add(Case("resolve_override_beats_script_beats_default", "Vessel override beats a repo script, which beats the ecosystem default", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("package.json", "{ \"scripts\": { \"test\": \"jest\" } }");
                    repo.Write("pnpm-lock.yaml", "");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);

                    Vessel vessel = new Vessel("web", "https://example.com/web.git");
                    vessel.TestCommand = "make check";
                    ResolvedEcosystemCommand withOverride = EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Test, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.VesselOverride, withOverride.Source);
                    AssertEqual("make check", withOverride.Command);
                    AssertEqual("Vessel.TestCommand", withOverride.Detail);

                    vessel.TestCommand = "   ";
                    ResolvedEcosystemCommand script = EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Test, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.RepoScript, script.Source, "blank override is ignored");
                    AssertEqual("pnpm run test", script.Command);
                    AssertEqual(VesselEcosystemEnum.Node, script.Ecosystem);

                    ResolvedEcosystemCommand lint = EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Lint, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, lint.Source, "no lint script");
                    AssertEqual("pnpm run --if-present lint", lint.Command);
                    AssertEqual("pnpm", lint.RequiredTools[0]);
                }
            }));

            cases.Add(Case("resolve_definition_of_done_fallbacks", "Test falls back to the definition-of-done test command and Build uses the definition-of-done build command", TestTags.Positive, () =>
            {
                Vessel vessel = new Vessel("api", "https://example.com/api.git");
                vessel.DefinitionOfDoneTestCommand = "dotnet test src/App.sln";
                vessel.DefinitionOfDoneBuildCommand = "dotnet build src/App.sln";
                List<DetectedEcosystem> found = new List<DetectedEcosystem> { new DetectedEcosystem { Ecosystem = VesselEcosystemEnum.DotNet, Tool = "dotnet" } };
                ResolvedEcosystemCommand test = EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Test, null, false);
                AssertEqual(EcosystemCommandSourceEnum.VesselOverride, test.Source);
                AssertEqual("dotnet test src/App.sln", test.Command);
                AssertEqual("Vessel.DefinitionOfDoneTestCommand", test.Detail);
                vessel.TestCommand = "dotnet test --filter Unit";
                AssertEqual("dotnet test --filter Unit", EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Test, null, false).Command);
                AssertEqual("dotnet build src/App.sln", EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Build, null, false).Command);
                vessel.AuditCommand = "osv-scanner -r src";
                vessel.LintCommand = "dotnet format whitespace --verify-no-changes";
                AssertEqual("osv-scanner -r src", EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Audit, null, false).Command);
                AssertEqual("dotnet format whitespace --verify-no-changes", EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Lint, null, false).Command);
                AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, EcosystemCommandResolver.Resolve(vessel, found, EcosystemCommandIntentEnum.Outdated, null, false).Source, "outdated has no override");
            }));

            cases.Add(Case("resolve_makefile_and_justfile_targets", "Makefile and justfile targets are repo scripts; .PHONY lists and := assignments are not targets", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("go.mod", "module x\n");
                    repo.Write("Makefile", ".PHONY: test audit\nlint := golangci-lint\nlint:\n\tgolangci-lint run\n");
                    repo.Write("justfile", "test *args:\n    go test ./... {{args}}\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);

                    ResolvedEcosystemCommand lint = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.RepoScript, lint.Source);
                    AssertEqual("make lint", lint.Command);

                    ResolvedEcosystemCommand test = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Test, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.RepoScript, test.Source);
                    AssertEqual("just test", test.Command);

                    ResolvedEcosystemCommand audit = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Audit, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, audit.Source, ".PHONY entry is not a target");
                    AssertEqual("govulncheck ./...", audit.Command);
                    AssertEqual("govulncheck", audit.AuditParserId);
                }
            }));

            cases.Add(Case("resolve_composer_script", "composer.json scripts resolve through composer run-script", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("composer.json", "{ \"scripts\": { \"test\": [\"phpunit\"], \"lint\": \"phpcs\" } }");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual("composer run-script test", EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Test, repo.Path, false).Command);
                    AssertEqual("composer run-script lint", EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, repo.Path, false).Command);
                    ResolvedEcosystemCommand audit = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Audit, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, audit.Source);
                    AssertEqual("composer audit", audit.Command);
                }
            }));

            cases.Add(Case("resolve_gradle_wrapper_is_repo_script", "The Gradle wrapper resolves as a repo script with platform variants", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("build.gradle", "");
                    repo.Write("gradlew", "#!/bin/sh\n");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    ResolvedEcosystemCommand posix = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.RepoScript, posix.Source);
                    AssertEqual("./gradlew check", posix.Command);
                    ResolvedEcosystemCommand windows = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, repo.Path, true);
                    AssertEqual("gradlew.bat check", windows.Command);
                    ResolvedEcosystemCommand audit = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Audit, repo.Path, false);
                    AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, audit.Source);
                    AssertEqual(EcosystemCommandCatalog.OsvScannerParserId, audit.AuditParserId);
                }
            }));

            cases.Add(Case("resolve_no_ecosystems", "With no ecosystems Audit falls back to osv-scanner, Secrets to gitleaks, and Lint is NotSupported", TestTags.Negative, () =>
            {
                ResolvedEcosystemCommand audit = EcosystemCommandResolver.Resolve(null, null, EcosystemCommandIntentEnum.Audit, null, false);
                AssertEqual(EcosystemCommandSourceEnum.EcosystemDefault, audit.Source);
                AssertEqual(EcosystemCommandCatalog.OsvScannerCommand, audit.Command);
                AssertNull(audit.Ecosystem);
                ResolvedEcosystemCommand secrets = EcosystemCommandResolver.Resolve(null, new List<DetectedEcosystem>(), EcosystemCommandIntentEnum.Secrets, null, true);
                AssertEqual(EcosystemCommandCatalog.GitleaksCommand, secrets.Command);
                ResolvedEcosystemCommand lint = EcosystemCommandResolver.Resolve(null, null, EcosystemCommandIntentEnum.Lint, "/definitely/missing/dir", false);
                AssertEqual(EcosystemCommandSourceEnum.NotSupported, lint.Source);
                AssertEqual(String.Empty, lint.Command);
            }));

            cases.Add(Case("resolve_not_supported_skips_to_next_ecosystem", "An ecosystem without the intent defers to the next detected one", TestTags.Negative, () =>
            {
                List<DetectedEcosystem> found = new List<DetectedEcosystem>
                {
                    new DetectedEcosystem { Ecosystem = VesselEcosystemEnum.Maven, Tool = "mvn" },
                    new DetectedEcosystem { Ecosystem = VesselEcosystemEnum.Container, Tool = "docker" }
                };
                ResolvedEcosystemCommand lint = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, null, false);
                AssertEqual(VesselEcosystemEnum.Container, lint.Ecosystem);
                AssertEqual("hadolint Dockerfile", lint.Command);
            }));

            cases.Add(Case("resolve_subdirectory_root_shell_line", "A command for a nested root is prefixed with a cd into it", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("frontend/package.json", "{ \"scripts\": { \"lint\": \"eslint .\" } }");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    ResolvedEcosystemCommand lint = EcosystemCommandResolver.Resolve(null, found, EcosystemCommandIntentEnum.Lint, repo.Path, false);
                    AssertEqual("frontend", lint.RootPath);
                    AssertEqual("npm run lint", lint.Command);
                    AssertEqual("cd frontend && npm run lint", EcosystemCommandResolver.ToShellLine(lint, false));
                }
            }));

            #endregion

            #region Template-Variables

            cases.Add(Case("template_renders_new_variables", "The ecosystem and resolved command variables render and validate", TestTags.Positive, () =>
            {
                FleetActionTemplateContext ctx = new FleetActionTemplateContext
                {
                    Ecosystems = "Node, Container",
                    TestCommand = "npm test",
                    LintCommand = "npm run lint --if-present",
                    AuditCommand = "npm audit"
                };
                string template = "{{vessel.ecosystems}}|{{vessel.testCommand}}|{{ VESSEL.LINTCOMMAND }}|{{vessel.auditCommand}}";
                FleetActionTemplateRenderer.Validate(template);
                AssertEqual("Node, Container|npm test|npm run lint --if-present|npm audit", FleetActionTemplateRenderer.Render(template, ctx));
                AssertTrue(FleetActionTemplateRenderer.SupportedVariables.Contains(FleetActionTemplateRenderer.EcosystemsVariable), "listed");
            }));

            cases.Add(Case("template_from_vessel_detects", "FromVessel detects ecosystems and resolves commands from the working directory", TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    repo.Write("package.json", "{ \"scripts\": { \"lint\": \"eslint .\" } }");
                    repo.Write("pnpm-lock.yaml", "");
                    repo.Write("Dockerfile", "FROM node\n");
                    Vessel vessel = new Vessel("web", "https://example.com/web.git");
                    vessel.WorkingDirectory = repo.Path;
                    vessel.AuditCommand = "osv-scanner -r .";
                    FleetActionTemplateContext ctx = FleetActionTemplateContext.FromVessel(vessel, VesselEcosystemDetector.Detect(repo.Path), false);
                    AssertEqual("Node, Container", ctx.Ecosystems);
                    AssertEqual("pnpm test", ctx.TestCommand);
                    AssertEqual("pnpm run lint", ctx.LintCommand);
                    AssertEqual("osv-scanner -r .", ctx.AuditCommand);

                    FleetActionTemplateContext detected = FleetActionTemplateContext.FromVessel(vessel);
                    AssertEqual("Node, Container", detected.Ecosystems);
                }
            }));

            cases.Add(Case("template_vessel_without_ecosystems", "A vessel with no ecosystems renders empty ecosystems, test and lint and the osv-scanner audit fallback", TestTags.Negative, () =>
            {
                Vessel vessel = new Vessel("bare", "https://example.com/bare.git");
                vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada-missing-" + Guid.NewGuid().ToString("N"));
                FleetActionTemplateContext ctx = FleetActionTemplateContext.FromVessel(vessel);
                string rendered = FleetActionTemplateRenderer.Render("[{{vessel.ecosystems}}][{{vessel.testCommand}}][{{vessel.lintCommand}}][{{vessel.auditCommand}}]", ctx);
                AssertEqual("[][][][" + EcosystemCommandCatalog.OsvScannerCommand + "]", rendered);

                Vessel noDirectory = new Vessel("nodir", "https://example.com/nodir.git");
                noDirectory.TestCommand = "make test";
                FleetActionTemplateContext overridden = FleetActionTemplateContext.FromVessel(noDirectory);
                AssertEqual(String.Empty, overridden.Ecosystems);
                AssertEqual("make test", overridden.TestCommand);
            }));

            #endregion

            #region Vessel-Fields

            cases.Add(Case("vessel_fields_default_null_and_json", "The new vessel fields default to null and survive JSON and the client upsert copy", TestTags.Positive, () =>
            {
                Vessel minimal = System.Text.Json.JsonSerializer.Deserialize<Vessel>("{\"Name\":\"x\"}")!;
                AssertNull(minimal.TestCommand);
                AssertNull(minimal.LintCommand);
                AssertNull(minimal.AuditCommand);

                Vessel vessel = new Vessel("x", "https://example.com/x.git");
                vessel.TestCommand = "t";
                vessel.LintCommand = "l";
                vessel.AuditCommand = "a";
                VesselUpsertRequest copy = VesselUpsertRequest.From(vessel);
                AssertEqual("t", copy.TestCommand);
                AssertEqual("l", copy.LintCommand);
                AssertEqual("a", copy.AuditCommand);
            }));

            cases.Add(CaseAsync("vessel_fields_database_round_trip", "TestCommand, LintCommand and AuditCommand round-trip through create, read and update", TestTags.Positive, async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("roundtrip", "https://example.com/rt.git");
                    vessel.TestCommand = "make test";
                    vessel.LintCommand = "npm run lint";
                    vessel.AuditCommand = "osv-scanner -r .";
                    await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? read = await db.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertEqual("make test", read.TestCommand);
                    AssertEqual("npm run lint", read.LintCommand);
                    AssertEqual("osv-scanner -r .", read.AuditCommand);

                    read.TestCommand = null;
                    read.LintCommand = "ruff check";
                    await db.Driver.Vessels.UpdateAsync(read).ConfigureAwait(false);
                    Vessel? updated = await db.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated);
                    AssertNull(updated.TestCommand);
                    AssertEqual("ruff check", updated.LintCommand);
                    AssertEqual("osv-scanner -r .", updated.AuditCommand);

                    Vessel plain = new Vessel("plain", "https://example.com/plain.git");
                    await db.Driver.Vessels.CreateAsync(plain).ConfigureAwait(false);
                    Vessel? plainRead = await db.Driver.Vessels.ReadAsync(plain.Id).ConfigureAwait(false);
                    AssertNotNull(plainRead);
                    AssertNull(plainRead.TestCommand);
                    AssertNull(plainRead.LintCommand);
                    AssertNull(plainRead.AuditCommand);
                }
            }));

            #endregion

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Vessel Ecosystem Detection and Commands",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static void AddSingle(List<TestCaseDescriptor> cases, string caseId, VesselEcosystemEnum expected, string tool, params string[] files)
        {
            cases.Add(Case(caseId, "Detects " + expected + " (" + tool + ") from " + String.Join(", ", files), TestTags.Positive, () =>
            {
                using (TempRepo repo = new TempRepo())
                {
                    foreach (string file in files) repo.Write(file, "");
                    List<DetectedEcosystem> found = VesselEcosystemDetector.Detect(repo.Path);
                    AssertEqual(1, found.Count, "ecosystems: " + VesselEcosystemDetector.Format(found));
                    AssertEqual(expected, found[0].Ecosystem);
                    AssertEqual(tool, found[0].Tool);
                    AssertEqual(".", found[0].RootPath);
                    AssertTrue(found[0].HasMarker(files[0]), "marker " + files[0]);
                }
            }));
        }

        private static string Posix(VesselEcosystemEnum ecosystem, EcosystemCommandIntentEnum intent)
        {
            return EcosystemCommandCatalog.Get(ecosystem, intent).PosixCommand;
        }

        private static bool CanList(string directory)
        {
            try
            {
                Directory.GetFiles(directory);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Classes

        private sealed class TempRepo : IDisposable
        {
            public string Path { get; }

            public TempRepo()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada-eco-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Write(string relative, string content)
            {
                string full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                string? dir = System.IO.Path.GetDirectoryName(full);
                if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, content);
                return full;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { }
            }
        }

        #endregion
    }
}
