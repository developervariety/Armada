namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Best-effort polyglot symbol and relationship extractor for code-index graph sidecars.
    /// Uses lightweight language-specific regexes so indexing remains dependency-free.
    /// </summary>
    public class PolyglotSymbolExtractor
    {
        #region Private-Members

        private readonly CSharpSymbolExtractor _CSharp = new CSharpSymbolExtractor();

        private static readonly Regex _JsImportRx = new Regex(@"^\s*import\s+.*?\s+from\s+['""]([^'""]+)['""]", RegexOptions.Compiled);
        private static readonly Regex _JsRequireRx = new Regex(@"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);
        private static readonly Regex _JsClassRx = new Regex(@"^\s*(?:export\s+default\s+|export\s+)?class\s+([A-Za-z_$][\w$]*)", RegexOptions.Compiled);
        private static readonly Regex _JsFunctionRx = new Regex(@"^\s*(?:export\s+default\s+|export\s+)?(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex _JsArrowFunctionRx = new Regex(@"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>", RegexOptions.Compiled);
        private static readonly Regex _JsMethodRx = new Regex(@"^\s*(?:async\s+)?([A-Za-z_$][\w$]*)\s*\([^)]*\)\s*\{", RegexOptions.Compiled);
        private static readonly Regex _JsEndpointRx = new Regex(@"\b(?:app|router|server)\s*\.\s*(get|post|put|patch|delete|all|use)\s*\(\s*['""]([^'""]+)['""]\s*(?:,\s*([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _JsNamedImportRx = new Regex(@"\{\s*([^}]+)\s*\}\s+from\s+['""]([^'""]+)['""]", RegexOptions.Compiled);
        private static readonly Regex _JsReactHookRx = new Regex(@"\b(useEffect|useMemo|useCallback|useState|useReducer)\s*\(", RegexOptions.Compiled);

        private static readonly Regex _PythonClassRx = new Regex(@"^\s*class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex _PythonFunctionRx = new Regex(@"^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex _PythonImportRx = new Regex(@"^\s*(?:from\s+([\w.]+)\s+import|import\s+([\w.]+))", RegexOptions.Compiled);
        private static readonly Regex _PythonFromImportRx = new Regex(@"^\s*from\s+([\w.]+)\s+import\s+(.+)", RegexOptions.Compiled);
        private static readonly Regex _PythonDecoratorEndpointRx = new Regex(@"^\s*@(?:\w+\.)?(get|post|put|patch|delete|route)\s*\(\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _JavaPackageRx = new Regex(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Compiled);
        private static readonly Regex _JavaImportRx = new Regex(@"^\s*import\s+([\w.*]+)\s*;", RegexOptions.Compiled);
        private static readonly Regex _JavaTypeRx = new Regex(@"^\s*(?:public|private|protected|abstract|final|sealed|open|data|internal|static|\s)*\s*(class|interface|record|enum|object)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex _JavaMethodRx = new Regex(@"^\s*(?:(?:public|private|protected|static|final|abstract|suspend|override|open|internal)\s+)*(?:[\w<>\[\],.?]+\s+)+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex _JavaEndpointRx = new Regex(@"^\s*@(GetMapping|PostMapping|PutMapping|PatchMapping|DeleteMapping|RequestMapping)\s*(?:\(\s*(?:value\s*=\s*)?['""]?([^'"")]+)['""]?)?", RegexOptions.Compiled);

        private static readonly Regex _GoPackageRx = new Regex(@"^\s*package\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex _GoFunctionRx = new Regex(@"^\s*func\s+(?:\([^)]+\)\s*)?([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex _GoTypeRx = new Regex(@"^\s*type\s+([A-Za-z_]\w*)\s+(?:struct|interface)\b", RegexOptions.Compiled);
        private static readonly Regex _GoEndpointRx = new Regex(@"\b(?:HandleFunc|Handle)\s*\(\s*['""]([^'""]+)['""]\s*(?:,\s*([A-Za-z_]\w*))?", RegexOptions.Compiled);

        private static readonly Regex _RustFunctionRx = new Regex(@"^\s*(?:pub\s+)?(?:async\s+)?fn\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex _RustTypeRx = new Regex(@"^\s*(?:pub\s+)?(?:struct|enum|trait)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex _RustModRx = new Regex(@"^\s*(?:pub\s+)?mod\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        private static readonly Regex _CallRx = new Regex(@"\b([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?)\s*\(", RegexOptions.Compiled);

        private static readonly HashSet<string> _CallKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "catch", "return", "throw", "new", "typeof", "sizeof",
            "function", "class", "def", "async", "await", "yield", "foreach", "using", "lock",
            "nameof", "import", "require", "console.log", "Math.max", "Math.min"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extract symbols and edges from a supported source file.
        /// </summary>
        public void Extract(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string language,
            string source,
            out List<CodeGraphSymbolRecord> symbols,
            out List<CodeGraphEdgeRecord> edges)
        {
            symbols = new List<CodeGraphSymbolRecord>();
            edges = new List<CodeGraphEdgeRecord>();
            if (String.IsNullOrWhiteSpace(source)) return;

            string normalizedLanguage = (language ?? "").Trim().ToLowerInvariant();
            switch (normalizedLanguage)
            {
                case "csharp":
                    _CSharp.Extract(vesselId, commitSha, path, contentHash, source, out symbols, out edges);
                    foreach (CodeGraphSymbolRecord symbol in symbols)
                    {
                        symbol.Language = "csharp";
                    }
                    ExtractCSharpEndpoints(vesselId, commitSha, path, contentHash, source, symbols, edges);
                    break;
                case "javascript":
                case "typescript":
                    ExtractJavaScriptLike(vesselId, commitSha, path, contentHash, normalizedLanguage, source, symbols, edges);
                    break;
                case "python":
                    ExtractPython(vesselId, commitSha, path, contentHash, source, symbols, edges);
                    break;
                case "java":
                case "kotlin":
                    ExtractJavaLike(vesselId, commitSha, path, contentHash, normalizedLanguage, source, symbols, edges);
                    break;
                case "go":
                    ExtractGo(vesselId, commitSha, path, contentHash, source, symbols, edges);
                    break;
                case "rust":
                    ExtractRust(vesselId, commitSha, path, contentHash, source, symbols, edges);
                    break;
            }

            ResolveKnownCallTargets(symbols, edges);
        }

        /// <summary>
        /// Returns true when the language has graph extraction support.
        /// </summary>
        public static bool SupportsLanguage(string language)
        {
            switch ((language ?? "").Trim().ToLowerInvariant())
            {
                case "csharp":
                case "javascript":
                case "typescript":
                case "python":
                case "java":
                case "kotlin":
                case "go":
                case "rust":
                    return true;
                default:
                    return false;
            }
        }

        #endregion

        #region Private-Methods

        private static void ExtractJavaScriptLike(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string language,
            string source,
            List<CodeGraphSymbolRecord> symbols,
            List<CodeGraphEdgeRecord> edges)
        {
            string module = BuildModuleName(path);
            AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, module, module, 1, contentHash, language, "", "module");

            string[] lines = Lines(source);
            string currentContainer = module;
            string currentCallable = "";
            int callableBraceDepth = 0;
            int braceDepth = 0;
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                int lineNumber = i + 1;

                foreach (Match import in _JsImportRx.Matches(line))
                {
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Imports, path, import.Groups[1].Value, path, lineNumber);
                    AddJavaScriptImportAliases(line, aliases);
                }
                foreach (Match require in _JsRequireRx.Matches(line))
                {
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Imports, path, require.Groups[1].Value, path, lineNumber);
                }

                foreach (Match endpoint in _JsEndpointRx.Matches(line))
                {
                    string verb = endpoint.Groups[1].Value.ToUpperInvariant();
                    string route = endpoint.Groups[2].Value;
                    string name = verb + " " + route;
                    string qualified = module + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Endpoint, name, qualified, lineNumber, contentHash, language, "express", "endpoint", "route");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, String.IsNullOrWhiteSpace(currentCallable) ? module : currentCallable, qualified, path, lineNumber);
                    if (endpoint.Groups[3].Success)
                    {
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, qualified, ResolveAlias(endpoint.Groups[3].Value, aliases), path, lineNumber);
                    }
                }

                Match classMatch = _JsClassRx.Match(line);
                if (classMatch.Success)
                {
                    string name = classMatch.Groups[1].Value;
                    string qualified = module + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Class, name, qualified, lineNumber, contentHash, language, "", "class");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, module, qualified, path, lineNumber);
                    currentContainer = qualified;
                }

                Match functionMatch = _JsFunctionRx.Match(line);
                if (!functionMatch.Success) functionMatch = _JsArrowFunctionRx.Match(line);
                if (!functionMatch.Success && !String.IsNullOrWhiteSpace(currentContainer) && !String.Equals(currentContainer, module, StringComparison.Ordinal))
                {
                    functionMatch = _JsMethodRx.Match(line);
                }

                if (functionMatch.Success)
                {
                    string name = functionMatch.Groups[1].Value;
                    bool component = LooksLikeComponentName(name) || trimmed.Contains("React.", StringComparison.Ordinal);
                    string qualified = currentContainer + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, component ? CodeGraphSymbolKindEnum.Component : CodeGraphSymbolKindEnum.Function, name, qualified, lineNumber, contentHash, language, component ? "react" : "", component ? new[] { "function", "component" } : new[] { "function" });
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, currentContainer, qualified, path, lineNumber);
                    currentCallable = qualified;
                    callableBraceDepth = line.Contains('{') ? braceDepth + CountChar(line, '{') - CountChar(line, '}') : braceDepth + 1;
                }

                if (!String.IsNullOrWhiteSpace(currentCallable))
                {
                    EmitCallEdges(vesselId, commitSha, path, line, lineNumber, currentCallable, edges, aliases);
                    if (_JsReactHookRx.IsMatch(line))
                    {
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.References, currentCallable, "react.hooks", path, lineNumber);
                    }
                }

                braceDepth += CountChar(line, '{') - CountChar(line, '}');
                if (!String.IsNullOrWhiteSpace(currentCallable) && braceDepth < callableBraceDepth)
                {
                    currentCallable = "";
                }
                if (braceDepth <= 0)
                {
                    currentContainer = module;
                }
            }
        }

        private static void ExtractPython(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string source,
            List<CodeGraphSymbolRecord> symbols,
            List<CodeGraphEdgeRecord> edges)
        {
            string module = BuildModuleName(path);
            AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, module, module, 1, contentHash, "python", "", "module");

            string[] lines = Lines(source);
            string currentClass = "";
            string currentCallable = "";
            int classIndent = -1;
            int callableIndent = -1;
            string? pendingEndpointVerb = null;
            string? pendingEndpointRoute = null;
            string? pendingFramework = null;
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                int lineNumber = i + 1;
                int indent = CountIndent(line);

                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                if (classIndent >= 0 && indent <= classIndent && !trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    currentClass = "";
                    classIndent = -1;
                }
                if (callableIndent >= 0 && indent <= callableIndent && !trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    currentCallable = "";
                    callableIndent = -1;
                }

                Match import = _PythonImportRx.Match(line);
                if (import.Success)
                {
                    string target = import.Groups[1].Success ? import.Groups[1].Value : import.Groups[2].Value;
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Imports, path, target, path, lineNumber);
                    AddPythonImportAliases(line, aliases);
                }

                Match decorator = _PythonDecoratorEndpointRx.Match(line);
                if (decorator.Success)
                {
                    pendingEndpointVerb = decorator.Groups[1].Value.Equals("route", StringComparison.OrdinalIgnoreCase) ? "ROUTE" : decorator.Groups[1].Value.ToUpperInvariant();
                    pendingEndpointRoute = decorator.Groups[2].Value;
                    pendingFramework = trimmed.Contains("router.", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("app.", StringComparison.OrdinalIgnoreCase)
                        ? "fastapi"
                        : "python-web";
                    continue;
                }

                Match cls = _PythonClassRx.Match(line);
                if (cls.Success)
                {
                    string name = cls.Groups[1].Value;
                    string qualified = module + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Class, name, qualified, lineNumber, contentHash, "python", "", "class");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, module, qualified, path, lineNumber);
                    currentClass = qualified;
                    classIndent = indent;
                    continue;
                }

                Match fn = _PythonFunctionRx.Match(line);
                if (fn.Success)
                {
                    string name = fn.Groups[1].Value;
                    string container = String.IsNullOrWhiteSpace(currentClass) ? module : currentClass;
                    string qualified = container + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, String.IsNullOrWhiteSpace(currentClass) ? CodeGraphSymbolKindEnum.Function : CodeGraphSymbolKindEnum.Method, name, qualified, lineNumber, contentHash, "python", "", "function");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, container, qualified, path, lineNumber);
                    currentCallable = qualified;
                    callableIndent = indent;

                    if (!String.IsNullOrWhiteSpace(pendingEndpointRoute))
                    {
                        string endpointName = (pendingEndpointVerb ?? "ROUTE") + " " + pendingEndpointRoute;
                        string endpointQualified = module + "." + endpointName;
                        AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Endpoint, endpointName, endpointQualified, lineNumber, contentHash, "python", pendingFramework ?? "python-web", "endpoint", "route");
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, qualified, endpointQualified, path, lineNumber);
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, endpointQualified, qualified, path, lineNumber);
                        pendingEndpointVerb = null;
                        pendingEndpointRoute = null;
                        pendingFramework = null;
                    }
                }

                if (!String.IsNullOrWhiteSpace(currentCallable))
                {
                    EmitCallEdges(vesselId, commitSha, path, line, lineNumber, currentCallable, edges, aliases);
                }
            }
        }

        private static void ExtractJavaLike(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string language,
            string source,
            List<CodeGraphSymbolRecord> symbols,
            List<CodeGraphEdgeRecord> edges)
        {
            string package = BuildModuleName(path);
            string currentType = "";
            string currentCallable = "";
            string? pendingEndpointVerb = null;
            string? pendingEndpointRoute = null;
            string[] lines = Lines(source);
            int braceDepth = 0;
            int callableBraceDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNumber = i + 1;

                Match pkg = _JavaPackageRx.Match(line);
                if (pkg.Success)
                {
                    package = pkg.Groups[1].Value;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, package, package, lineNumber, contentHash, language, "", "package");
                    continue;
                }

                Match import = _JavaImportRx.Match(line);
                if (import.Success)
                {
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Imports, path, import.Groups[1].Value, path, lineNumber);
                }

                Match endpoint = _JavaEndpointRx.Match(line);
                if (endpoint.Success)
                {
                    pendingEndpointVerb = ResolveJavaHttpVerb(endpoint.Groups[1].Value);
                    pendingEndpointRoute = endpoint.Groups[2].Success ? endpoint.Groups[2].Value.Trim() : "/";
                    continue;
                }

                Match type = _JavaTypeRx.Match(line);
                if (type.Success)
                {
                    string kindText = type.Groups[1].Value;
                    string name = type.Groups[2].Value;
                    CodeGraphSymbolKindEnum kind = kindText.Equals("interface", StringComparison.OrdinalIgnoreCase)
                        ? CodeGraphSymbolKindEnum.Interface
                        : kindText.Equals("enum", StringComparison.OrdinalIgnoreCase)
                            ? CodeGraphSymbolKindEnum.Enum
                            : CodeGraphSymbolKindEnum.Class;
                    string qualified = package + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, kind, name, qualified, lineNumber, contentHash, language, "", "type");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, package, qualified, path, lineNumber);
                    currentType = qualified;
                }

                Match method = _JavaMethodRx.Match(line);
                if (method.Success && !String.IsNullOrWhiteSpace(currentType))
                {
                    string name = method.Groups[1].Value;
                    string qualified = currentType + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Method, name, qualified, lineNumber, contentHash, language, "", "method");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, currentType, qualified, path, lineNumber);
                    currentCallable = qualified;
                    callableBraceDepth = line.Contains('{') ? braceDepth + CountChar(line, '{') - CountChar(line, '}') : braceDepth + 1;

                    if (!String.IsNullOrWhiteSpace(pendingEndpointRoute))
                    {
                        string endpointName = (pendingEndpointVerb ?? "ROUTE") + " " + pendingEndpointRoute;
                        string endpointQualified = currentType + "." + endpointName;
                        AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Endpoint, endpointName, endpointQualified, lineNumber, contentHash, language, "spring", "endpoint", "route");
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, qualified, endpointQualified, path, lineNumber);
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, endpointQualified, qualified, path, lineNumber);
                        pendingEndpointVerb = null;
                        pendingEndpointRoute = null;
                    }
                }

                if (!String.IsNullOrWhiteSpace(currentCallable))
                {
                    EmitCallEdges(vesselId, commitSha, path, line, lineNumber, currentCallable, edges);
                }

                braceDepth += CountChar(line, '{') - CountChar(line, '}');
                if (!String.IsNullOrWhiteSpace(currentCallable) && braceDepth < callableBraceDepth)
                {
                    currentCallable = "";
                }
            }
        }

        private static void ExtractGo(string vesselId, string commitSha, string path, string contentHash, string source, List<CodeGraphSymbolRecord> symbols, List<CodeGraphEdgeRecord> edges)
        {
            string package = BuildModuleName(path);
            string currentCallable = "";
            string[] lines = Lines(source);
            int braceDepth = 0;
            int callableBraceDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNumber = i + 1;

                Match pkg = _GoPackageRx.Match(line);
                if (pkg.Success)
                {
                    package = pkg.Groups[1].Value;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, package, package, lineNumber, contentHash, "go", "", "package");
                }

                Match type = _GoTypeRx.Match(line);
                if (type.Success)
                {
                    string name = type.Groups[1].Value;
                    string qualified = package + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Class, name, qualified, lineNumber, contentHash, "go", "", "type");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, package, qualified, path, lineNumber);
                }

                Match fn = _GoFunctionRx.Match(line);
                if (fn.Success)
                {
                    string name = fn.Groups[1].Value;
                    string qualified = package + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Function, name, qualified, lineNumber, contentHash, "go", "", "function");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, package, qualified, path, lineNumber);
                    currentCallable = qualified;
                    callableBraceDepth = line.Contains('{') ? braceDepth + CountChar(line, '{') - CountChar(line, '}') : braceDepth + 1;
                }

                foreach (Match endpoint in _GoEndpointRx.Matches(line))
                {
                    string endpointName = "ROUTE " + endpoint.Groups[1].Value;
                    string endpointQualified = package + "." + endpointName;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Endpoint, endpointName, endpointQualified, lineNumber, contentHash, "go", "net/http", "endpoint", "route");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, String.IsNullOrWhiteSpace(currentCallable) ? package : currentCallable, endpointQualified, path, lineNumber);
                    if (endpoint.Groups[2].Success)
                    {
                        AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, endpointQualified, endpoint.Groups[2].Value, path, lineNumber);
                    }
                }

                if (!String.IsNullOrWhiteSpace(currentCallable))
                {
                    EmitCallEdges(vesselId, commitSha, path, line, lineNumber, currentCallable, edges);
                }

                braceDepth += CountChar(line, '{') - CountChar(line, '}');
                if (!String.IsNullOrWhiteSpace(currentCallable) && braceDepth < callableBraceDepth)
                {
                    currentCallable = "";
                }
            }
        }

        private static void ExtractRust(string vesselId, string commitSha, string path, string contentHash, string source, List<CodeGraphSymbolRecord> symbols, List<CodeGraphEdgeRecord> edges)
        {
            string module = BuildModuleName(path);
            AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, module, module, 1, contentHash, "rust", "", "module");
            string currentCallable = "";
            string[] lines = Lines(source);
            int braceDepth = 0;
            int callableBraceDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNumber = i + 1;

                Match mod = _RustModRx.Match(line);
                if (mod.Success)
                {
                    string name = mod.Groups[1].Value;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Module, name, module + "." + name, lineNumber, contentHash, "rust", "", "module");
                }

                Match type = _RustTypeRx.Match(line);
                if (type.Success)
                {
                    string name = type.Groups[1].Value;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Class, name, module + "." + name, lineNumber, contentHash, "rust", "", "type");
                }

                Match fn = _RustFunctionRx.Match(line);
                if (fn.Success)
                {
                    string name = fn.Groups[1].Value;
                    string qualified = module + "." + name;
                    AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Function, name, qualified, lineNumber, contentHash, "rust", "", "function");
                    AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, module, qualified, path, lineNumber);
                    currentCallable = qualified;
                    callableBraceDepth = line.Contains('{') ? braceDepth + CountChar(line, '{') - CountChar(line, '}') : braceDepth + 1;
                }

                if (!String.IsNullOrWhiteSpace(currentCallable))
                {
                    EmitCallEdges(vesselId, commitSha, path, line, lineNumber, currentCallable, edges);
                }

                braceDepth += CountChar(line, '{') - CountChar(line, '}');
                if (!String.IsNullOrWhiteSpace(currentCallable) && braceDepth < callableBraceDepth)
                {
                    currentCallable = "";
                }
            }
        }

        private static void ExtractCSharpEndpoints(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string source,
            List<CodeGraphSymbolRecord> symbols,
            List<CodeGraphEdgeRecord> edges)
        {
            string? pendingVerb = null;
            string? pendingRoute = null;
            string[] lines = Lines(source);
            foreach (CodeGraphSymbolRecord symbol in symbols)
            {
                symbol.Language = "csharp";
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                int lineNumber = i + 1;
                if (trimmed.StartsWith("[Http", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("[Route", StringComparison.OrdinalIgnoreCase))
                {
                    pendingVerb = ResolveCSharpHttpVerb(trimmed);
                    pendingRoute = ExtractFirstQuotedString(trimmed) ?? "/";
                    continue;
                }

                if (pendingRoute == null) continue;

                CodeGraphSymbolRecord? method = symbols
                    .Where(s => (s.Kind == CodeGraphSymbolKindEnum.Method || s.Kind == CodeGraphSymbolKindEnum.Constructor) && s.StartLine >= lineNumber)
                    .OrderBy(s => s.StartLine)
                    .FirstOrDefault();
                if (method == null || method.StartLine > lineNumber + 4) continue;

                string endpointName = (pendingVerb ?? "ROUTE") + " " + pendingRoute;
                string endpointQualified = method.QualifiedName + "." + endpointName;
                AddSymbol(symbols, vesselId, commitSha, path, CodeGraphSymbolKindEnum.Endpoint, endpointName, endpointQualified, method.StartLine, contentHash, "csharp", "aspnet", "endpoint", "route");
                AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Contains, method.QualifiedName, endpointQualified, path, method.StartLine);
                AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, endpointQualified, method.QualifiedName, path, method.StartLine);
                pendingVerb = null;
                pendingRoute = null;
            }
        }

        private static void EmitCallEdges(
            string vesselId,
            string commitSha,
            string path,
            string line,
            int lineNumber,
            string sourceSymbol,
            List<CodeGraphEdgeRecord> edges,
            Dictionary<string, string>? aliases = null)
        {
            foreach (Match call in _CallRx.Matches(line))
            {
                string target = call.Groups[1].Value.Trim();
                if (String.IsNullOrWhiteSpace(target)) continue;
                if (_CallKeywords.Contains(target)) continue;
                string last = target.Contains('.') ? target.Substring(target.LastIndexOf('.') + 1) : target;
                if (_CallKeywords.Contains(last)) continue;
                AddEdge(edges, vesselId, commitSha, CodeGraphEdgeKindEnum.Calls, sourceSymbol, ResolveAlias(target, aliases), path, lineNumber);
            }
        }

        private static void ResolveKnownCallTargets(List<CodeGraphSymbolRecord> symbols, List<CodeGraphEdgeRecord> edges)
        {
            if (symbols.Count == 0 || edges.Count == 0) return;

            Dictionary<string, List<CodeGraphSymbolRecord>> simpleNameIndex = symbols
                .Where(IsCallableTarget)
                .GroupBy(s => s.SimpleName ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => !String.IsNullOrWhiteSpace(g.Key))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            Dictionary<string, CodeGraphSymbolRecord> qualifiedIndex = symbols
                .Where(s => !String.IsNullOrWhiteSpace(s.QualifiedName))
                .GroupBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphEdgeRecord edge in edges)
            {
                if (edge.Kind != CodeGraphEdgeKindEnum.Calls) continue;
                string resolved = ResolveKnownCallTarget(edge.TargetSymbol, simpleNameIndex, qualifiedIndex);
                if (!String.IsNullOrWhiteSpace(resolved)) edge.TargetSymbol = resolved;
            }
        }

        private static bool IsCallableTarget(CodeGraphSymbolRecord symbol)
        {
            switch (symbol.Kind)
            {
                case CodeGraphSymbolKindEnum.Class:
                case CodeGraphSymbolKindEnum.Component:
                case CodeGraphSymbolKindEnum.Constructor:
                case CodeGraphSymbolKindEnum.Endpoint:
                case CodeGraphSymbolKindEnum.Function:
                case CodeGraphSymbolKindEnum.Method:
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveKnownCallTarget(
            string target,
            Dictionary<string, List<CodeGraphSymbolRecord>> simpleNameIndex,
            Dictionary<string, CodeGraphSymbolRecord> qualifiedIndex)
        {
            if (String.IsNullOrWhiteSpace(target)) return target;
            string trimmed = target.Trim();

            if (qualifiedIndex.ContainsKey(trimmed)) return trimmed;

            List<CodeGraphSymbolRecord> suffixMatches = qualifiedIndex.Values
                .Where(s => s.QualifiedName.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (suffixMatches.Count == 1) return suffixMatches[0].QualifiedName;

            if (!trimmed.Contains(".", StringComparison.Ordinal) &&
                simpleNameIndex.TryGetValue(trimmed, out List<CodeGraphSymbolRecord>? simpleMatches) &&
                simpleMatches.Count == 1)
            {
                return simpleMatches[0].QualifiedName;
            }

            return trimmed;
        }

        private static void AddJavaScriptImportAliases(string line, Dictionary<string, string> aliases)
        {
            Match named = _JsNamedImportRx.Match(line);
            if (!named.Success) return;

            foreach (string part in named.Groups[1].Value.Split(','))
            {
                string[] pieces = part.Trim().Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 1)
                {
                    string name = pieces[0].Trim();
                    if (!String.IsNullOrWhiteSpace(name)) aliases[name] = name;
                }
                else if (pieces.Length == 2)
                {
                    string original = pieces[0].Trim();
                    string alias = pieces[1].Trim();
                    if (!String.IsNullOrWhiteSpace(original) && !String.IsNullOrWhiteSpace(alias)) aliases[alias] = original;
                }
            }
        }

        private static void AddPythonImportAliases(string line, Dictionary<string, string> aliases)
        {
            Match fromImport = _PythonFromImportRx.Match(line);
            if (!fromImport.Success) return;

            foreach (string part in fromImport.Groups[2].Value.Split(','))
            {
                string[] pieces = part.Trim().Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 1)
                {
                    string name = pieces[0].Trim();
                    if (!String.IsNullOrWhiteSpace(name) && name != "*") aliases[name] = name;
                }
                else if (pieces.Length == 2)
                {
                    string original = pieces[0].Trim();
                    string alias = pieces[1].Trim();
                    if (!String.IsNullOrWhiteSpace(original) && !String.IsNullOrWhiteSpace(alias)) aliases[alias] = original;
                }
            }
        }

        private static string ResolveAlias(string target, Dictionary<string, string>? aliases)
        {
            if (aliases == null || aliases.Count == 0 || String.IsNullOrWhiteSpace(target)) return target;
            if (aliases.TryGetValue(target, out string? resolved)) return resolved;
            int dot = target.LastIndexOf('.');
            if (dot > 0)
            {
                string last = target.Substring(dot + 1);
                if (aliases.TryGetValue(last, out resolved)) return resolved;
            }
            return target;
        }

        private static void AddSymbol(
            List<CodeGraphSymbolRecord> symbols,
            string vesselId,
            string commitSha,
            string path,
            CodeGraphSymbolKindEnum kind,
            string simple,
            string qualified,
            int line,
            string contentHash,
            string language,
            string framework,
            params string[] tags)
        {
            symbols.Add(new CodeGraphSymbolRecord
            {
                VesselId = vesselId,
                CommitSha = commitSha,
                Path = path,
                Kind = kind,
                SimpleName = simple,
                QualifiedName = qualified,
                StartLine = Math.Max(1, line),
                EndLine = Math.Max(1, line),
                ContentHash = contentHash,
                Language = language ?? "",
                Framework = framework ?? "",
                Tags = tags.Where(t => !String.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        private static void AddEdge(List<CodeGraphEdgeRecord> edges, string vesselId, string commitSha, CodeGraphEdgeKindEnum kind, string source, string target, string path, int line)
        {
            if (String.IsNullOrWhiteSpace(source) || String.IsNullOrWhiteSpace(target)) return;
            edges.Add(new CodeGraphEdgeRecord
            {
                VesselId = vesselId,
                CommitSha = commitSha,
                Kind = kind,
                SourceSymbol = source,
                TargetSymbol = target,
                SourcePath = path,
                SourceLine = Math.Max(1, line)
            });
        }

        private static string[] Lines(string source)
        {
            return (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string BuildModuleName(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            string withoutExtension = Path.ChangeExtension(normalized, null) ?? normalized;
            return withoutExtension.Replace('/', '.').Replace('-', '_');
        }

        private static int CountChar(string value, char target)
        {
            int count = 0;
            foreach (char c in value ?? "")
            {
                if (c == target) count++;
            }
            return count;
        }

        private static int CountIndent(string line)
        {
            int count = 0;
            foreach (char c in line ?? "")
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
        }

        private static bool LooksLikeComponentName(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && Char.IsUpper(name[0]);
        }

        private static string ResolveJavaHttpVerb(string attribute)
        {
            if (attribute.StartsWith("Get", StringComparison.OrdinalIgnoreCase)) return "GET";
            if (attribute.StartsWith("Post", StringComparison.OrdinalIgnoreCase)) return "POST";
            if (attribute.StartsWith("Put", StringComparison.OrdinalIgnoreCase)) return "PUT";
            if (attribute.StartsWith("Patch", StringComparison.OrdinalIgnoreCase)) return "PATCH";
            if (attribute.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)) return "DELETE";
            return "ROUTE";
        }

        private static string ResolveCSharpHttpVerb(string attribute)
        {
            if (attribute.StartsWith("[HttpGet", StringComparison.OrdinalIgnoreCase)) return "GET";
            if (attribute.StartsWith("[HttpPost", StringComparison.OrdinalIgnoreCase)) return "POST";
            if (attribute.StartsWith("[HttpPut", StringComparison.OrdinalIgnoreCase)) return "PUT";
            if (attribute.StartsWith("[HttpPatch", StringComparison.OrdinalIgnoreCase)) return "PATCH";
            if (attribute.StartsWith("[HttpDelete", StringComparison.OrdinalIgnoreCase)) return "DELETE";
            return "ROUTE";
        }

        private static string? ExtractFirstQuotedString(string value)
        {
            Match match = Regex.Match(value ?? "", @"['""]([^'""]+)['""]");
            return match.Success ? match.Groups[1].Value : null;
        }

        #endregion
    }
}
