namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for dependency-free polyglot graph extraction.
    /// </summary>
    public class PolyglotSymbolExtractorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Polyglot Symbol Extractor";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("TypeScript extracts React components and Express endpoints", () =>
            {
                string source = "import React, { useEffect } from 'react';\n" +
                    "import express from 'express';\n" +
                    "const router = express.Router();\n" +
                    "export function UserCard() {\n" +
                    "  useEffect(() => { loadUser(); }, []);\n" +
                    "  return <div />;\n" +
                    "}\n" +
                    "router.get('/users/:id', handleUser);\n" +
                    "function handleUser(req, res) { loadUser(); }\n" +
                    "function loadUser() { return 1; }\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/UserCard.tsx", "hash", "typescript", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(symbols.Any(s => s.Kind == CodeGraphSymbolKindEnum.Component && s.SimpleName == "UserCard" && s.Framework == "react"),
                    "React component should be tagged");
                AssertTrue(symbols.Any(s => s.Kind == CodeGraphSymbolKindEnum.Endpoint && s.SimpleName == "GET /users/:id" && s.Framework == "express"),
                    "Express endpoint should be extracted");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol.EndsWith(".loadUser")),
                    "Function body should emit call edge");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.SourceSymbol.EndsWith("GET /users/:id") && e.TargetSymbol.EndsWith(".handleUser")),
                    "Express endpoint should call its handler");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.References && e.TargetSymbol == "react.hooks"),
                    "React hooks should emit framework reference edge");
            });

            await RunTest("TypeScript resolves imported call aliases", () =>
            {
                string source = "import { loadUser as fetchUser } from './users';\n" +
                    "export function run() {\n" +
                    "  return fetchUser();\n" +
                    "}\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/run.ts", "hash", "typescript", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol == "loadUser"),
                    "Aliased TypeScript import calls should resolve to the imported symbol name");
            });

            await RunTest("Python extracts FastAPI endpoints and call edges", () =>
            {
                string source = "from fastapi import APIRouter\n" +
                    "router = APIRouter()\n" +
                    "class UserService:\n" +
                    "    def load_user(self):\n" +
                    "        return build_user()\n" +
                    "@router.get('/users/{id}')\n" +
                    "async def get_user(id: str):\n" +
                    "    return UserService().load_user()\n" +
                    "def build_user():\n" +
                    "    return {}\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "app/users.py", "hash", "python", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(symbols.Any(s => s.Kind == CodeGraphSymbolKindEnum.Class && s.SimpleName == "UserService"),
                    "Python class should be extracted");
                AssertTrue(symbols.Any(s => s.Kind == CodeGraphSymbolKindEnum.Endpoint && s.SimpleName == "GET /users/{id}"),
                    "FastAPI endpoint should be extracted");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.SourceSymbol.EndsWith("GET /users/{id}") && e.TargetSymbol.EndsWith(".get_user")),
                    "FastAPI endpoint should call its decorated function");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol.EndsWith(".UserService")),
                    "Python function body should emit call edge");
            });

            await RunTest("Python resolves imported call aliases", () =>
            {
                string source = "from services.users import load_user as fetch_user\n" +
                    "def run():\n" +
                    "    return fetch_user()\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "app/run.py", "hash", "python", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol == "load_user"),
                    "Aliased Python import calls should resolve to the imported symbol name");
            });

            await RunTest("Java extracts Spring endpoints", () =>
            {
                string source = "package com.example;\n" +
                    "import org.springframework.web.bind.annotation.GetMapping;\n" +
                    "public class UserController {\n" +
                    "  @GetMapping(\"/users/{id}\")\n" +
                    "  public UserDto getUser(String id) { return loadUser(id); }\n" +
                    "  private UserDto loadUser(String id) { return new UserDto(); }\n" +
                    "}\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/UserController.java", "hash", "java", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(symbols.Any(s => s.Kind == CodeGraphSymbolKindEnum.Endpoint && s.SimpleName == "GET /users/{id}" && s.Framework == "spring"),
                    "Spring endpoint should be extracted");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.SourceSymbol.EndsWith("GET /users/{id}") && e.TargetSymbol.EndsWith(".getUser")),
                    "Spring endpoint should call its annotated method");
                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls && e.TargetSymbol.EndsWith(".loadUser")),
                    "Java method body should emit call edge");
            });

            await RunTest("Local call targets resolve to qualified graph symbols", () =>
            {
                string source = "export function entry() {\n" +
                    "  return helper();\n" +
                    "}\n" +
                    "function helper() { return 1; }\n";

                PolyglotSymbolExtractor extractor = new PolyglotSymbolExtractor();
                extractor.Extract("vsl_test", "abc", "src/local.ts", "hash", "typescript", source,
                    out List<CodeGraphSymbolRecord> symbols, out List<CodeGraphEdgeRecord> edges);

                AssertTrue(edges.Any(e => e.Kind == CodeGraphEdgeKindEnum.Calls &&
                    e.SourceSymbol == "src.local.entry" &&
                    e.TargetSymbol == "src.local.helper"),
                    "Known local calls should resolve to qualified target symbols for graph traversal");
            });
        }
    }
}
