#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// One scoped-read matrix per method set: rows spread over two tenants, three users and three scopes are
    /// read back through every tenant, user, scope and creation-time filter the method set offers, page by page,
    /// and each page must hold exactly the matching rows in the documented order. Reads and deletes outside the
    /// owning scope must see nothing.
    /// </summary>
    internal sealed class ScopedReadMatrixDatabaseTests
    {
        private const int PageSize = 2;

        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal ScopedReadMatrixDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        /// <summary>
        /// Where one matrix row lives. Row times are one minute apart, so every documented order is total.
        /// </summary>
        private sealed class Slot
        {
            internal int Index { get; set; }

            internal string TenantId { get; set; } = String.Empty;

            internal string UserId { get; set; } = String.Empty;

            internal string ScopeId { get; set; } = String.Empty;

            internal DateTime Time { get; set; }
        }

        /// <summary>
        /// The owners every matrix shares: tenant A holds users A1 and A2 and scopes X and Y; tenant B holds user B1 and scope Z.
        /// </summary>
        private sealed class Owners
        {
            internal string TenantA { get; set; } = String.Empty;

            internal string TenantB { get; set; } = String.Empty;

            internal string UserA1 { get; set; } = String.Empty;

            internal string UserA2 { get; set; } = String.Empty;

            internal string UserB1 { get; set; } = String.Empty;

            internal string ScopeX { get; set; } = String.Empty;

            internal string ScopeY { get; set; } = String.Empty;

            internal string ScopeZ { get; set; } = String.Empty;

            internal string FleetA { get; set; } = String.Empty;

            internal string FleetB { get; set; } = String.Empty;

            internal DateTime Start { get; set; }

            internal List<Slot> Slots { get; } = new List<Slot>();

            internal DateTime WindowFrom => Start.AddMinutes(1);

            internal DateTime WindowTo => Start.AddMinutes(4);

            internal string FleetOf(string scopeId) => scopeId == ScopeZ ? FleetB : FleetA;
        }

        /// <summary>
        /// One scope a matrix reads through: which slots it must return, and the page it reads.
        /// </summary>
        private sealed class Scope<T>
        {
            internal Scope(string label, Func<Slot, bool> match, Func<int, int, Task<EnumerationResult<T>>> page)
            {
                Label = label;
                Match = match;
                Page = page;
            }

            internal string Label { get; }

            internal Func<Slot, bool> Match { get; }

            internal Func<int, int, Task<EnumerationResult<T>>> Page { get; }
        }

        /// <summary>
        /// One tenant, user and scope filter a query-based method set offers, with the slots it must return.
        /// </summary>
        private sealed class StandardScope
        {
            internal StandardScope(string label, Func<Slot, bool> match, string? tenantId, string? userId, string? scopeId, bool window)
            {
                Label = label;
                Match = match;
                TenantId = tenantId;
                UserId = userId;
                ScopeId = scopeId;
                Window = window;
            }

            internal string Label { get; }

            internal Func<Slot, bool> Match { get; }

            internal string? TenantId { get; }

            internal string? UserId { get; }

            internal string? ScopeId { get; }

            internal bool Window { get; }
        }

        /// <summary>
        /// A created row and the slot it was created in.
        /// </summary>
        private sealed class Placed<T>
        {
            internal Placed(Slot slot, T row)
            {
                Slot = slot;
                Row = row;
            }

            internal Slot Slot { get; }

            internal T Row { get; }
        }

        private async Task<Owners> SeedOwnersAsync(DatabaseFixture fixture, DateTime start, CancellationToken token)
        {
            TenantMetadata tenantA = await fixture.CreateTenantAsync("matrix-a", token: token).ConfigureAwait(false);
            TenantMetadata tenantB = await fixture.CreateTenantAsync("matrix-b", token: token).ConfigureAwait(false);
            UserMaster userA1 = await fixture.CreateUserAsync(tenantA.Id, "matrix-a1", token: token).ConfigureAwait(false);
            UserMaster userA2 = await fixture.CreateUserAsync(tenantA.Id, "matrix-a2", token: token).ConfigureAwait(false);
            UserMaster userB1 = await fixture.CreateUserAsync(tenantB.Id, "matrix-b1", token: token).ConfigureAwait(false);
            Fleet fleetA = await fixture.CreateFleetAsync(tenantA.Id, userA1.Id, "matrix-fleet-a", token).ConfigureAwait(false);
            Fleet fleetB = await fixture.CreateFleetAsync(tenantB.Id, userB1.Id, "matrix-fleet-b", token).ConfigureAwait(false);
            Vessel vesselX = await fixture.CreateVesselAsync(tenantA.Id, userA1.Id, fleetA.Id, "matrix-x", token).ConfigureAwait(false);
            Vessel vesselY = await fixture.CreateVesselAsync(tenantA.Id, userA1.Id, fleetA.Id, "matrix-y", token).ConfigureAwait(false);
            Vessel vesselZ = await fixture.CreateVesselAsync(tenantB.Id, userB1.Id, fleetB.Id, "matrix-z", token).ConfigureAwait(false);

            Owners owners = new Owners
            {
                TenantA = tenantA.Id,
                TenantB = tenantB.Id,
                UserA1 = userA1.Id,
                UserA2 = userA2.Id,
                UserB1 = userB1.Id,
                ScopeX = vesselX.Id,
                ScopeY = vesselY.Id,
                ScopeZ = vesselZ.Id,
                FleetA = fleetA.Id,
                FleetB = fleetB.Id,
                Start = start
            };

            string[][] layout =
            {
                new[] { owners.TenantA, owners.UserA1, owners.ScopeX },
                new[] { owners.TenantA, owners.UserA1, owners.ScopeX },
                new[] { owners.TenantA, owners.UserA1, owners.ScopeY },
                new[] { owners.TenantA, owners.UserA2, owners.ScopeX },
                new[] { owners.TenantA, owners.UserA1, owners.ScopeY },
                new[] { owners.TenantB, owners.UserB1, owners.ScopeZ },
                new[] { owners.TenantA, owners.UserA2, owners.ScopeY }
            };
            for (int i = 0; i < layout.Length; i++)
            {
                owners.Slots.Add(new Slot
                {
                    Index = i,
                    TenantId = layout[i][0],
                    UserId = layout[i][1],
                    ScopeId = layout[i][2],
                    Time = start.AddMinutes(i)
                });
            }

            return owners;
        }

        private static DateTime MatrixStart()
        {
            DateTime now = DateTime.UtcNow.AddHours(-2);
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
        }

        /// <summary>
        /// The tenant, user and scope filters every query-based method set shares, plus its creation-time window.
        /// </summary>
        private static IEnumerable<StandardScope> StandardScopes(Owners o, bool hasScope, bool hasWindow)
        {
            yield return new StandardScope("tenant", s => s.TenantId == o.TenantA, o.TenantA, null, null, false);
            yield return new StandardScope("tenant and user", s => s.TenantId == o.TenantA && s.UserId == o.UserA1, o.TenantA, o.UserA1, null, false);
            yield return new StandardScope("other tenant", s => s.TenantId == o.TenantB, o.TenantB, null, null, false);
            yield return new StandardScope("tenant with a foreign user", s => false, o.TenantA, o.UserB1, null, false);
            if (hasScope)
            {
                yield return new StandardScope("tenant, user and scope", s => s.TenantId == o.TenantA && s.UserId == o.UserA1 && s.ScopeId == o.ScopeX, o.TenantA, o.UserA1, o.ScopeX, false);
                yield return new StandardScope("tenant and scope", s => s.TenantId == o.TenantA && s.ScopeId == o.ScopeY, o.TenantA, null, o.ScopeY, false);
                yield return new StandardScope("tenant with a foreign scope", s => false, o.TenantA, null, o.ScopeZ, false);
            }
            if (hasWindow)
            {
                yield return new StandardScope("tenant and creation window", s => s.TenantId == o.TenantA && s.Time >= o.WindowFrom && s.Time <= o.WindowTo, o.TenantA, null, null, true);
            }
        }

        /// <summary>
        /// Every page of every scope holds exactly the matching rows in the expected order, and the totals agree.
        /// </summary>
        private static async Task VerifyScopesAsync<T>(
            string entity,
            Owners owners,
            IReadOnlyDictionary<int, T> rows,
            Func<T, string> id,
            Func<IEnumerable<Placed<T>>, IEnumerable<Placed<T>>> order,
            IEnumerable<Scope<T>> scopes)
        {
            foreach (Scope<T> scope in scopes)
            {
                string label = entity + " [" + scope.Label + "]";
                List<string> expected = order(owners.Slots.Where(scope.Match).Select(s => new Placed<T>(s, rows[s.Index]))).Select(r => id(r.Row)).ToList();
                int expectedPages = (int)Math.Ceiling((double)expected.Count / PageSize);
                List<string> actual = new List<string>();
                for (int pageNumber = 1; pageNumber <= expectedPages + 1; pageNumber++)
                {
                    EnumerationResult<T> page = await scope.Page(pageNumber, PageSize).ConfigureAwait(false);
                    DatabaseAssert.Equal((long)expected.Count, page.TotalRecords, label + " total records on page " + pageNumber);
                    DatabaseAssert.Equal(expectedPages, page.TotalPages, label + " total pages on page " + pageNumber);
                    DatabaseAssert.True(page.Objects.Count <= PageSize, label + " page " + pageNumber + " holds at most one page of rows");
                    actual.AddRange(page.Objects.Select(id));
                }
                Ordered(label, expected, actual);
            }
        }

        private static void Ordered(string label, IEnumerable<string> expected, IEnumerable<string> actual)
        {
            string expectedText = String.Join(", ", expected);
            string actualText = String.Join(", ", actual);
            if (!String.Equals(expectedText, actualText, StringComparison.Ordinal))
                throw new Exception(label + " rows mismatch: expected [" + expectedText + "] got [" + actualText + "]");
        }

        private static EnumerationResult<T> AsPage<T>(List<T> all, int pageNumber, int pageSize)
        {
            return new EnumerationResult<T>
            {
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalRecords = all.Count,
                TotalPages = (int)Math.Ceiling((double)all.Count / pageSize),
                Objects = all.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList()
            };
        }

        internal async Task VerifyCheckRunsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, CheckRun> rows = new Dictionary<int, CheckRun>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        Label = "matrix check " + slot.Index,
                        Type = CheckRunTypeEnum.Build,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Passed,
                        CreatedUtc = slot.Time
                    }, token).ConfigureAwait(false);
                }

                List<Scope<CheckRun>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<CheckRun>(s.Label, s.Match, (page, size) => _Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                await VerifyScopesAsync("CheckRun", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);

                CheckRun owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.CheckRuns.ReadAsync(owned.Id, new CheckRunQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "CheckRun reads in its own scope");
                DatabaseAssert.True(await _Driver.CheckRuns.ReadAsync(owned.Id, new CheckRunQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "CheckRun is hidden from another tenant");
                DatabaseAssert.True(await _Driver.CheckRuns.ReadAsync(owned.Id, new CheckRunQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "CheckRun is hidden from another user");
                await _Driver.CheckRuns.DeleteAsync(owned.Id, new CheckRunQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.CheckRuns.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the CheckRun");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (CheckRun row in rows.Values) await _Driver.CheckRuns.DeleteAsync(row.Id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDeploymentsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, Deployment> rows = new Dictionary<int, Deployment>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.Deployments.CreateAsync(new Deployment
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        EnvironmentName = "staging",
                        Title = "matrix deployment " + slot.Index,
                        Status = DeploymentStatusEnum.Succeeded,
                        VerificationStatus = DeploymentVerificationStatusEnum.Passed,
                        CreatedUtc = slot.Time,
                        StartedUtc = slot.Time,
                        CompletedUtc = slot.Time.AddSeconds(30)
                    }, token).ConfigureAwait(false);
                }

                List<Scope<Deployment>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<Deployment>(s.Label, s.Match, (page, size) => _Driver.Deployments.EnumerateAsync(new DeploymentQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.Add(new Scope<Deployment>("tenant, all rows", s => s.TenantId == o.TenantA, async (page, size) =>
                    AsPage(await _Driver.Deployments.EnumerateAllAsync(new DeploymentQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), page, size)));
                await VerifyScopesAsync("Deployment", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Row.CompletedUtc), scopes).ConfigureAwait(false);

                Deployment owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.Deployments.ReadAsync(owned.Id, new DeploymentQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "Deployment reads in its own scope");
                DatabaseAssert.True(await _Driver.Deployments.ReadAsync(owned.Id, new DeploymentQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "Deployment is hidden from another tenant");
                DatabaseAssert.True(await _Driver.Deployments.ReadAsync(owned.Id, new DeploymentQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "Deployment is hidden from another user");
                await _Driver.Deployments.DeleteAsync(owned.Id, new DeploymentQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.Deployments.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the Deployment");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (Deployment row in rows.Values) await _Driver.Deployments.DeleteAsync(row.Id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDeploymentEnvironmentsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, DeploymentEnvironment> rows = new Dictionary<int, DeploymentEnvironment>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.Environments.CreateAsync(new DeploymentEnvironment
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        Name = "matrix-env-" + slot.Index + "-" + suffix,
                        Kind = EnvironmentKindEnum.Staging,
                        IsDefault = false,
                        Active = slot.Index != 1,
                        CreatedUtc = slot.Time
                    }, token).ConfigureAwait(false);
                }

                List<Scope<DeploymentEnvironment>> scopes = StandardScopes(o, hasScope: true, hasWindow: false)
                    .Select(s => new Scope<DeploymentEnvironment>(s.Label, s.Match, (page, size) => _Driver.Environments.EnumerateAsync(new DeploymentEnvironmentQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.Add(new Scope<DeploymentEnvironment>("tenant and active", s => s.TenantId == o.TenantA && s.Index != 1, (page, size) =>
                    _Driver.Environments.EnumerateAsync(new DeploymentEnvironmentQuery { TenantId = o.TenantA, Active = true, PageNumber = page, PageSize = size }, token)));
                scopes.Add(new Scope<DeploymentEnvironment>("tenant, all rows", s => s.TenantId == o.TenantA, async (page, size) =>
                    AsPage(await _Driver.Environments.EnumerateAllAsync(new DeploymentEnvironmentQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), page, size)));
                await VerifyScopesAsync("DeploymentEnvironment", o, rows, r => r.Id,
                    r => r.OrderByDescending(x => x.Row.Active).ThenBy(x => x.Slot.Index), scopes).ConfigureAwait(false);

                DeploymentEnvironment owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.Environments.ReadAsync(owned.Id, new DeploymentEnvironmentQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "DeploymentEnvironment reads in its own scope");
                DatabaseAssert.True(await _Driver.Environments.ReadAsync(owned.Id, new DeploymentEnvironmentQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "DeploymentEnvironment is hidden from another tenant");
                DatabaseAssert.True(await _Driver.Environments.ReadAsync(owned.Id, new DeploymentEnvironmentQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "DeploymentEnvironment is hidden from another user");
                await _Driver.Environments.DeleteAsync(owned.Id, new DeploymentEnvironmentQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.Environments.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the DeploymentEnvironment");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (DeploymentEnvironment row in rows.Values) await _Driver.Environments.DeleteAsync(row.Id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyReleasesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, Release> rows = new Dictionary<int, Release>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.Releases.CreateAsync(new Release
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        Title = "matrix release " + slot.Index,
                        Status = ReleaseStatusEnum.Candidate,
                        CreatedUtc = slot.Time,
                        PublishedUtc = slot.Time.AddSeconds(30)
                    }, token).ConfigureAwait(false);
                }

                List<Scope<Release>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<Release>(s.Label, s.Match, (page, size) => _Driver.Releases.EnumerateAsync(new ReleaseQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.Add(new Scope<Release>("tenant, all rows", s => s.TenantId == o.TenantA, async (page, size) =>
                    AsPage(await _Driver.Releases.EnumerateAllAsync(new ReleaseQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), page, size)));
                await VerifyScopesAsync("Release", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Row.PublishedUtc), scopes).ConfigureAwait(false);

                Release owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.Releases.ReadAsync(owned.Id, new ReleaseQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "Release reads in its own scope");
                DatabaseAssert.True(await _Driver.Releases.ReadAsync(owned.Id, new ReleaseQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "Release is hidden from another tenant");
                DatabaseAssert.True(await _Driver.Releases.ReadAsync(owned.Id, new ReleaseQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "Release is hidden from another user");
                await _Driver.Releases.DeleteAsync(owned.Id, new ReleaseQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.Releases.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the Release");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (Release row in rows.Values) await _Driver.Releases.DeleteAsync(row.Id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyWorkflowProfilesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, WorkflowProfile> rows = new Dictionary<int, WorkflowProfile>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        Name = "matrix-profile-" + slot.Index + "-" + suffix,
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        FleetId = o.FleetOf(slot.ScopeId),
                        VesselId = slot.ScopeId,
                        IsDefault = false,
                        Active = true,
                        CreatedUtc = slot.Time
                    }, token).ConfigureAwait(false);
                    // The documented order leads with the stamped update time, so rows are stamped at least a tick apart on every store.
                    await Task.Delay(20, token).ConfigureAwait(false);
                }

                List<Scope<WorkflowProfile>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<WorkflowProfile>(s.Label, s.Match, (page, size) => _Driver.WorkflowProfiles.EnumerateAsync(new WorkflowProfileQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.Add(new Scope<WorkflowProfile>("tenant and fleet", s => s.TenantId == o.TenantA, (page, size) =>
                    _Driver.WorkflowProfiles.EnumerateAsync(new WorkflowProfileQuery { TenantId = o.TenantA, FleetId = o.FleetA, PageNumber = page, PageSize = size }, token)));
                scopes.Add(new Scope<WorkflowProfile>("tenant, all rows", s => s.TenantId == o.TenantA, async (page, size) =>
                    AsPage(await _Driver.WorkflowProfiles.EnumerateAllAsync(new WorkflowProfileQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), page, size)));
                await VerifyScopesAsync("WorkflowProfile", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Row.LastUpdateUtc), scopes).ConfigureAwait(false);

                WorkflowProfile owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.WorkflowProfiles.ReadAsync(owned.Id, new WorkflowProfileQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "WorkflowProfile reads in its own scope");
                DatabaseAssert.True(await _Driver.WorkflowProfiles.ReadAsync(owned.Id, new WorkflowProfileQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "WorkflowProfile is hidden from another tenant");
                DatabaseAssert.True(await _Driver.WorkflowProfiles.ReadAsync(owned.Id, new WorkflowProfileQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "WorkflowProfile is hidden from another user");
                await _Driver.WorkflowProfiles.DeleteAsync(owned.Id, new WorkflowProfileQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.WorkflowProfiles.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the WorkflowProfile");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (WorkflowProfile row in rows.Values) await _Driver.WorkflowProfiles.DeleteAsync(row.Id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyMemoriesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, Memory> rows = new Dictionary<int, Memory>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                foreach (Slot slot in o.Slots)
                {
                    Memory memory = new Memory
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        Key = "matrix/" + slot.Index,
                        Content = "matrix memory " + slot.Index,
                        CreatedUtc = slot.Time,
                        LastUpdateUtc = slot.Time
                    };
                    rows[slot.Index] = await _Driver.Memories.CreateAsync(memory, token).ConfigureAwait(false);
                }

                List<Scope<Memory>> scopes = new List<Scope<Memory>>
                {
                    new Scope<Memory>("tenant", s => s.TenantId == o.TenantA, async (page, size) =>
                        AsPage(await _Driver.Memories.EnumerateAsync(o.TenantA, token).ConfigureAwait(false), page, size)),
                    new Scope<Memory>("other tenant", s => s.TenantId == o.TenantB, async (page, size) =>
                        AsPage(await _Driver.Memories.EnumerateAsync(o.TenantB, token).ConfigureAwait(false), page, size))
                };
                await VerifyScopesAsync("Memory", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);

                Memory owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.Memories.ReadAsync(o.TenantA, owned.Id, token).ConfigureAwait(false), "Memory reads in its own tenant");
                DatabaseAssert.NotNull(await _Driver.Memories.ReadByKeyAsync(o.TenantA, "matrix/0", token).ConfigureAwait(false), "Memory key resolves in its own tenant");
                DatabaseAssert.True(await _Driver.Memories.ReadAsync(o.TenantB, owned.Id, token).ConfigureAwait(false) == null, "Memory is hidden from another tenant");
                DatabaseAssert.True(await _Driver.Memories.ReadByKeyAsync(o.TenantB, "matrix/0", token).ConfigureAwait(false) == null, "Memory key does not resolve in another tenant");
                DatabaseAssert.True(!await _Driver.Memories.DeleteAsync(o.TenantB, owned.Id, token).ConfigureAwait(false), "Another tenant cannot delete the Memory");
                DatabaseAssert.NotNull(await _Driver.Memories.ReadAsync(owned.Id, token).ConfigureAwait(false), "Memory survives a foreign delete");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (Memory row in rows.Values) await _Driver.Memories.DeleteAsync(row.TenantId!, row.Id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyModelEndpointsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, ModelEndpoint> rows = new Dictionary<int, ModelEndpoint>();
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        Name = "matrix endpoint " + slot.Index,
                        Scope = ScopeEnum.UserSpecific,
                        BaseUrl = "http://localhost:9" + slot.Index + "00/v1",
                        CreatedUtc = slot.Time,
                        LastUpdateUtc = slot.Time
                    }, token).ConfigureAwait(false);
                }

                List<Scope<ModelEndpoint>> scopes = new List<Scope<ModelEndpoint>>
                {
                    new Scope<ModelEndpoint>("tenant", s => s.TenantId == o.TenantA, async (page, size) =>
                        AsPage(await _Driver.ModelEndpoints.EnumerateAsync(o.TenantA, token).ConfigureAwait(false), page, size)),
                    new Scope<ModelEndpoint>("tenant and user", s => s.TenantId == o.TenantA && s.UserId == o.UserA1, async (page, size) =>
                        AsPage(await _Driver.ModelEndpoints.EnumerateAsync(o.TenantA, o.UserA1, token).ConfigureAwait(false), page, size)),
                    new Scope<ModelEndpoint>("other tenant", s => s.TenantId == o.TenantB, async (page, size) =>
                        AsPage(await _Driver.ModelEndpoints.EnumerateAsync(o.TenantB, token).ConfigureAwait(false), page, size)),
                    new Scope<ModelEndpoint>("tenant with a foreign user", s => false, async (page, size) =>
                        AsPage(await _Driver.ModelEndpoints.EnumerateAsync(o.TenantA, o.UserB1, token).ConfigureAwait(false), page, size))
                };
                await VerifyScopesAsync("ModelEndpoint", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);

                ModelEndpoint owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(o.TenantA, o.UserA1, owned.Id, token).ConfigureAwait(false), "ModelEndpoint reads in its own scope");
                DatabaseAssert.True(await _Driver.ModelEndpoints.ReadAsync(o.TenantB, owned.Id, token).ConfigureAwait(false) == null, "ModelEndpoint is hidden from another tenant");
                DatabaseAssert.True(await _Driver.ModelEndpoints.ReadAsync(o.TenantA, o.UserA2, owned.Id, token).ConfigureAwait(false) == null, "ModelEndpoint is hidden from another user");
                await _Driver.ModelEndpoints.DeleteAsync(o.TenantB, owned.Id, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(owned.Id, token).ConfigureAwait(false), "Another tenant cannot delete the ModelEndpoint");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (ModelEndpoint row in rows.Values) await _Driver.ModelEndpoints.DeleteAsync(row.Id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyPromptTemplatesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, PromptTemplate> rows = new Dictionary<int, PromptTemplate>();
            try
            {
                // Prompt templates are enumerated without a tenant filter, so the matrix owns a private time range instead.
                DateTime start = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(new Random().Next(0, 2_000_000));
                Owners o = await SeedOwnersAsync(fixture, start, token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                foreach (Slot slot in o.Slots)
                {
                    rows[slot.Index] = await _Driver.PromptTemplates.CreateAsync(new PromptTemplate("matrix.template." + slot.Index + "." + suffix, "content " + slot.Index)
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        OwnershipScope = OwnershipScopeEnum.UserSpecific,
                        CreatedUtc = slot.Time
                    }, token).ConfigureAwait(false);
                }

                DateTime after = o.Start.AddSeconds(-1);
                DateTime before = o.Start.AddMinutes(o.Slots.Count);
                List<Scope<PromptTemplate>> scopes = new List<Scope<PromptTemplate>>
                {
                    new Scope<PromptTemplate>("creation range, newest first", s => true, (page, size) =>
                        _Driver.PromptTemplates.EnumerateAsync(new EnumerationQuery { CreatedAfter = after, CreatedBefore = before, Order = EnumerationOrderEnum.CreatedDescending, PageNumber = page, PageSize = size }, token)),
                    new Scope<PromptTemplate>("inner creation range", s => s.Time > o.WindowFrom && s.Time < o.WindowTo, (page, size) =>
                        _Driver.PromptTemplates.EnumerateAsync(new EnumerationQuery { CreatedAfter = o.WindowFrom, CreatedBefore = o.WindowTo, Order = EnumerationOrderEnum.CreatedDescending, PageNumber = page, PageSize = size }, token))
                };
                await VerifyScopesAsync("PromptTemplate", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);
                await VerifyScopesAsync("PromptTemplate", o, rows, r => r.Id, r => r.OrderBy(x => x.Slot.Time), new[]
                {
                    new Scope<PromptTemplate>("creation range, oldest first", s => true, (page, size) =>
                        _Driver.PromptTemplates.EnumerateAsync(new EnumerationQuery { CreatedAfter = after, CreatedBefore = before, Order = EnumerationOrderEnum.CreatedAscending, PageNumber = page, PageSize = size }, token))
                }).ConfigureAwait(false);

                PromptTemplate owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.PromptTemplates.ReadByNameAsync(o.TenantA, owned.Name, token).ConfigureAwait(false), "PromptTemplate name resolves in its own tenant");
                DatabaseAssert.True(await _Driver.PromptTemplates.ReadByNameAsync(o.TenantB, owned.Name, token).ConfigureAwait(false) == null, "PromptTemplate name does not resolve in another tenant");
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (PromptTemplate row in rows.Values) await _Driver.PromptTemplates.DeleteAsync(row.Id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyTokenUsageAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, TokenUsageRecord> rows = new Dictionary<int, TokenUsageRecord>();
            Owners? owners = null;
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                owners = o;
                foreach (Slot slot in o.Slots)
                {
                    TokenUsageRecord record = new TokenUsageRecord
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        VesselId = slot.ScopeId,
                        Model = "matrix-model",
                        Runtime = "ClaudeCode",
                        Source = "mission",
                        SourceId = "msn_matrix_" + slot.Index,
                        InputTokens = 10 + slot.Index,
                        OutputTokens = 20 + slot.Index,
                        TotalTokens = 30 + (2 * slot.Index),
                        CreatedUtc = slot.Time
                    };
                    rows[slot.Index] = await _Driver.TokenUsage.CreateAsync(record, token).ConfigureAwait(false);
                }

                List<Scope<TokenUsageRecord>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<TokenUsageRecord>(s.Label, s.Match, (page, size) => _Driver.TokenUsage.EnumerateAsync(new TokenUsageQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.AddRange(StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<TokenUsageRecord>(s.Label + ", summary rows", s.Match, async (page, size) => AsPage(await _Driver.TokenUsage.EnumerateForSummaryAsync(new TokenUsageQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        VesselId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null
                    }, token).ConfigureAwait(false), page, size))));
                await VerifyScopesAsync("TokenUsage", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);

                TokenUsageRecord owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.TokenUsage.ReadAsync(owned.Id, new TokenUsageQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "TokenUsage reads in its own scope");
                DatabaseAssert.True(await _Driver.TokenUsage.ReadAsync(owned.Id, new TokenUsageQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "TokenUsage is hidden from another tenant");
                DatabaseAssert.True(await _Driver.TokenUsage.ReadAsync(owned.Id, new TokenUsageQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "TokenUsage is hidden from another user");

                DatabaseAssert.Equal(0, await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = o.TenantA, UserId = o.UserB1 }, token).ConfigureAwait(false), "A foreign user deletes no TokenUsage rows");
                DatabaseAssert.Equal(2, await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = o.TenantA, UserId = o.UserA1, VesselId = o.ScopeX }, token).ConfigureAwait(false), "A scoped delete counts only its rows");
                DatabaseAssert.Equal(4, await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), "A tenant delete counts the rest of the tenant");
                DatabaseAssert.Equal(1, await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = o.TenantB }, token).ConfigureAwait(false), "Another tenant keeps its own row until its own delete");
                rows.Clear();
            }
            finally
            {
                if (!_NoCleanup && owners != null && rows.Count > 0)
                {
                    await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = owners.TenantA }, token).ConfigureAwait(false);
                    await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { TenantId = owners.TenantB }, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyRequestHistoryAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Dictionary<int, RequestHistoryEntry> rows = new Dictionary<int, RequestHistoryEntry>();
            Owners? owners = null;
            try
            {
                Owners o = await SeedOwnersAsync(fixture, MatrixStart(), token).ConfigureAwait(false);
                owners = o;
                foreach (Slot slot in o.Slots)
                {
                    RequestHistoryEntry entry = new RequestHistoryEntry
                    {
                        TenantId = slot.TenantId,
                        UserId = slot.UserId,
                        CredentialId = slot.ScopeId,
                        Method = "GET",
                        Route = "/api/v1/matrix/" + slot.Index,
                        StatusCode = 200,
                        IsSuccess = true,
                        CreatedUtc = slot.Time
                    };
                    await _Driver.RequestHistory.CreateAsync(entry, new RequestHistoryDetail { RequestHistoryId = entry.Id, RequestBodyText = "matrix " + slot.Index }, token).ConfigureAwait(false);
                    rows[slot.Index] = entry;
                }

                List<Scope<RequestHistoryEntry>> scopes = StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<RequestHistoryEntry>(s.Label, s.Match, (page, size) => _Driver.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        CredentialId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null,
                        PageNumber = page,
                        PageSize = size
                    }, token))).ToList();
                scopes.AddRange(StandardScopes(o, hasScope: true, hasWindow: true)
                    .Select(s => new Scope<RequestHistoryEntry>(s.Label + ", summary rows", s.Match, async (page, size) => AsPage(await _Driver.RequestHistory.EnumerateForSummaryAsync(new RequestHistoryQuery
                    {
                        TenantId = s.TenantId,
                        UserId = s.UserId,
                        CredentialId = s.ScopeId,
                        FromUtc = s.Window ? o.WindowFrom : null,
                        ToUtc = s.Window ? o.WindowTo : null
                    }, token).ConfigureAwait(false), page, size))));
                await VerifyScopesAsync("RequestHistory", o, rows, r => r.Id, r => r.OrderByDescending(x => x.Slot.Time), scopes).ConfigureAwait(false);

                RequestHistoryEntry owned = rows[0];
                DatabaseAssert.NotNull(await _Driver.RequestHistory.ReadAsync(owned.Id, new RequestHistoryQuery { TenantId = o.TenantA, UserId = o.UserA1, CredentialId = o.ScopeX }, token).ConfigureAwait(false), "RequestHistory reads in its own scope");
                DatabaseAssert.True(await _Driver.RequestHistory.ReadAsync(owned.Id, new RequestHistoryQuery { TenantId = o.TenantB }, token).ConfigureAwait(false) == null, "RequestHistory is hidden from another tenant");
                DatabaseAssert.True(await _Driver.RequestHistory.ReadAsync(owned.Id, new RequestHistoryQuery { TenantId = o.TenantA, UserId = o.UserA2 }, token).ConfigureAwait(false) == null, "RequestHistory is hidden from another user");
                await _Driver.RequestHistory.DeleteAsync(owned.Id, new RequestHistoryQuery { TenantId = o.TenantB }, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(await _Driver.RequestHistory.ReadAsync(owned.Id, null, token).ConfigureAwait(false), "Another tenant cannot delete the RequestHistory entry");

                DatabaseAssert.Equal(0, await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = o.TenantA, UserId = o.UserB1 }, token).ConfigureAwait(false), "A foreign user deletes no RequestHistory rows");
                DatabaseAssert.Equal(2, await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = o.TenantA, UserId = o.UserA1, CredentialId = o.ScopeX }, token).ConfigureAwait(false), "A scoped delete counts only its rows");
                DatabaseAssert.Equal(4, await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = o.TenantA }, token).ConfigureAwait(false), "A tenant delete counts the rest of the tenant");
                DatabaseAssert.Equal(1, await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = o.TenantB }, token).ConfigureAwait(false), "Another tenant keeps its own row until its own delete");
                DatabaseAssert.True(await _Driver.RequestHistory.ReadAsync(owned.Id, null, token).ConfigureAwait(false) == null, "A filtered delete removes the entry");
                rows.Clear();
            }
            finally
            {
                if (!_NoCleanup && owners != null && rows.Count > 0)
                {
                    await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = owners.TenantA }, token).ConfigureAwait(false);
                    await _Driver.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery { TenantId = owners.TenantB }, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }
    }
}
