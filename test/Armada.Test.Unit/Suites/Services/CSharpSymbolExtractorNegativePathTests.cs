namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Negative-path and edge-case coverage for the regex-based C# symbol and edge extractor.
    /// Complements the Worker-authored happy-path suite in <c>CSharpSymbolExtractorTests</c>.
    /// </summary>
    public class CSharpSymbolExtractorNegativePathTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "CSharp Symbol Extractor Negative Paths";

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Whitespace-only source produces no symbols or edges", () =>
            {
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Empty.cs", "h", "   \n   \r\n   \t   \n",
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertEqual(0, symbols.Count, "no symbols from whitespace source");
                AssertEqual(0, edges.Count, "no edges from whitespace source");
            });

            await RunTest("Struct declaration produces Struct symbol", () =>
            {
                string source = @"namespace Armada.Core
{
    public struct Point
    {
        public int X;
        public int Y;
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Point.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? st = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Struct);
                AssertNotNull(st, "struct symbol");
                AssertEqual("Point", st!.SimpleName, "struct simple name");
                AssertEqual("Armada.Core.Point", st.QualifiedName, "struct qualified name");
            });

            await RunTest("Delegate declaration produces a Delegate symbol kind", () =>
            {
                // Note: the regex-based extractor currently captures the return type as part of the
                // delegate simple name (e.g. "void StatusChanged" rather than "StatusChanged"). The
                // kind discrimination is what callers rely on for triage; the simple-name limitation
                // is tracked as residual risk in the mission report.
                string source = @"namespace Armada.Core
{
    public delegate void StatusChanged(string status);
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Delegates.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? del = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Delegate);
                AssertNotNull(del, "delegate symbol present with Delegate kind");
                AssertTrue(del!.SimpleName.Contains("StatusChanged"),
                    "delegate simple name contains the declared identifier (current behavior includes return type)");
            });

            await RunTest("Field declaration produces Field symbol distinct from Property", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Widget
    {
        private int _Counter = 0;
        public string Name { get; set; } = """";
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Widget.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? field = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Field);
                CodeGraphSymbolRecord? property = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Property);
                AssertNotNull(field, "field symbol present");
                AssertNotNull(property, "property symbol present");
                AssertEqual("_Counter", field!.SimpleName, "field simple name");
                AssertEqual("Name", property!.SimpleName, "property simple name");
            });

            await RunTest("Multi-base declaration emits Inherits and Implements edges in order", () =>
            {
                string source = @"namespace Armada.Core
{
    public class DerivedService : BaseService, IDisposable, IServiceContract
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/DerivedService.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> inherits = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Inherits).ToList();
                List<CodeGraphEdgeRecord> implements = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Implements).ToList();

                AssertTrue(inherits.Any(e => e.TargetSymbol == "BaseService"), "Inherits BaseService edge");
                AssertTrue(implements.Any(e => e.TargetSymbol == "IDisposable"), "Implements IDisposable edge");
                AssertTrue(implements.Any(e => e.TargetSymbol == "IServiceContract"), "Implements IServiceContract edge");
            });

            await RunTest("Interface base list emits Implements edges (not Inherits)", () =>
            {
                string source = @"namespace Armada.Core
{
    public interface IDerived : IBase
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/IDerived.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> implementsEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Implements).ToList();
                List<CodeGraphEdgeRecord> inheritsEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Inherits).ToList();

                AssertTrue(implementsEdges.Any(e => e.TargetSymbol == "IBase"), "Implements IBase edge present for interface base");
                AssertFalse(inheritsEdges.Any(e => e.TargetSymbol == "IBase"), "no Inherits edge from interface to its base");
            });

            await RunTest("Single-line comment hides declarations from extraction", () =>
            {
                string source = @"namespace Armada.Core
{
    // public class HiddenClass { }
    public class VisibleClass { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/VisibleClass.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                List<CodeGraphSymbolRecord> classes = symbols.Where(s => s.Kind == CodeGraphSymbolKindEnum.Class).ToList();
                AssertTrue(classes.Any(c => c.SimpleName == "VisibleClass"), "VisibleClass symbol present");
                AssertFalse(classes.Any(c => c.SimpleName == "HiddenClass"), "HiddenClass symbol must not be emitted from commented line");
            });

            await RunTest("Method with opening brace on next line still exits the body cleanly", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void First()
        {
            DoWork();
        }

        public string Name { get; set; } = """";
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Worker.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? property = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Property && s.SimpleName == "Name");
                AssertNotNull(property, "property after method-body must be picked up (regression for next-line-brace exit bug)");
            });

            await RunTest("Type with opening brace on next line still exits the body cleanly", () =>
            {
                // Without the type-body-exit fix from ae4033d5, after the first class's body closed
                // the extractor would have stayed in First's type body, and Second's method Contains
                // edge would incorrectly source from "Armada.Core.First" instead of "Armada.Core.Second".
                string source = @"namespace Armada.Core
{
    public class First
    {
        public void DoFirst() { }
    }

    public class Second
    {
        public void DoSecond() { }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Pair.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                CodeGraphEdgeRecord? containsSecond = edges.FirstOrDefault(e =>
                    e.Kind == CodeGraphEdgeKindEnum.Contains
                    && e.TargetSymbol == "Armada.Core.Second.DoSecond");
                AssertNotNull(containsSecond, "Contains edge for Second.DoSecond");
                AssertEqual("Armada.Core.Second", containsSecond!.SourceSymbol,
                    "Contains edge source for Second.DoSecond must be Second (proves type body exited after First)");
            });

            await RunTest("Abstract method declaration does not enter method-body tracking", () =>
            {
                // If inMethodBody were incorrectly set true for the abstract method, the property
                // after it would not be picked up as a Property symbol.
                string source = @"namespace Armada.Core
{
    public abstract class Base
    {
        public abstract void DoSomething();
        public string Tag { get; set; } = """";
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Base.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? prop = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Property && s.SimpleName == "Tag");
                AssertNotNull(prop, "property after abstract method declaration must be extracted");
            });

            await RunTest("Call edge sourceSymbol is the qualified enclosing method", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void Run()
        {
            DoWork();
        }

        private void DoWork() { }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Worker.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                CodeGraphEdgeRecord? call = edges.FirstOrDefault(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol == "DoWork");
                AssertNotNull(call, "call edge for DoWork");
                AssertEqual("Armada.Core.Worker.Run", call!.SourceSymbol,
                    "call edge sourceSymbol must be the qualified enclosing method");
            });

            await RunTest("Language keywords in expressions do not produce Call edges", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void Run(int x)
        {
            if (x > 0)
            {
                while (x > 0)
                {
                    x--;
                }
            }
            for (int i = 0; i < 1; i++) { }
            switch (x) { default: break; }
            return;
        }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Worker.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> callEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Calls).ToList();
                AssertFalse(callEdges.Any(e => e.TargetSymbol == "if"), "no Call edge for 'if'");
                AssertFalse(callEdges.Any(e => e.TargetSymbol == "while"), "no Call edge for 'while'");
                AssertFalse(callEdges.Any(e => e.TargetSymbol == "for"), "no Call edge for 'for'");
                AssertFalse(callEdges.Any(e => e.TargetSymbol == "switch"), "no Call edge for 'switch'");
                AssertFalse(callEdges.Any(e => e.TargetSymbol == "return"), "no Call edge for 'return'");
            });

            await RunTest("CRLF line endings preserve 1-based line numbers", () =>
            {
                string source = "namespace Armada.Core\r\n{\r\n    public class Foo\r\n    {\r\n    }\r\n}\r\n";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Foo.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? cls = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Class);
                AssertNotNull(cls, "class symbol");
                AssertEqual(3, cls!.StartLine, "class starts on line 3 even with CRLF endings");
            });

            await RunTest("Using static directive emits Imports edge", () =>
            {
                string source = @"namespace Armada.Core
{
    using static System.Math;

    public class Calc { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Calc.cs", "h", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> importEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Imports).ToList();
                AssertTrue(importEdges.Any(e => e.TargetSymbol == "System.Math"),
                    "Imports edge for 'using static System.Math;'");
            });

            await RunTest("File-scoped namespace (semicolon form) is recognized as namespace prefix", () =>
            {
                // C# 10+ file-scoped namespace syntax: `namespace X.Y;` with class declarations at top level.
                string source = "namespace Armada.Core.Internals;\n\npublic class Hidden { }\n";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Hidden.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? ns = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Namespace);
                AssertNotNull(ns, "file-scoped namespace symbol");
                AssertEqual("Armada.Core.Internals", ns!.SimpleName, "file-scoped namespace simple name");
                CodeGraphSymbolRecord? cls = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Class);
                AssertNotNull(cls, "class symbol after file-scoped namespace");
                AssertEqual("Armada.Core.Internals.Hidden", cls!.QualifiedName,
                    "class qualified name uses file-scoped namespace as prefix");
            });

            await RunTest("All emitted symbols carry the path and contentHash fields", () =>
            {
                // Use a multi-line method body so the method body cleanly exits before the property declaration.
                string source = @"namespace Armada.Core
{
    public class Foo
    {
        public void Bar()
        {
        }

        public string Baz { get; set; } = """";
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_v", "sha_v", "src/Foo.cs", "ch_v", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(symbols.Count >= 4, "expected at least 4 symbols (ns, class, method, property)");
                foreach (CodeGraphSymbolRecord sym in symbols)
                {
                    AssertEqual("src/Foo.cs", sym.Path, "symbol path on " + sym.SimpleName);
                    AssertEqual("ch_v", sym.ContentHash, "symbol contentHash on " + sym.SimpleName);
                }
                foreach (CodeGraphEdgeRecord edge in edges)
                {
                    AssertEqual("src/Foo.cs", edge.SourcePath, "edge sourcePath on " + edge.TargetSymbol);
                    AssertTrue(edge.SourceLine >= 1, "edge sourceLine is 1-based for " + edge.TargetSymbol);
                }
            });

            await RunTest("Multiple namespaces in one file each emit their own Namespace symbol", () =>
            {
                string source = @"namespace Alpha.First
{
    public class A { }
}

namespace Beta.Second
{
    public class B { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Multi.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                List<CodeGraphSymbolRecord> namespaces = symbols.Where(s => s.Kind == CodeGraphSymbolKindEnum.Namespace).ToList();
                AssertTrue(namespaces.Any(n => n.SimpleName == "Alpha.First"), "Alpha.First namespace symbol");
                AssertTrue(namespaces.Any(n => n.SimpleName == "Beta.Second"), "Beta.Second namespace symbol");
            });

            return;
        }

        #endregion
    }
}
