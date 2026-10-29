namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A build ecosystem detected in a vessel's working directory from its marker files.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VesselEcosystemEnum
    {
        /// <summary>
        /// .NET (*.sln, *.slnx, *.csproj, *.fsproj).
        /// </summary>
        [EnumMember(Value = "DotNet")]
        DotNet,

        /// <summary>
        /// Java with Maven (pom.xml).
        /// </summary>
        [EnumMember(Value = "Maven")]
        Maven,

        /// <summary>
        /// JVM with Gradle (build.gradle or build.gradle.kts).
        /// </summary>
        [EnumMember(Value = "Gradle")]
        Gradle,

        /// <summary>
        /// JavaScript or TypeScript (package.json); the lockfile picks npm, pnpm, yarn or bun.
        /// </summary>
        [EnumMember(Value = "Node")]
        Node,

        /// <summary>
        /// Python (pyproject.toml, requirements*.txt or setup.py); uv.lock or poetry.lock picks the tool.
        /// </summary>
        [EnumMember(Value = "Python")]
        Python,

        /// <summary>
        /// Ruby with Bundler (Gemfile).
        /// </summary>
        [EnumMember(Value = "Ruby")]
        Ruby,

        /// <summary>
        /// Go modules (go.mod).
        /// </summary>
        [EnumMember(Value = "Go")]
        Go,

        /// <summary>
        /// Rust with Cargo (Cargo.toml).
        /// </summary>
        [EnumMember(Value = "Rust")]
        Rust,

        /// <summary>
        /// PHP with Composer (composer.json).
        /// </summary>
        [EnumMember(Value = "Php")]
        Php,

        /// <summary>
        /// C or C++ (CMakeLists.txt, meson.build, or a Makefile in a directory with no other ecosystem).
        /// </summary>
        [EnumMember(Value = "Cpp")]
        Cpp,

        /// <summary>
        /// Swift Package Manager (Package.swift).
        /// </summary>
        [EnumMember(Value = "Swift")]
        Swift,

        /// <summary>
        /// Dart or Flutter (pubspec.yaml).
        /// </summary>
        [EnumMember(Value = "Dart")]
        Dart,

        /// <summary>
        /// Elixir with Mix (mix.exs).
        /// </summary>
        [EnumMember(Value = "Elixir")]
        Elixir,

        /// <summary>
        /// Container image (Dockerfile or Containerfile).
        /// </summary>
        [EnumMember(Value = "Container")]
        Container
    }
}
