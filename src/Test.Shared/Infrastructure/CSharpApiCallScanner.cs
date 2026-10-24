namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Finds the REST requests C# client code sends (the Helm CLI and Armada.Core's ArmadaApiClient) by reading the
    /// path argument of Get/Post/Put/Delete/Patch*Async calls and HttpMethod.X request builders. A concatenated or
    /// interpolated whole path segment becomes <c>x</c>; the query string is removed.
    /// </summary>
    public static class CSharpApiCallScanner
    {
        #region Private-Members

        private static readonly Regex _Call = new Regex(
            "\\b(Get|Post|Put|Delete|Patch)(?:Json|NoResult|Raw)?Async\\s*(?:<[^()]*?>)?\\s*\\(|HttpMethod\\.(Get|Post|Put|Delete|Patch)\\s*,",
            RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Scan C# files for REST requests.
        /// </summary>
        /// <param name="files">Absolute file paths.</param>
        /// <returns>Requests; <see cref="DashboardApiCall.Export"/> holds file name and line.</returns>
        /// <exception cref="ArgumentNullException">Thrown when files is null.</exception>
        public static List<DashboardApiCall> Scan(IEnumerable<string> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            List<DashboardApiCall> calls = new List<DashboardApiCall>();
            foreach (string file in files)
            {
                string src = File.ReadAllText(file);
                foreach (Match m in _Call.Matches(src))
                {
                    string path = ReadPathArgument(src, m.Index + m.Length);
                    int api = path.IndexOf("/api/", StringComparison.Ordinal);
                    if (api < 0) continue;
                    path = path.Substring(api);
                    int query = path.IndexOf('?');
                    if (query >= 0) path = path.Substring(0, query);
                    string verb = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToUpperInvariant();
                    int line = src.Take(m.Index).Count(c => c == '\n') + 1;
                    calls.Add(new DashboardApiCall { Export = Path.GetFileName(file) + ":" + line, Method = verb, Path = path });
                }
            }

            return calls;
        }

        #endregion

        #region Private-Methods

        private static string ReadPathArgument(string src, int start)
        {
            StringBuilder path = new StringBuilder();
            int depth = 0;
            int i = start;
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (c == ',' && depth == 0) break;
                else if (c == '"' || (c == '$' && i + 1 < src.Length && src[i + 1] == '"'))
                {
                    bool interpolated = c == '$';
                    if (interpolated) i++;
                    int k = i + 1;
                    StringBuilder literal = new StringBuilder();
                    while (k < src.Length && src[k] != '"')
                    {
                        if (src[k] == '\\' && k + 1 < src.Length) { literal.Append(src, k, 2); k += 2; continue; }
                        literal.Append(src[k]);
                        k++;
                    }

                    string text = literal.ToString();
                    if (interpolated)
                    {
                        text = Regex.Replace(text, "(?<=/)\\{[^}]*\\}(?=/|$|\\?)", "x");
                        text = Regex.Replace(text, "\\{[^}]*\\}", "");
                    }

                    path.Append(text);
                    i = k + 1;
                    continue;
                }
                else if (c == '+' && depth == 0)
                {
                    string rest = src.Substring(i + 1).TrimStart();
                    bool nextIsLiteral = rest.StartsWith("\"", StringComparison.Ordinal) || rest.StartsWith("$\"", StringComparison.Ordinal);
                    if (!nextIsLiteral && path.Length > 0 && path[path.Length - 1] == '/') path.Append('x');
                }

                i++;
            }

            return path.ToString();
        }

        #endregion
    }
}
