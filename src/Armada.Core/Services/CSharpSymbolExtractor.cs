namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Regex-based C# symbol and edge extractor used to populate code-graph sidecar files.
    /// Handles namespaces, classes, interfaces, records, enums, structs, methods, constructors,
    /// properties, fields, inheritance/implementation declarations, using-import edges, and
    /// simple method-call edges within method bodies.
    /// </summary>
    public class CSharpSymbolExtractor
    {
        #region Private-Members

        private static readonly Regex _NamespaceRx = new Regex(
            @"^\s*(?:file\s+)?namespace\s+([\w.]+)",
            RegexOptions.Compiled);

        private static readonly Regex _TypeRx = new Regex(
            @"^\s*(?:(?:public|internal|protected|private|static|abstract|sealed|partial|readonly|file)\s+)*" +
            @"(class|interface|record\s+struct|record|enum|struct|delegate)\s+([\w<>,\s]+?)(?:\s*[:,({<]|$)",
            RegexOptions.Compiled);

        private static readonly Regex _MethodRx = new Regex(
            @"^\s*(?:(?:public|internal|protected|private|static|abstract|virtual|override|sealed|async|extern|new|partial|readonly)\s+)*" +
            @"(?!if\b|while\b|for\b|foreach\b|switch\b|return\b|using\b|throw\b|catch\b)" +
            @"(?:[\w<>\[\],\s\?]+?\s+)" +
            @"([\w]+)\s*(?:<[^>]*>)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex _PropFieldRx = new Regex(
            @"^\s*(?:(?:public|internal|protected|private|static|abstract|virtual|override|sealed|readonly|const|volatile|new)\s+)*" +
            @"(?:[\w<>\[\],\s\?]+?\s+)" +
            @"([\w]+)\s*(?:\{|;|=)",
            RegexOptions.Compiled);

        private static readonly Regex _InheritsRx = new Regex(
            @":\s*([\w<>.,\s]+?)(?:\s*(?:where|{|$))",
            RegexOptions.Compiled);

        private static readonly Regex _UsingRx = new Regex(
            @"^\s*using\s+(?:static\s+)?([\w.]+)\s*;",
            RegexOptions.Compiled);

        private static readonly Regex _CallRx = new Regex(
            @"\b([\w]+)\s*\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> _LanguageKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "while", "for", "foreach", "switch", "case", "return", "break",
            "continue", "throw", "try", "catch", "finally", "using", "await", "new", "typeof",
            "sizeof", "nameof", "default", "checked", "unchecked", "lock", "fixed", "goto",
            "do", "in", "out", "ref", "is", "as", "base", "this", "void", "var", "when",
            "yield", "from", "select", "where", "group", "into", "orderby", "join", "let",
            "on", "equals", "ascending", "descending", "async", "await", "get", "set",
            "add", "remove", "value", "global", "partial", "dynamic", "true", "false", "null"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extract symbols and edges from C# source text.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="commitSha">Commit SHA.</param>
        /// <param name="path">Repository-relative file path.</param>
        /// <param name="contentHash">SHA-256 hash of the file content.</param>
        /// <param name="source">Full C# source text.</param>
        /// <param name="symbols">Emitted symbol records.</param>
        /// <param name="edges">Emitted edge records.</param>
        public void Extract(
            string vesselId,
            string commitSha,
            string path,
            string contentHash,
            string source,
            out List<CodeGraphSymbolRecord> symbols,
            out List<CodeGraphEdgeRecord> edges)
        {
            symbols = new List<CodeGraphSymbolRecord>();
            edges = new List<CodeGraphEdgeRecord>();

            if (String.IsNullOrWhiteSpace(source)) return;

            string[] lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            string currentNamespace = "";
            string currentType = "";
            string currentMethod = "";
            bool inMethodBody = false;
            int braceDepthAtMethodEntry = 0;
            int braceDepth = 0;
            int braceDepthAtTypeEntry = 0;
            bool inTypeBody = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string trimmed = raw.TrimEnd();
                int lineNumber = i + 1;

                // Track brace depth
                foreach (char c in raw)
                {
                    if (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                }

                // Skip pure comment lines
                string stripped = trimmed.TrimStart();
                if (stripped.StartsWith("//") || stripped.StartsWith("*") || stripped.StartsWith("/*")) continue;

                // Using/import directives (only at top level)
                if (!inTypeBody && !inMethodBody)
                {
                    Match usingMatch = _UsingRx.Match(trimmed);
                    if (usingMatch.Success)
                    {
                        string imported = usingMatch.Groups[1].Value.Trim();
                        edges.Add(new CodeGraphEdgeRecord
                        {
                            VesselId = vesselId,
                            CommitSha = commitSha,
                            Kind = CodeGraphEdgeKindEnum.Imports,
                            SourceSymbol = path,
                            TargetSymbol = imported,
                            SourcePath = path,
                            SourceLine = lineNumber
                        });
                        continue;
                    }
                }

                // Namespace
                Match nsMatch = _NamespaceRx.Match(trimmed);
                if (nsMatch.Success)
                {
                    currentNamespace = nsMatch.Groups[1].Value.Trim();
                    symbols.Add(new CodeGraphSymbolRecord
                    {
                        VesselId = vesselId,
                        CommitSha = commitSha,
                        Path = path,
                        Kind = CodeGraphSymbolKindEnum.Namespace,
                        SimpleName = currentNamespace,
                        QualifiedName = currentNamespace,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        ContentHash = contentHash
                    });
                    continue;
                }

                // Type declarations (class, interface, record, enum, struct, delegate)
                if (!inMethodBody)
                {
                    Match typeMatch = _TypeRx.Match(trimmed);
                    if (typeMatch.Success)
                    {
                        string keyword = typeMatch.Groups[1].Value.Trim();
                        string rawName = typeMatch.Groups[2].Value.Trim();
                        // Strip generic parameters from simple name
                        int ltIdx = rawName.IndexOf('<');
                        string simpleName = ltIdx >= 0 ? rawName.Substring(0, ltIdx).Trim() : rawName;
                        simpleName = simpleName.Trim();

                        if (!String.IsNullOrWhiteSpace(simpleName) && !_LanguageKeywords.Contains(simpleName))
                        {
                            CodeGraphSymbolKindEnum kind = ResolveTypeKind(keyword);
                            string qualifiedName = BuildQualified(currentNamespace, simpleName);

                            symbols.Add(new CodeGraphSymbolRecord
                            {
                                VesselId = vesselId,
                                CommitSha = commitSha,
                                Path = path,
                                Kind = kind,
                                SimpleName = simpleName,
                                QualifiedName = qualifiedName,
                                StartLine = lineNumber,
                                EndLine = lineNumber,
                                ContentHash = contentHash
                            });

                            // Inheritance/implementation edges
                            int colonIdx = trimmed.IndexOf(':', typeMatch.Index + typeMatch.Length - 1);
                            if (colonIdx >= 0)
                            {
                                string afterColon = trimmed.Substring(colonIdx + 1);
                                EmitInheritanceEdges(vesselId, commitSha, path, qualifiedName, afterColon, lineNumber, kind, edges);
                            }

                            if (!inTypeBody)
                            {
                                currentType = qualifiedName;
                                braceDepthAtTypeEntry = braceDepth;
                                inTypeBody = true;
                            }
                        }
                        continue;
                    }
                }

                // Detect leaving type body
                if (inTypeBody && !inMethodBody && braceDepth < braceDepthAtTypeEntry)
                {
                    inTypeBody = false;
                    currentType = "";
                }

                // Method / constructor declarations (inside type body, not in method body)
                if (inTypeBody && !inMethodBody)
                {
                    Match methodMatch = _MethodRx.Match(trimmed);
                    if (methodMatch.Success)
                    {
                        string simpleName = methodMatch.Groups[1].Value.Trim();
                        if (!String.IsNullOrWhiteSpace(simpleName) && !_LanguageKeywords.Contains(simpleName))
                        {
                            // Distinguish constructor: simple name matches enclosing type simple name
                            string enclosingSimple = currentType.Contains('.') ? currentType.Substring(currentType.LastIndexOf('.') + 1) : currentType;
                            CodeGraphSymbolKindEnum memberKind = simpleName == enclosingSimple
                                ? CodeGraphSymbolKindEnum.Constructor
                                : CodeGraphSymbolKindEnum.Method;

                            string qualifiedMethod = BuildQualified(currentType, simpleName);

                            symbols.Add(new CodeGraphSymbolRecord
                            {
                                VesselId = vesselId,
                                CommitSha = commitSha,
                                Path = path,
                                Kind = memberKind,
                                SimpleName = simpleName,
                                QualifiedName = qualifiedMethod,
                                StartLine = lineNumber,
                                EndLine = lineNumber,
                                ContentHash = contentHash
                            });

                            // Contains edge from type to method
                            if (!String.IsNullOrWhiteSpace(currentType))
                            {
                                edges.Add(new CodeGraphEdgeRecord
                                {
                                    VesselId = vesselId,
                                    CommitSha = commitSha,
                                    Kind = CodeGraphEdgeKindEnum.Contains,
                                    SourceSymbol = currentType,
                                    TargetSymbol = qualifiedMethod,
                                    SourcePath = path,
                                    SourceLine = lineNumber
                                });
                            }

                            if (trimmed.Contains('{') || !trimmed.TrimEnd().EndsWith(";"))
                            {
                                currentMethod = qualifiedMethod;
                                inMethodBody = true;
                                braceDepthAtMethodEntry = braceDepth;
                            }
                        }
                        continue;
                    }

                    // Property/field (best-effort, no method body tracking needed)
                    Match propMatch = _PropFieldRx.Match(trimmed);
                    if (propMatch.Success)
                    {
                        string simpleName = propMatch.Groups[1].Value.Trim();
                        if (!String.IsNullOrWhiteSpace(simpleName) && !_LanguageKeywords.Contains(simpleName))
                        {
                            bool isField = trimmed.TrimEnd().EndsWith(";") && !trimmed.Contains('{');
                            CodeGraphSymbolKindEnum memberKind = isField ? CodeGraphSymbolKindEnum.Field : CodeGraphSymbolKindEnum.Property;
                            string qualifiedMember = BuildQualified(currentType, simpleName);

                            symbols.Add(new CodeGraphSymbolRecord
                            {
                                VesselId = vesselId,
                                CommitSha = commitSha,
                                Path = path,
                                Kind = memberKind,
                                SimpleName = simpleName,
                                QualifiedName = qualifiedMember,
                                StartLine = lineNumber,
                                EndLine = lineNumber,
                                ContentHash = contentHash
                            });

                            if (!String.IsNullOrWhiteSpace(currentType))
                            {
                                edges.Add(new CodeGraphEdgeRecord
                                {
                                    VesselId = vesselId,
                                    CommitSha = commitSha,
                                    Kind = CodeGraphEdgeKindEnum.Contains,
                                    SourceSymbol = currentType,
                                    TargetSymbol = qualifiedMember,
                                    SourcePath = path,
                                    SourceLine = lineNumber
                                });
                            }
                        }
                    }
                }

                // Inside method body: collect call edges
                if (inMethodBody)
                {
                    if (braceDepth < braceDepthAtMethodEntry)
                    {
                        inMethodBody = false;
                        currentMethod = "";
                    }
                    else
                    {
                        MatchCollection callMatches = _CallRx.Matches(trimmed);
                        foreach (Match callMatch in callMatches)
                        {
                            string callee = callMatch.Groups[1].Value.Trim();
                            if (!String.IsNullOrWhiteSpace(callee) && !_LanguageKeywords.Contains(callee))
                            {
                                edges.Add(new CodeGraphEdgeRecord
                                {
                                    VesselId = vesselId,
                                    CommitSha = commitSha,
                                    Kind = CodeGraphEdgeKindEnum.Calls,
                                    SourceSymbol = currentMethod,
                                    TargetSymbol = callee,
                                    SourcePath = path,
                                    SourceLine = lineNumber
                                });
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Private-Methods

        private static CodeGraphSymbolKindEnum ResolveTypeKind(string keyword)
        {
            if (keyword.StartsWith("record", StringComparison.Ordinal)) return CodeGraphSymbolKindEnum.Record;
            switch (keyword)
            {
                case "class": return CodeGraphSymbolKindEnum.Class;
                case "interface": return CodeGraphSymbolKindEnum.Interface;
                case "enum": return CodeGraphSymbolKindEnum.Enum;
                case "struct": return CodeGraphSymbolKindEnum.Struct;
                case "delegate": return CodeGraphSymbolKindEnum.Delegate;
                default: return CodeGraphSymbolKindEnum.Unknown;
            }
        }

        private static string BuildQualified(string prefix, string name)
        {
            if (String.IsNullOrWhiteSpace(prefix)) return name;
            return prefix + "." + name;
        }

        private static void EmitInheritanceEdges(
            string vesselId,
            string commitSha,
            string path,
            string sourceSymbol,
            string afterColon,
            int lineNumber,
            CodeGraphSymbolKindEnum kind,
            List<CodeGraphEdgeRecord> edges)
        {
            // Strip where-clauses and opening brace
            int whereIdx = afterColon.IndexOf("where", StringComparison.Ordinal);
            if (whereIdx >= 0) afterColon = afterColon.Substring(0, whereIdx);
            int braceIdx = afterColon.IndexOf('{');
            if (braceIdx >= 0) afterColon = afterColon.Substring(0, braceIdx);

            string[] parts = afterColon.Split(',');
            foreach (string part in parts)
            {
                string target = part.Trim();
                // Strip generic args for name only
                int lt = target.IndexOf('<');
                if (lt >= 0) target = target.Substring(0, lt).Trim();
                if (String.IsNullOrWhiteSpace(target)) continue;
                if (_LanguageKeywords.Contains(target)) continue;

                // Interfaces start with I conventionally; use Implements edge for interface kind sources too
                CodeGraphEdgeKindEnum edgeKind = target.StartsWith("I", StringComparison.Ordinal) && target.Length > 1 && char.IsUpper(target[1])
                    ? CodeGraphEdgeKindEnum.Implements
                    : CodeGraphEdgeKindEnum.Inherits;

                // If the declared symbol is an interface, all bases are Implements
                if (kind == CodeGraphSymbolKindEnum.Interface) edgeKind = CodeGraphEdgeKindEnum.Implements;

                edges.Add(new CodeGraphEdgeRecord
                {
                    VesselId = vesselId,
                    CommitSha = commitSha,
                    Kind = edgeKind,
                    SourceSymbol = sourceSymbol,
                    TargetSymbol = target,
                    SourcePath = path,
                    SourceLine = lineNumber
                });
            }
        }

        #endregion
    }
}
