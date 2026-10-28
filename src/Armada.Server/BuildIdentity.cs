namespace Armada.Server
{
    using System.Reflection;

    /// <summary>Identity embedded by the SDK at build time; never inferred from the server's current checkout.</summary>
    public static class BuildIdentity
    {
        /// <summary>Product version plus source revision, when available in the build.</summary>
        public static string InformationalVersion => typeof(BuildIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        /// <summary>The source revision embedded in this binary, or null for an unversioned build.</summary>
        public static string? CommitHash => InformationalVersion.Contains('+') ? InformationalVersion.Split('+', 2)[1] : null;
    }
}
