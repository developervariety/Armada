namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;

    /// <summary>
    /// Behavioural tests for live WebSocket delivery scope: which sessions receive an event about a
    /// record owned by another user or tenant, and that replayed events obey the same rule.
    /// </summary>
    public class WebSocketDeliveryScopeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "WebSocket Delivery Scope";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Delivery matrix covers anonymous, users, tenant administrators and global administrators", () =>
            {
                AuthContext anonymous = new AuthContext();
                AuthContext owner = User("ten_a", "usr_a1");
                AuthContext sameTenantUser = User("ten_a", "usr_a2");
                AuthContext tenantAdmin = AuthContext.Authenticated("ten_a", "usr_admin_a", false, true, "Bearer");
                AuthContext otherTenantAdmin = AuthContext.Authenticated("ten_b", "usr_admin_b", false, true, "Bearer");
                AuthContext otherTenantUser = User("ten_b", "usr_b1");
                AuthContext global = AuthContext.Authenticated("default", "default", true, true, "ApiKey");

                WebSocketDeliveryScope owned = WebSocketDeliveryScope.ForOwner("ten_a", "usr_a1");
                AssertFalse(owned.CanReceive(anonymous), "anonymous");
                AssertFalse(owned.CanReceive(null), "no session identity");
                AssertTrue(owned.CanReceive(owner), "owning user");
                AssertFalse(owned.CanReceive(sameTenantUser), "another user in the tenant");
                AssertTrue(owned.CanReceive(tenantAdmin), "tenant administrator");
                AssertFalse(owned.CanReceive(otherTenantAdmin), "another tenant's administrator");
                AssertFalse(owned.CanReceive(otherTenantUser), "another tenant's user");
                AssertTrue(owned.CanReceive(global), "global administrator");

                WebSocketDeliveryScope tenantOnly = WebSocketDeliveryScope.ForOwner("ten_a", null);
                AssertFalse(tenantOnly.CanReceive(owner), "a tenant record without a user is not visible to ordinary users");
                AssertTrue(tenantOnly.CanReceive(tenantAdmin), "the tenant's administrator");
                AssertFalse(tenantOnly.CanReceive(otherTenantAdmin), "another tenant's administrator");

                foreach (WebSocketDeliveryScope adminOnly in new[] { WebSocketDeliveryScope.AdminOnly, WebSocketDeliveryScope.ForOwner(null, "usr_a1"), WebSocketDeliveryScope.ForOwner("  ", null) })
                {
                    AssertTrue(adminOnly.IsAdminOnly, "an ownerless event is admin-only");
                    AssertFalse(adminOnly.CanReceive(owner), "ownerless event to a user");
                    AssertFalse(adminOnly.CanReceive(tenantAdmin), "ownerless event to a tenant administrator");
                    AssertTrue(adminOnly.CanReceive(global), "ownerless event to a global administrator");
                }
                return Task.CompletedTask;
            });

            await RunTest("Replay records keep their scope and default to admin-only", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(16, 1024 * 1024, "stream-scope");
                buffer.Append((stream, cursor) => "{\"cursor\":" + cursor + ",\"owner\":\"a1\"}", WebSocketDeliveryScope.ForOwner("ten_a", "usr_a1"));
                buffer.Append((stream, cursor) => "{\"cursor\":" + cursor + ",\"owner\":\"b1\"}", WebSocketDeliveryScope.ForOwner("ten_b", "usr_b1"));
                buffer.Append((stream, cursor) => "{\"cursor\":" + cursor + ",\"owner\":\"none\"}");

                WebSocketReplayReadResult replay = buffer.ReadAfter("stream-scope", 0);
                AssertEqual(3, replay.Records.Count, "every event is retained");
                AssertTrue(replay.Records[2].Scope.IsAdminOnly, "an event appended without a scope is admin-only");

                List<string> ownerSees = replay.Records.Where(record => record.Scope.CanReceive(User("ten_a", "usr_a1"))).Select(record => record.Frame).ToList();
                AssertEqual(1, ownerSees.Count, "the owner replays only its own event");
                AssertTrue(ownerSees[0].Contains("\"owner\":\"a1\""), "the owner's own event");

                AuthContext global = AuthContext.Authenticated("default", "default", true, true, "ApiKey");
                AssertEqual(3, replay.Records.Count(record => record.Scope.CanReceive(global)), "a global administrator replays every event");
                return Task.CompletedTask;
            });
        }

        private static AuthContext User(string tenantId, string userId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, false, "Bearer");
        }
    }
}
