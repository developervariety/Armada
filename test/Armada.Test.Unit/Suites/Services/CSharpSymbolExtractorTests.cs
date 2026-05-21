namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the regex-based C# symbol and edge extractor.
    /// </summary>
    public class CSharpSymbolExtractorTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "CSharp Symbol Extractor";

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Namespace is extracted as a symbol", () =>
            {
                string source = @"namespace Armada.Core.Services
{
    public class MyService { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/MyService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                CodeGraphSymbolRecord? ns = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Namespace);
                AssertNotNull(ns, "namespace symbol");
                AssertEqual("Armada.Core.Services", ns!.SimpleName, "namespace simple name");
                AssertEqual("Armada.Core.Services", ns.QualifiedName, "namespace qualified name");
            });

            await RunTest("Class declaration produces Class symbol with qualified name", () =>
            {
                string source = @"namespace Armada.Core.Services
{
    public class CodeIndexService
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/CodeIndexService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? cls = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Class);
                AssertNotNull(cls, "class symbol");
                AssertEqual("CodeIndexService", cls!.SimpleName, "class simple name");
                AssertEqual("Armada.Core.Services.CodeIndexService", cls.QualifiedName, "class qualified name");
            });

            await RunTest("Interface declaration produces Interface symbol", () =>
            {
                string source = @"namespace Armada.Core
{
    public interface IMyService { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/IMyService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? iface = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Interface);
                AssertNotNull(iface, "interface symbol");
                AssertEqual("IMyService", iface!.SimpleName, "interface simple name");
            });

            await RunTest("Record declaration produces Record symbol", () =>
            {
                string source = @"namespace Armada.Core
{
    public record MyRecord(string Id, string Name);
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/MyRecord.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? rec = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Record);
                AssertNotNull(rec, "record symbol");
                AssertEqual("MyRecord", rec!.SimpleName, "record simple name");
            });

            await RunTest("Enum declaration produces Enum symbol", () =>
            {
                string source = @"namespace Armada.Core
{
    public enum StatusEnum { Active, Inactive }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/StatusEnum.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? enm = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Enum);
                AssertNotNull(enm, "enum symbol");
                AssertEqual("StatusEnum", enm!.SimpleName, "enum simple name");
            });

            await RunTest("Method declarations inside a class produce Method symbols", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void DoWork(string input)
        {
        }

        private int ComputeHash(string s)
        {
            return s.GetHashCode();
        }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/Worker.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                List<CodeGraphSymbolRecord> methods = symbols.Where(s => s.Kind == CodeGraphSymbolKindEnum.Method).ToList();
                AssertTrue(methods.Any(m => m.SimpleName == "DoWork"), "DoWork method found");
                AssertTrue(methods.Any(m => m.SimpleName == "ComputeHash"), "ComputeHash method found");
                AssertTrue(methods.Any(m => m.QualifiedName == "Armada.Core.Worker.DoWork"), "DoWork qualified name");
            });

            await RunTest("Constructor produces Constructor symbol with type simple name", () =>
            {
                string source = @"namespace Armada.Core
{
    public class MyService
    {
        public MyService(string id)
        {
        }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/MyService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? ctor = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Constructor);
                AssertNotNull(ctor, "constructor symbol");
                AssertEqual("MyService", ctor!.SimpleName, "constructor simple name");
            });

            await RunTest("Contains edges emitted from type to methods", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void Run()
        {
        }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/Worker.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> containsEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Contains).ToList();
                AssertTrue(containsEdges.Count > 0, "contains edges present");
                AssertTrue(containsEdges.Any(e => e.SourceSymbol == "Armada.Core.Worker" && e.TargetSymbol == "Armada.Core.Worker.Run"),
                    "Worker contains Run");
            });

            await RunTest("Inherits edge emitted when class extends another class", () =>
            {
                string source = @"namespace Armada.Core
{
    public class DerivedService : BaseService
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/DerivedService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> inheritsEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Inherits).ToList();
                AssertTrue(inheritsEdges.Any(e => e.TargetSymbol == "BaseService"), "inherits BaseService edge present");
            });

            await RunTest("Implements edge emitted when class implements interface", () =>
            {
                string source = @"namespace Armada.Core
{
    public class WorkerImpl : IWorker
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/WorkerImpl.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> implEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Implements).ToList();
                AssertTrue(implEdges.Any(e => e.TargetSymbol == "IWorker"), "implements IWorker edge present");
            });

            await RunTest("Imports edges emitted for using directives", () =>
            {
                string source = @"namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    public class MyService { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/MyService.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> importEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Imports).ToList();
                AssertTrue(importEdges.Any(e => e.TargetSymbol == "System"), "System import edge");
                AssertTrue(importEdges.Any(e => e.TargetSymbol == "System.Collections.Generic"), "Generic import edge");
            });

            await RunTest("Call edges emitted for method invocations inside a method body", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Worker
    {
        public void Run()
        {
            DoWork();
            Helper();
        }

        private void DoWork() { }
        private void Helper() { }
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc123", "src/Worker.cs", "hash1", source,
                    out List<CodeGraphSymbolRecord> _, out List<CodeGraphEdgeRecord> edges);

                List<CodeGraphEdgeRecord> callEdges = edges.Where(e => e.Kind == CodeGraphEdgeKindEnum.Calls).ToList();
                AssertTrue(callEdges.Any(e => e.TargetSymbol == "DoWork"), "DoWork call edge");
                AssertTrue(callEdges.Any(e => e.TargetSymbol == "Helper"), "Helper call edge");
            });

            await RunTest("Symbol records carry vessel id and commit sha", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Foo { }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_abc", "sha999", "src/Foo.cs", "contenthash", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                foreach (CodeGraphSymbolRecord sym in symbols)
                {
                    AssertEqual("vsl_abc", sym.VesselId, "symbol vessel id");
                    AssertEqual("sha999", sym.CommitSha, "symbol commit sha");
                    AssertEqual("contenthash", sym.ContentHash, "symbol content hash");
                    AssertEqual("src/Foo.cs", sym.Path, "symbol path");
                }
                foreach (CodeGraphEdgeRecord edge in edges)
                {
                    AssertEqual("vsl_abc", edge.VesselId, "edge vessel id");
                    AssertEqual("sha999", edge.CommitSha, "edge commit sha");
                }
            });

            await RunTest("Empty source produces no symbols or edges", () =>
            {
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Empty.cs", "h", "",
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertEqual(0, symbols.Count, "no symbols from empty source");
                AssertEqual(0, edges.Count, "no edges from empty source");
            });

            await RunTest("Generic class name is extracted without angle-bracket noise", () =>
            {
                string source = @"namespace Armada.Core
{
    public class Repository<T> where T : class
    {
    }
}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Repository.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? cls = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Class);
                AssertNotNull(cls, "generic class symbol");
                AssertEqual("Repository", cls!.SimpleName, "generic class simple name without angle brackets");
            });

            await RunTest("StartLine is set to the 1-based line where symbol appears", () =>
            {
                string source = "namespace Armada.Core\n{\n    public class Foo\n    {\n    }\n}";
                CSharpSymbolExtractor extractor = new CSharpSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/Foo.cs", "h", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> _);

                CodeGraphSymbolRecord? cls = symbols.FirstOrDefault(s => s.Kind == CodeGraphSymbolKindEnum.Class);
                AssertNotNull(cls, "class symbol");
                AssertEqual(3, cls!.StartLine, "class starts on line 3");
            });

            return;
        }

        #endregion
    }
}
