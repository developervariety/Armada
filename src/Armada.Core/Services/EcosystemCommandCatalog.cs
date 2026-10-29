namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Default command per ecosystem and intent, with POSIX and Windows variants, the executables each needs, whether
    /// it needs the network and, for audits, the output parser id. Every ecosystem/intent pair returns either a
    /// supported command or an explicit NotSupported entry (<see cref="EcosystemCommand.Supported"/> false).
    /// Ecosystems without their own audit tool use the universal osv-scanner fallback; Secrets is gitleaks for
    /// every ecosystem.
    /// </summary>
    public static class EcosystemCommandCatalog
    {
        #region Public-Members

        /// <summary>
        /// Audit parser id for osv-scanner output.
        /// </summary>
        public static readonly string OsvScannerParserId = "osv-scanner";

        /// <summary>
        /// Command for the universal audit fallback (osv-scanner, recursive source scan).
        /// </summary>
        public static readonly string OsvScannerCommand = "osv-scanner scan source -r .";

        /// <summary>
        /// Command for the Secrets intent (gitleaks).
        /// </summary>
        public static readonly string GitleaksCommand = "gitleaks detect --source . --no-banner";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The default command for a detected ecosystem, using its tool and markers (lockfile-selected package
        /// manager, Gradle or Maven wrapper, vcpkg/conan manifests, rspec layout).
        /// </summary>
        /// <param name="detected">Detected ecosystem.</param>
        /// <param name="intent">Intent.</param>
        /// <returns>Catalog entry; never null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="detected"/> is null.</exception>
        public static EcosystemCommand Get(DetectedEcosystem detected, EcosystemCommandIntentEnum intent)
        {
            if (detected == null) throw new ArgumentNullException(nameof(detected));
            return Get(detected.Ecosystem, intent, detected.Tool, detected.Markers);
        }

        /// <summary>
        /// The default command for an ecosystem and intent.
        /// </summary>
        /// <param name="ecosystem">Ecosystem.</param>
        /// <param name="intent">Intent.</param>
        /// <param name="tool">Sub-tool from detection (for example pnpm, uv, gradlew); null or empty uses the ecosystem's plain default.</param>
        /// <param name="markers">Marker names from detection; null means none.</param>
        /// <returns>Catalog entry; never null.</returns>
        public static EcosystemCommand Get(VesselEcosystemEnum ecosystem, EcosystemCommandIntentEnum intent, string? tool = null, IEnumerable<string>? markers = null)
        {
            HashSet<string> markerSet = new HashSet<string>(markers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string sub = (tool ?? String.Empty).Trim().ToLowerInvariant();

            if (intent == EcosystemCommandIntentEnum.Secrets)
            {
                EcosystemCommand secrets = Secrets();
                secrets.Ecosystem = ecosystem;
                return secrets;
            }

            switch (ecosystem)
            {
                case VesselEcosystemEnum.DotNet: return DotNet(intent);
                case VesselEcosystemEnum.Maven: return Maven(intent, sub);
                case VesselEcosystemEnum.Gradle: return Gradle(intent, sub);
                case VesselEcosystemEnum.Node: return Node(intent, sub);
                case VesselEcosystemEnum.Python: return Python(intent, sub);
                case VesselEcosystemEnum.Ruby: return Ruby(intent, markerSet);
                case VesselEcosystemEnum.Go: return Go(intent);
                case VesselEcosystemEnum.Rust: return Rust(intent);
                case VesselEcosystemEnum.Php: return Php(intent);
                case VesselEcosystemEnum.Cpp: return Cpp(intent, sub, markerSet);
                case VesselEcosystemEnum.Swift: return Swift(intent);
                case VesselEcosystemEnum.Dart: return Dart(intent, sub);
                case VesselEcosystemEnum.Elixir: return Elixir(intent);
                case VesselEcosystemEnum.Container: return Container(intent);
                default: return NotSupported(ecosystem, intent, "Unknown ecosystem.");
            }
        }

        /// <summary>
        /// The universal audit fallback (osv-scanner), used when no detected ecosystem has its own audit tool.
        /// </summary>
        /// <returns>Catalog entry with a null ecosystem.</returns>
        public static EcosystemCommand UniversalAudit()
        {
            return Osv(null);
        }

        /// <summary>
        /// The Secrets command (gitleaks), which applies to every repository.
        /// </summary>
        /// <returns>Catalog entry with a null ecosystem.</returns>
        public static EcosystemCommand Secrets()
        {
            return Same(null, EcosystemCommandIntentEnum.Secrets, GitleaksCommand, Tools("gitleaks"), false, null);
        }

        #endregion

        #region Private-Methods

        private static EcosystemCommand DotNet(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.DotNet;
            List<string> tools = Tools("dotnet");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "dotnet build", tools, true, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "dotnet test", tools, true, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "dotnet format --verify-no-changes", tools, true, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "dotnet list package --vulnerable --include-transitive", tools, true, "dotnet-list-package-vulnerable");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "dotnet list package --outdated", tools, true, null);
                default: return NotSupported(e, intent, "No built-in .NET license report; set a command on the action.");
            }
        }

        private static EcosystemCommand Maven(EcosystemCommandIntentEnum intent, string tool)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Maven;
            bool wrapper = tool == "mvnw";
            string posix = wrapper ? "./mvnw" : "mvn";
            string windows = wrapper ? "mvnw.cmd" : "mvn";
            List<string> tools = wrapper ? Tools("java") : Tools("mvn");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Split(e, intent, posix + " -B -DskipTests package", windows + " -B -DskipTests package", tools, true, null);
                case EcosystemCommandIntentEnum.Test: return Split(e, intent, posix + " -B test", windows + " -B test", tools, true, null);
                case EcosystemCommandIntentEnum.Audit: return Osv(e);
                case EcosystemCommandIntentEnum.Outdated: return Split(e, intent, posix + " -B versions:display-dependency-updates", windows + " -B versions:display-dependency-updates", tools, true, null);
                case EcosystemCommandIntentEnum.Licenses: return Split(e, intent, posix + " -B license:add-third-party", windows + " -B license:add-third-party", tools, true, null);
                default: return NotSupported(e, intent, "Maven has no standard lint goal; set LintCommand on the vessel.");
            }
        }

        private static EcosystemCommand Gradle(EcosystemCommandIntentEnum intent, string tool)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Gradle;
            bool wrapper = tool == "gradlew";
            string posix = wrapper ? "./gradlew" : "gradle";
            string windows = wrapper ? "gradlew.bat" : "gradle";
            List<string> tools = wrapper ? Tools("java") : Tools("gradle");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Split(e, intent, posix + " assemble", windows + " assemble", tools, true, null);
                case EcosystemCommandIntentEnum.Test: return Split(e, intent, posix + " test", windows + " test", tools, true, null);
                case EcosystemCommandIntentEnum.Lint: return Split(e, intent, posix + " check", windows + " check", tools, true, null);
                case EcosystemCommandIntentEnum.Audit: return Osv(e);
                case EcosystemCommandIntentEnum.Outdated: return NotSupported(e, intent, "Needs the gradle-versions-plugin (dependencyUpdates); not built in.");
                default: return NotSupported(e, intent, "Needs a license plugin; not built in.");
            }
        }

        private static EcosystemCommand Node(EcosystemCommandIntentEnum intent, string tool)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Node;
            string pm = (tool == "pnpm" || tool == "yarn" || tool == "bun") ? tool : "npm";
            List<string> tools = Tools(pm);
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, RunIfPresent(pm, "build"), tools, false, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, pm == "bun" ? "bun run test" : pm + " test", tools, false, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, RunIfPresent(pm, "lint"), tools, false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, pm + " audit", tools, true, pm + "-audit");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, pm + " outdated", tools, true, null);
                default: return Same(e, intent, "npx --yes license-checker --summary", Tools("npx"), true, null);
            }
        }

        private static string RunIfPresent(string pm, string script)
        {
            // npm takes --if-present after the script name; pnpm needs it before (later flags go to the script).
            // yarn and bun have no --if-present, so a missing script fails; the resolver prefers declared scripts.
            if (pm == "npm") return "npm run " + script + " --if-present";
            if (pm == "pnpm") return "pnpm run --if-present " + script;
            return pm + " run " + script;
        }

        private static EcosystemCommand Python(EcosystemCommandIntentEnum intent, string tool)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Python;
            if (tool == "uv")
            {
                List<string> uv = Tools("uv");
                switch (intent)
                {
                    case EcosystemCommandIntentEnum.Build: return Same(e, intent, "uv build", uv, true, null);
                    case EcosystemCommandIntentEnum.Test: return Same(e, intent, "uv run pytest", uv, true, null);
                    case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "uv run ruff check", uv, true, null);
                    case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "uv run --with pip-audit pip-audit", uv, true, "pip-audit");
                    case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "uv pip list --outdated", uv, true, null);
                    default: return Same(e, intent, "uv run --with pip-licenses pip-licenses", uv, true, null);
                }
            }

            if (tool == "poetry")
            {
                List<string> poetry = Tools("poetry");
                switch (intent)
                {
                    case EcosystemCommandIntentEnum.Build: return Same(e, intent, "poetry build", poetry, false, null);
                    case EcosystemCommandIntentEnum.Test: return Same(e, intent, "poetry run pytest", poetry, false, null);
                    case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "poetry run ruff check", poetry, false, null);
                    case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "poetry run pip-audit", poetry, true, "pip-audit");
                    case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "poetry show --outdated", poetry, true, null);
                    default: return Same(e, intent, "poetry run pip-licenses", poetry, false, null);
                }
            }

            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Split(e, intent, "python3 -m build", "python -m build", Tools("python3"), Tools("python"), true, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "pytest", Tools("pytest"), false, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "ruff check", Tools("ruff"), false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "pip-audit", Tools("pip-audit"), true, "pip-audit");
                case EcosystemCommandIntentEnum.Outdated: return Split(e, intent, "python3 -m pip list --outdated", "python -m pip list --outdated", Tools("python3"), Tools("python"), true, null);
                default: return Same(e, intent, "pip-licenses", Tools("pip-licenses"), false, null);
            }
        }

        private static EcosystemCommand Ruby(EcosystemCommandIntentEnum intent, HashSet<string> markers)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Ruby;
            List<string> bundle = Tools("bundle");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "bundle install", bundle, true, null);
                case EcosystemCommandIntentEnum.Test:
                    bool rspec = markers.Contains(".rspec") || markers.Contains("spec");
                    return Same(e, intent, rspec ? "bundle exec rspec" : "bundle exec rake test", bundle, false, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "bundle exec rubocop", bundle, false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "bundle-audit check --update", Tools("bundle-audit"), true, "bundle-audit");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "bundle outdated", bundle, true, null);
                default: return Same(e, intent, "license_finder", Tools("license_finder"), false, null);
            }
        }

        private static EcosystemCommand Go(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Go;
            List<string> go = Tools("go");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "go build ./...", go, true, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "go test ./...", go, true, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "go vet ./...", go, false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "govulncheck ./...", Tools("govulncheck"), true, "govulncheck");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "go list -m -u all", go, true, null);
                default: return Same(e, intent, "go-licenses report ./...", Tools("go-licenses"), true, null);
            }
        }

        private static EcosystemCommand Rust(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Rust;
            List<string> cargo = Tools("cargo");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "cargo build", cargo, true, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "cargo test", cargo, true, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "cargo clippy -- -D warnings", cargo, true, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "cargo audit", Tools("cargo", "cargo-audit"), true, "cargo-audit");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "cargo outdated", Tools("cargo", "cargo-outdated"), true, null);
                default: return Same(e, intent, "cargo deny check licenses", Tools("cargo", "cargo-deny"), true, null);
            }
        }

        private static EcosystemCommand Php(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Php;
            List<string> composer = Tools("composer");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "composer install", composer, true, null);
                case EcosystemCommandIntentEnum.Test: return Split(e, intent, "vendor/bin/phpunit", "vendor\\bin\\phpunit", Tools("php"), false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "composer audit", composer, true, "composer-audit");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "composer outdated --direct", composer, true, null);
                case EcosystemCommandIntentEnum.Licenses: return Same(e, intent, "composer licenses", composer, false, null);
                default: return NotSupported(e, intent, "PHP has no standard linter; set LintCommand on the vessel or declare a composer lint script.");
            }
        }

        private static EcosystemCommand Cpp(EcosystemCommandIntentEnum intent, string tool, HashSet<string> markers)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Cpp;
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build:
                    if (tool == "meson") return Same(e, intent, "meson setup build && meson compile -C build", Tools("meson"), false, null);
                    if (tool == "make") return Same(e, intent, "make", Tools("make"), false, null);
                    return Same(e, intent, "cmake -S . -B build && cmake --build build", Tools("cmake"), false, null);
                case EcosystemCommandIntentEnum.Test:
                    if (tool == "meson") return Same(e, intent, "meson test -C build", Tools("meson"), false, null);
                    if (tool == "make") return Same(e, intent, "make test", Tools("make"), false, null);
                    return Same(e, intent, "ctest --test-dir build", Tools("ctest"), false, null);
                case EcosystemCommandIntentEnum.Audit:
                    bool manifest = markers.Contains("vcpkg.json") || markers.Contains("conanfile.txt") || markers.Contains("conanfile.py");
                    if (manifest) return Osv(e);
                    return NotSupported(e, intent, "C/C++ audit needs a vcpkg.json or conanfile dependency manifest.");
                case EcosystemCommandIntentEnum.Lint: return NotSupported(e, intent, "No standard C/C++ linter; set LintCommand on the vessel.");
                case EcosystemCommandIntentEnum.Outdated: return NotSupported(e, intent, "No standard C/C++ dependency manager.");
                default: return NotSupported(e, intent, "No standard C/C++ license report.");
            }
        }

        private static EcosystemCommand Swift(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Swift;
            List<string> swift = Tools("swift");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "swift build", swift, true, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "swift test", swift, true, null);
                case EcosystemCommandIntentEnum.Audit: return Osv(e);
                case EcosystemCommandIntentEnum.Lint: return NotSupported(e, intent, "No built-in Swift linter; set LintCommand on the vessel (for example swiftlint).");
                case EcosystemCommandIntentEnum.Outdated: return NotSupported(e, intent, "SwiftPM has no outdated report.");
                default: return NotSupported(e, intent, "SwiftPM has no license report.");
            }
        }

        private static EcosystemCommand Dart(EcosystemCommandIntentEnum intent, string tool)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Dart;
            string cli = tool == "flutter" ? "flutter" : "dart";
            List<string> tools = Tools(cli);
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, cli + " test", tools, false, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, cli + " analyze", tools, false, null);
                case EcosystemCommandIntentEnum.Audit: return Osv(e);
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, cli + " pub outdated", tools, true, null);
                case EcosystemCommandIntentEnum.Build: return NotSupported(e, intent, "Dart and Flutter builds need a target or entry point; set a build command on the vessel.");
                default: return NotSupported(e, intent, "No built-in Dart license report.");
            }
        }

        private static EcosystemCommand Elixir(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Elixir;
            List<string> mix = Tools("mix");
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "mix compile", mix, false, null);
                case EcosystemCommandIntentEnum.Test: return Same(e, intent, "mix test", mix, false, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "mix format --check-formatted", mix, false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "mix deps.audit", mix, true, "mix-audit");
                case EcosystemCommandIntentEnum.Outdated: return Same(e, intent, "mix hex.outdated", mix, true, null);
                default: return NotSupported(e, intent, "No built-in Elixir license report.");
            }
        }

        private static EcosystemCommand Container(EcosystemCommandIntentEnum intent)
        {
            VesselEcosystemEnum e = VesselEcosystemEnum.Container;
            switch (intent)
            {
                case EcosystemCommandIntentEnum.Build: return Same(e, intent, "docker build .", Tools("docker"), true, null);
                case EcosystemCommandIntentEnum.Lint: return Same(e, intent, "hadolint Dockerfile", Tools("hadolint"), false, null);
                case EcosystemCommandIntentEnum.Audit: return Same(e, intent, "trivy fs .", Tools("trivy"), true, "trivy");
                case EcosystemCommandIntentEnum.Licenses: return Same(e, intent, "trivy fs --scanners license .", Tools("trivy"), true, null);
                case EcosystemCommandIntentEnum.Test: return NotSupported(e, intent, "Container images have no standard test command.");
                default: return NotSupported(e, intent, "Container images have no outdated report.");
            }
        }

        private static EcosystemCommand Osv(VesselEcosystemEnum? ecosystem)
        {
            return Same(ecosystem, EcosystemCommandIntentEnum.Audit, OsvScannerCommand, Tools("osv-scanner"), true, OsvScannerParserId);
        }

        private static EcosystemCommand Same(VesselEcosystemEnum? ecosystem, EcosystemCommandIntentEnum intent, string command, List<string> tools, bool network, string? parser)
        {
            return Split(ecosystem, intent, command, command, tools, new List<string>(tools), network, parser);
        }

        private static EcosystemCommand Split(VesselEcosystemEnum? ecosystem, EcosystemCommandIntentEnum intent, string posix, string windows, List<string> tools, bool network, string? parser)
        {
            return Split(ecosystem, intent, posix, windows, tools, new List<string>(tools), network, parser);
        }

        private static EcosystemCommand Split(VesselEcosystemEnum? ecosystem, EcosystemCommandIntentEnum intent, string posix, string windows, List<string> tools, List<string> windowsTools, bool network, string? parser)
        {
            return new EcosystemCommand
            {
                Ecosystem = ecosystem,
                Intent = intent,
                Supported = true,
                PosixCommand = posix,
                WindowsCommand = windows,
                RequiredTools = tools,
                WindowsRequiredTools = windowsTools,
                RequiresNetwork = network,
                AuditParserId = intent == EcosystemCommandIntentEnum.Audit ? parser : null
            };
        }

        private static EcosystemCommand NotSupported(VesselEcosystemEnum ecosystem, EcosystemCommandIntentEnum intent, string notes)
        {
            return new EcosystemCommand
            {
                Ecosystem = ecosystem,
                Intent = intent,
                Supported = false,
                Notes = notes
            };
        }

        private static List<string> Tools(params string[] names)
        {
            return new List<string>(names);
        }

        #endregion
    }
}
