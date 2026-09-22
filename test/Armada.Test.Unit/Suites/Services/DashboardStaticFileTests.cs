namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Server.Dashboard;
    using Armada.Test.Common;

    /// <summary>
    /// Verifies that the dashboard is served only from the dashboard directory and that every static
    /// asset path the React dashboard requests ships in its build input.
    /// </summary>
    public class DashboardStaticFileTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Dashboard Static Files";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryGetFile ServesDashboardDirectoryFilesAndNothingElse", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada_dashboard_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(directory, "assets"));
                File.WriteAllText(Path.Combine(directory, "index.html"), "<!doctype html><title>React dashboard</title>");
                File.WriteAllText(Path.Combine(directory, "assets", "app.js"), "console.log('dashboard');");

                try
                {
                    StaticFileHandler.SetExternalPath(directory);

                    AssertTrue(StaticFileHandler.TryGetFile("/dashboard", out byte[] index, out string indexType), "the dashboard root serves index.html");
                    AssertContains("React dashboard", Encoding.UTF8.GetString(index));
                    AssertEqual("text/html; charset=utf-8", indexType);

                    AssertTrue(StaticFileHandler.TryGetFile("/dashboard/assets/app.js", out byte[] script, out string scriptType), "a build asset is served");
                    AssertEqual("application/javascript; charset=utf-8", scriptType);
                    AssertTrue(script.Length > 0, "the asset content is returned");

                    AssertTrue(StaticFileHandler.TryGetIndex(out byte[] fallback, out _), "the client-side route fallback serves index.html");
                    AssertEqual(index.Length, fallback.Length);

                    foreach (string absent in new[] { "/dashboard/js/dashboard.js", "/dashboard/views/home.html", "/dashboard/css/dashboard.css", "/dashboard/i18n/armada.json" })
                        AssertFalse(StaticFileHandler.TryGetFile(absent, out _, out _), "a file missing from the dashboard directory is not served: " + absent);

                    AssertFalse(StaticFileHandler.TryGetFile("/dashboard/../index.html", out _, out _), "a traversal path is refused");
                }
                finally
                {
                    StaticFileHandler.SetExternalPath(null);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }

                return Task.CompletedTask;
            });

            await RunTest("TryGetFile RefusesSiblingDirectorySharingTheDashboardNamePrefix", () =>
            {
                string baseDirectory = Path.Combine(Path.GetTempPath(), "armada_dashboard_base_" + Guid.NewGuid().ToString("N"));
                string directory = Path.Combine(baseDirectory, "dashboard");
                string sibling = Path.Combine(baseDirectory, "dashboard-backup");
                Directory.CreateDirectory(directory);
                Directory.CreateDirectory(sibling);
                File.WriteAllText(Path.Combine(directory, "index.html"), "<!doctype html><title>React dashboard</title>");
                File.WriteAllText(Path.Combine(sibling, "probe.txt"), "outside the dashboard");

                try
                {
                    StaticFileHandler.SetExternalPath(directory);

                    // A rooted request path replaces the dashboard directory when combined, so the containment
                    // check alone decides whether a sibling with the same name prefix is served.
                    string rootedSibling = Path.Combine(sibling, "probe.txt").Replace('\\', '/');
                    AssertFalse(StaticFileHandler.TryGetFile("/dashboard/" + rootedSibling, out _, out _), "a sibling directory that shares the dashboard's name prefix is not served");
                    AssertTrue(StaticFileHandler.TryGetFile("/dashboard/index.html", out _, out _), "a file inside the dashboard directory is still served");
                }
                finally
                {
                    StaticFileHandler.SetExternalPath(null);
                    if (Directory.Exists(baseDirectory)) Directory.Delete(baseDirectory, recursive: true);
                }

                return Task.CompletedTask;
            });

            await RunTest("ServerAssembly EmbedsNoDashboardFiles", () =>
            {
                string[] resources = typeof(StaticFileHandler).Assembly.GetManifestResourceNames();
                string[] dashboardResources = resources.Where(name => name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
                AssertEqual(0, dashboardResources.Length, "the server assembly carries no dashboard files: " + String.Join(", ", dashboardResources));
                return Task.CompletedTask;
            });

            await RunTest("ReactDashboard RequestedStaticAssetsShipInPublicDirectory", () =>
            {
                string dashboardRoot = Path.Combine(FindRepositoryRoot(), "src", "Armada.Dashboard");
                List<string> sources = new List<string> { Path.Combine(dashboardRoot, "index.html") };
                sources.AddRange(Directory.EnumerateFiles(Path.Combine(dashboardRoot, "src"), "*.ts*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)));

                Regex assetPath = new Regex(@"['""](?:/dashboard)?/((?:img|i18n)/[A-Za-z0-9._-]+)['""]");
                HashSet<string> requested = new HashSet<string>(StringComparer.Ordinal);
                foreach (string source in sources)
                {
                    foreach (Match match in assetPath.Matches(File.ReadAllText(source)))
                        requested.Add(match.Groups[1].Value);
                }

                AssertTrue(requested.Contains("i18n/armada.json"), "the dashboard requests its translation catalog");
                AssertTrue(requested.Any(path => path.StartsWith("img/", StringComparison.Ordinal)), "the dashboard requests its logo images");
                foreach (string path in requested)
                {
                    string shipped = Path.Combine(dashboardRoot, "public", path.Replace('/', Path.DirectorySeparatorChar));
                    AssertTrue(File.Exists(shipped), "a requested dashboard asset must ship in the build input: public/" + path);
                }

                return Task.CompletedTask;
            });
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src", "Armada.Dashboard"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                    return current.FullName;
                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
        }
    }
}
