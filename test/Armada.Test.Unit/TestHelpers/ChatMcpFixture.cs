namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Runtime factory that builds a real API-endpoint runtime driven by a scripted inference client.
    /// </summary>
    internal sealed class ScriptedApiRuntimeFactory : AgentRuntimeFactory
    {
        private readonly LoggingModule _Logging;
        private readonly ScriptedToolChatClient _Client;

        public ScriptedApiRuntimeFactory(LoggingModule logging, ScriptedToolChatClient client) : base(logging)
        {
            _Logging = logging;
            _Client = client;
        }

        public override IAgentRuntime Create(ModelEndpoint endpoint)
        {
            return new ApiAgentRuntime(endpoint, _Logging, 10, (ep, log) => _Client);
        }
    }

    /// <summary>
    /// Two tenants, one memory record in each, an API-endpoint captain owned by the first tenant's user, and an
    /// MCP server that authenticates like the admiral and applies the shared tool access policy. Ask chat and the
    /// captain tools preflight share this fixture, so both are exercised against one real MCP endpoint.
    /// </summary>
    internal sealed class ChatMcpFixture
    {
        public ArmadaSettings Settings { get; private set; } = new ArmadaSettings();
        public SessionTokenService SessionTokens { get; private set; } = new SessionTokenService();
        public string TenantAId { get; private set; } = String.Empty;
        public string UserAId { get; private set; } = String.Empty;
        public string OwnMemoryId { get; private set; } = String.Empty;
        public string ForeignMemoryId { get; private set; } = String.Empty;
        public string CaptainId { get; private set; } = String.Empty;
        public bool OperatorToolRan { get; private set; } = false;

        private DatabaseDriver? _Database;
        private LoggingModule? _Logging;
        private AuthenticationService? _Authentication;
        private readonly List<McpRequestCredentials> _Seen = new List<McpRequestCredentials>();
        private readonly object _Lock = new object();

        public static async Task<ChatMcpFixture> CreateAsync(TestDatabase testDb, LoggingModule logging)
        {
            ChatMcpFixture fixture = new ChatMcpFixture();
            fixture._Database = testDb.Driver;
            fixture._Logging = logging;
            fixture.Settings.McpPort = GetAvailablePort();
            fixture._Authentication = new AuthenticationService(testDb.Driver, fixture.SessionTokens, fixture.Settings, logging);

            TenantMetadata tenantA = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ChatMcpTenantA")).ConfigureAwait(false);
            TenantMetadata tenantB = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ChatMcpTenantB")).ConfigureAwait(false);
            UserMaster userA = await testDb.Driver.Users.CreateAsync(new UserMaster(tenantA.Id, "a@chat-mcp.test", "pass")).ConfigureAwait(false);
            UserMaster userB = await testDb.Driver.Users.CreateAsync(new UserMaster(tenantB.Id, "b@chat-mcp.test", "pass")).ConfigureAwait(false);
            fixture.TenantAId = tenantA.Id;
            fixture.UserAId = userA.Id;

            Memory own = new Memory { TenantId = tenantA.Id, UserId = userA.Id, Scope = MemoryScopeEnum.UserSpecific, Content = "OWN-TENANT-MEMORY" };
            Memory foreign = new Memory { TenantId = tenantB.Id, UserId = userB.Id, Scope = MemoryScopeEnum.TenantWide, Content = "OTHER-TENANT-MEMORY" };
            fixture.OwnMemoryId = (await testDb.Driver.Memories.CreateAsync(own).ConfigureAwait(false)).Id;
            fixture.ForeignMemoryId = (await testDb.Driver.Memories.CreateAsync(foreign).ConfigureAwait(false)).Id;

            ModelEndpoint endpoint = new ModelEndpoint
            {
                Name = "chat-mcp-endpoint",
                TenantId = tenantA.Id,
                UserId = userA.Id,
                Scope = ScopeEnum.TenantWide,
                Provider = ModelProviderEnum.OpenAICompatible,
                Kind = ModelEndpointKindEnum.Inference,
                BaseUrl = "http://127.0.0.1:1",
                Model = "fixture-model",
                Enabled = true
            };
            await testDb.Driver.ModelEndpoints.CreateAsync(endpoint).ConfigureAwait(false);
            Captain captain = new Captain("chat-api-mcp", AgentRuntimeEnum.ApiEndpoint)
            {
                TenantId = tenantA.Id,
                UserId = userA.Id,
                ModelEndpointId = endpoint.Id,
                Model = "fixture-model"
            };
            await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
            fixture.CaptainId = captain.Id;
            return fixture;
        }

        public ArmadaMcpHttpServer CreateServer()
        {
            ArmadaMcpHttpServer server = new ArmadaMcpHttpServer("127.0.0.1", Settings.McpPort);
            server.Authenticator = async (credentials, token) =>
            {
                lock (_Lock) _Seen.Add(credentials);
                return await _Authentication!.AuthenticateAsync(credentials.Authorization, credentials.SessionToken, credentials.ApiKey, token).ConfigureAwait(false);
            };
            server.ToolAuthorizer = McpToolAccessPolicy.IsAllowed;
            McpMemoryTools.Register(server.RegisterTool, _Database!, _Logging);
            server.RegisterTool("armada_stop_server", "Operator control", new { type = "object" }, args =>
            {
                OperatorToolRan = true;
                return Task.FromResult((object)new { Status = "stopped" });
            });
            return server;
        }

        public List<McpRequestCredentials> SeenCredentials()
        {
            lock (_Lock) return new List<McpRequestCredentials>(_Seen);
        }

        private static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
