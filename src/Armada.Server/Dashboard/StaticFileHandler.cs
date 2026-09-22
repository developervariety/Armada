namespace Armada.Server.Dashboard
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using Armada.Core;
    using Armada.Core.Services;

    /// <summary>
    /// Serves static files for the web dashboard from the dashboard directory (the React build
    /// output). A path that does not exist in that directory is not served.
    /// </summary>
    public static class StaticFileHandler
    {
        #region Private-Members

        private static readonly Dictionary<string, string> _ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".js", "application/javascript; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" },
            { ".png", "image/png" },
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" },
            { ".svg", "image/svg+xml" },
            { ".ico", "image/x-icon" },
            { ".woff", "font/woff" },
            { ".woff2", "font/woff2" },
            { ".ttf", "font/ttf" },
            { ".eot", "application/vnd.ms-fontobject" },
            { ".map", "application/json" }
        };

        private static string? _ExternalDashboardPath = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Set the dashboard directory path.
        /// </summary>
        /// <param name="path">Absolute path to the dashboard build output directory (e.g., dist/).</param>
        public static void SetExternalPath(string? path)
        {
            if (!String.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                _ExternalDashboardPath = Path.GetFullPath(path);
            }
            else
            {
                _ExternalDashboardPath = null;
            }
        }

        /// <summary>
        /// Try to read a static file for the given URL path from the dashboard directory.
        /// </summary>
        /// <param name="urlPath">URL path (e.g., "/dashboard/index.html").</param>
        /// <param name="content">File content bytes.</param>
        /// <param name="contentType">MIME content type.</param>
        /// <returns>True if the file was found.</returns>
        public static bool TryGetFile(string urlPath, out byte[] content, out string contentType)
        {
            content = Array.Empty<byte>();
            contentType = "application/octet-stream";

            if (String.IsNullOrEmpty(urlPath)) return false;
            EnsureExternalPathResolved();
            if (_ExternalDashboardPath == null) return false;

            // Strip leading /dashboard/ prefix
            string relativePath = urlPath;
            if (relativePath.StartsWith("/dashboard/"))
                relativePath = relativePath.Substring("/dashboard/".Length);
            else if (relativePath == "/dashboard")
                relativePath = "index.html";

            if (String.IsNullOrEmpty(relativePath) || relativePath == "/")
                relativePath = "index.html";

            // Sanitize: prevent directory traversal
            relativePath = relativePath.Replace('\\', '/');
            if (relativePath.Contains("..")) return false;

            return TryGetExternalFile(relativePath, out content, out contentType);
        }

        /// <summary>
        /// Try to get the SPA index.html fallback for client-side routing.
        /// </summary>
        /// <param name="content">File content bytes.</param>
        /// <param name="contentType">MIME content type.</param>
        /// <returns>True if index.html was found.</returns>
        public static bool TryGetIndex(out byte[] content, out string contentType)
        {
            EnsureExternalPathResolved();
            return TryGetFile("/dashboard/index.html", out content, out contentType);
        }

        /// <summary>
        /// Returns true if a dashboard directory is configured and exists.
        /// </summary>
        public static bool HasExternalDashboard => _ExternalDashboardPath != null;

        #endregion

        #region Private-Methods

        private static void EnsureExternalPathResolved()
        {
            if (_ExternalDashboardPath != null && Directory.Exists(_ExternalDashboardPath))
                return;

            _ExternalDashboardPath = null;

            string dashboardInData = Path.Combine(Constants.DefaultDataDirectory, "dashboard");
            if (Directory.Exists(dashboardInData) && File.Exists(Path.Combine(dashboardInData, "index.html")))
            {
                _ExternalDashboardPath = Path.GetFullPath(dashboardInData);
                return;
            }

            string? exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (exeDir == null)
                return;

            string dashboardNextToExe = Path.Combine(exeDir, "dashboard");
            if (Directory.Exists(dashboardNextToExe) && File.Exists(Path.Combine(dashboardNextToExe, "index.html")))
            {
                _ExternalDashboardPath = Path.GetFullPath(dashboardNextToExe);
            }
        }

        private static bool TryGetExternalFile(string relativePath, out byte[] content, out string contentType)
        {
            content = Array.Empty<byte>();
            contentType = "application/octet-stream";

            string? filePath = PathContainment.TryResolve(_ExternalDashboardPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (filePath == null)
                return false;

            if (!File.Exists(filePath))
                return false;

            content = File.ReadAllBytes(filePath);

            string ext = Path.GetExtension(filePath);
            if (!String.IsNullOrEmpty(ext) && _ContentTypes.TryGetValue(ext, out string? ct))
            {
                contentType = ct;
            }

            return true;
        }

        #endregion
    }
}
