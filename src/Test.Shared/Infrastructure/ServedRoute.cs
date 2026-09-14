namespace Test.Shared.Infrastructure
{
    using System;
    using System.Text.RegularExpressions;

    /// <summary>
    /// One route a live server registered, with the matching rule contract suites share.
    /// </summary>
    public sealed class ServedRoute
    {
        #region Public-Members

        /// <summary>
        /// Upper-case HTTP method.
        /// </summary>
        public string Method { get; private set; } = String.Empty;

        /// <summary>
        /// Registered path template, or the pattern text for a dynamic route.
        /// </summary>
        public string Path { get; private set; } = String.Empty;

        #endregion

        #region Private-Members

        private static readonly Regex _Variable = new Regex(@"\{\{[^{}]+\}\}", RegexOptions.Compiled);
        private string[] _Segments = Array.Empty<string>();
        private Regex? _Pattern;

        #endregion

        #region Constructors-and-Factories

        private ServedRoute()
        {
        }

        /// <summary>
        /// Create a route from a static or parameter template such as <c>/api/v1/vessels/{id}</c>.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Path template.</param>
        /// <returns>The route.</returns>
        public static ServedRoute FromTemplate(string method, string path)
        {
            ServedRoute route = new ServedRoute();
            route.Method = method.ToUpperInvariant();
            route.Path = path;
            route._Segments = Split(path);
            return route;
        }

        /// <summary>
        /// Create a route from a dynamic regular-expression route.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="pattern">Route pattern.</param>
        /// <returns>The route.</returns>
        public static ServedRoute FromRegex(string method, Regex pattern)
        {
            ServedRoute route = new ServedRoute();
            route.Method = method.ToUpperInvariant();
            route.Path = pattern.ToString();
            route._Pattern = pattern;
            return route;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// A route parameter matches any single segment. A literal route segment matches the same
        /// literal, case-insensitively. A <c>{{variable}}</c> placeholder matches only a route
        /// parameter, so an example cannot pass by putting a variable where the server expects a
        /// fixed word.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Concrete or placeholder path without query.</param>
        /// <returns>Whether this route serves the request.</returns>
        public bool Matches(string method, string path)
        {
            if (!String.Equals(Method, method, StringComparison.OrdinalIgnoreCase)) return false;
            if (_Pattern != null) return _Pattern.IsMatch(_Variable.Replace(path, "sample"));

            string[] segments = Split(path);
            if (segments.Length != _Segments.Length) return false;
            for (int i = 0; i < segments.Length; i++)
            {
                bool routeParameter = _Segments[i].StartsWith("{", StringComparison.Ordinal) && _Segments[i].EndsWith("}", StringComparison.Ordinal);
                if (routeParameter)
                {
                    if (segments[i].Length == 0) return false;
                    continue;
                }

                if (_Variable.IsMatch(segments[i])) return false;
                if (!String.Equals(_Segments[i], segments[i], StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Method + " " + Path;
        }

        #endregion

        #region Private-Methods

        private static string[] Split(string path)
        {
            return path.Trim('/').Split('/', StringSplitOptions.None);
        }

        #endregion
    }
}
