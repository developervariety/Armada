namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="EntityResolver"/>: id/name/substring and git-remote-url
    /// resolution across vessels, captains, missions, voyages, and fleets. Positive cases
    /// assert unambiguous matches; negative cases assert null returns for empty/null lists,
    /// no-match, and ambiguous (multi-match) inputs. Audit additions extend the null/empty/
    /// ambiguous coverage to captains, missions, voyages, and fleets.
    /// </summary>
    public sealed class EntityResolverSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the EntityResolver suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // Vessel Resolution

            cases.Add(Case("resolve_vessel_by_id_returns_match", "ResolveVessel ById ReturnsMatch", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp") { Id = "vsl_abc123" },
                    new Vessel("other", "https://github.com/user/other") { Id = "vsl_def456" }
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "vsl_abc123");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_name_returns_match", "ResolveVessel ByName ReturnsMatch", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp"),
                    new Vessel("other", "https://github.com/user/other")
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "myapp");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_name_case_insensitive_returns_match", "ResolveVessel ByNameCaseInsensitive ReturnsMatch", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("MyApp", "https://github.com/user/myapp")
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "myapp");
                AssertNotNull(result);
                AssertEqual("MyApp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_substring_returns_single_match", "ResolveVessel BySubstring ReturnsSingleMatch", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("my-awesome-app", "https://github.com/user/myapp"),
                    new Vessel("other-service", "https://github.com/user/other")
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "awesome");
                AssertNotNull(result);
                AssertEqual("my-awesome-app", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_ambiguous_substring_returns_null", "ResolveVessel AmbiguousSubstring ReturnsNull", TestTags.Negative, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("app-one", "https://github.com/user/app-one"),
                    new Vessel("app-two", "https://github.com/user/app-two")
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "app");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_vessel_empty_list_returns_null", "ResolveVessel EmptyList ReturnsNull", TestTags.Negative, () =>
            {
                Vessel? result = EntityResolver.ResolveVessel(new List<Vessel>(), "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_vessel_null_list_returns_null", "ResolveVessel NullList ReturnsNull", TestTags.Negative, () =>
            {
                Vessel? result = EntityResolver.ResolveVessel(null!, "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_vessel_no_match_returns_null", "ResolveVessel NoMatch ReturnsNull", TestTags.Negative, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp")
                };

                Vessel? result = EntityResolver.ResolveVessel(vessels, "nonexistent");
                AssertNull(result);
            }));

            // Captain Resolution

            cases.Add(Case("resolve_captain_by_id_returns_match", "ResolveCaptain ById ReturnsMatch", TestTags.Positive, () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    new Captain("claude-1") { Id = "cpt_abc123" },
                    new Captain("claude-2") { Id = "cpt_def456" }
                };

                Captain? result = EntityResolver.ResolveCaptain(captains, "cpt_abc123");
                AssertNotNull(result);
                AssertEqual("claude-1", result!.Name);
            }));

            cases.Add(Case("resolve_captain_by_name_returns_match", "ResolveCaptain ByName ReturnsMatch", TestTags.Positive, () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    new Captain("claude-1"),
                    new Captain("codex-1")
                };

                Captain? result = EntityResolver.ResolveCaptain(captains, "claude-1");
                AssertNotNull(result);
                AssertEqual("claude-1", result!.Name);
            }));

            cases.Add(Case("resolve_captain_by_substring_returns_single_match", "ResolveCaptain BySubstring ReturnsSingleMatch", TestTags.Positive, () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    new Captain("claude-alpha"),
                    new Captain("codex-beta")
                };

                Captain? result = EntityResolver.ResolveCaptain(captains, "alpha");
                AssertNotNull(result);
                AssertEqual("claude-alpha", result!.Name);
            }));

            cases.Add(Case("resolve_captain_null_list_returns_null", "ResolveCaptain NullList ReturnsNull", TestTags.Negative, () =>
            {
                Captain? result = EntityResolver.ResolveCaptain(null!, "anything");
                AssertNull(result);
            }));

            // Audit additions: captain empty-list, no-match, and ambiguous paths (confirmed against source)

            cases.Add(Case("resolve_captain_empty_list_returns_null", "ResolveCaptain EmptyList ReturnsNull", TestTags.Negative, () =>
            {
                Captain? result = EntityResolver.ResolveCaptain(new List<Captain>(), "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_captain_no_match_returns_null", "ResolveCaptain NoMatch ReturnsNull", TestTags.Negative, () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    new Captain("claude-1")
                };

                Captain? result = EntityResolver.ResolveCaptain(captains, "nonexistent");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_captain_ambiguous_substring_returns_null", "ResolveCaptain AmbiguousSubstring ReturnsNull", TestTags.Negative, () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    new Captain("claude-alpha"),
                    new Captain("claude-beta")
                };

                Captain? result = EntityResolver.ResolveCaptain(captains, "claude");
                AssertNull(result);
            }));

            // Mission Resolution

            cases.Add(Case("resolve_mission_by_id_returns_match", "ResolveMission ById ReturnsMatch", TestTags.Positive, () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Fix login bug") { Id = "msn_abc123" },
                    new Mission("Add tests") { Id = "msn_def456" }
                };

                Mission? result = EntityResolver.ResolveMission(missions, "msn_abc123");
                AssertNotNull(result);
                AssertEqual("Fix login bug", result!.Title);
            }));

            cases.Add(Case("resolve_mission_by_title_substring_returns_single_match", "ResolveMission ByTitleSubstring ReturnsSingleMatch", TestTags.Positive, () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Fix login bug"),
                    new Mission("Add user registration")
                };

                Mission? result = EntityResolver.ResolveMission(missions, "login");
                AssertNotNull(result);
                AssertEqual("Fix login bug", result!.Title);
            }));

            cases.Add(Case("resolve_mission_ambiguous_title_returns_null", "ResolveMission AmbiguousTitle ReturnsNull", TestTags.Negative, () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Fix bug in login"),
                    new Mission("Fix bug in signup")
                };

                Mission? result = EntityResolver.ResolveMission(missions, "Fix bug");
                AssertNull(result);
            }));

            // Audit additions: mission null-list, empty-list, and no-match paths (confirmed against source)

            cases.Add(Case("resolve_mission_null_list_returns_null", "ResolveMission NullList ReturnsNull", TestTags.Negative, () =>
            {
                Mission? result = EntityResolver.ResolveMission(null!, "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_mission_empty_list_returns_null", "ResolveMission EmptyList ReturnsNull", TestTags.Negative, () =>
            {
                Mission? result = EntityResolver.ResolveMission(new List<Mission>(), "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_mission_no_match_returns_null", "ResolveMission NoMatch ReturnsNull", TestTags.Negative, () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Fix login bug")
                };

                Mission? result = EntityResolver.ResolveMission(missions, "nonexistent");
                AssertNull(result);
            }));

            // Voyage Resolution

            cases.Add(Case("resolve_voyage_by_id_returns_match", "ResolveVoyage ById ReturnsMatch", TestTags.Positive, () =>
            {
                List<Voyage> voyages = new List<Voyage>
                {
                    new Voyage("API Hardening") { Id = "vyg_abc123" },
                    new Voyage("UI Refresh") { Id = "vyg_def456" }
                };

                Voyage? result = EntityResolver.ResolveVoyage(voyages, "vyg_abc123");
                AssertNotNull(result);
                AssertEqual("API Hardening", result!.Title);
            }));

            cases.Add(Case("resolve_voyage_by_title_substring_returns_single_match", "ResolveVoyage ByTitleSubstring ReturnsSingleMatch", TestTags.Positive, () =>
            {
                List<Voyage> voyages = new List<Voyage>
                {
                    new Voyage("API Hardening"),
                    new Voyage("UI Refresh")
                };

                Voyage? result = EntityResolver.ResolveVoyage(voyages, "Hardening");
                AssertNotNull(result);
                AssertEqual("API Hardening", result!.Title);
            }));

            // Audit additions: voyage null-list and ambiguous paths (confirmed against source)

            cases.Add(Case("resolve_voyage_null_list_returns_null", "ResolveVoyage NullList ReturnsNull", TestTags.Negative, () =>
            {
                Voyage? result = EntityResolver.ResolveVoyage(null!, "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_voyage_ambiguous_title_returns_null", "ResolveVoyage AmbiguousTitle ReturnsNull", TestTags.Negative, () =>
            {
                List<Voyage> voyages = new List<Voyage>
                {
                    new Voyage("API Hardening"),
                    new Voyage("API Cleanup")
                };

                Voyage? result = EntityResolver.ResolveVoyage(voyages, "API");
                AssertNull(result);
            }));

            // Fleet Resolution

            cases.Add(Case("resolve_fleet_by_name_returns_match", "ResolveFleet ByName ReturnsMatch", TestTags.Positive, () =>
            {
                List<Fleet> fleets = new List<Fleet>
                {
                    new Fleet("production"),
                    new Fleet("staging")
                };

                Fleet? result = EntityResolver.ResolveFleet(fleets, "production");
                AssertNotNull(result);
                AssertEqual("production", result!.Name);
            }));

            cases.Add(Case("resolve_fleet_by_substring_returns_single_match", "ResolveFleet BySubstring ReturnsSingleMatch", TestTags.Positive, () =>
            {
                List<Fleet> fleets = new List<Fleet>
                {
                    new Fleet("my-production-fleet"),
                    new Fleet("staging-fleet")
                };

                Fleet? result = EntityResolver.ResolveFleet(fleets, "production");
                AssertNotNull(result);
                AssertEqual("my-production-fleet", result!.Name);
            }));

            // Audit additions: fleet null-list and ambiguous paths (confirmed against source)

            cases.Add(Case("resolve_fleet_null_list_returns_null", "ResolveFleet NullList ReturnsNull", TestTags.Negative, () =>
            {
                Fleet? result = EntityResolver.ResolveFleet(null!, "anything");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_fleet_ambiguous_substring_returns_null", "ResolveFleet AmbiguousSubstring ReturnsNull", TestTags.Negative, () =>
            {
                List<Fleet> fleets = new List<Fleet>
                {
                    new Fleet("prod-us"),
                    new Fleet("prod-eu")
                };

                Fleet? result = EntityResolver.ResolveFleet(fleets, "prod");
                AssertNull(result);
            }));

            // Remote URL Resolution

            cases.Add(Case("resolve_vessel_by_remote_url_exact_match_returns_vessel", "ResolveVesselByRemoteUrl ExactMatch ReturnsVessel", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp.git"),
                    new Vessel("other", "https://github.com/user/other.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "https://github.com/user/myapp.git");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_without_git_suffix_matches", "ResolveVesselByRemoteUrl WithoutGitSuffix Matches", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "https://github.com/user/myapp");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_ssh_to_https_matches", "ResolveVesselByRemoteUrl SshToHttps Matches", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "git@github.com:user/myapp.git");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_https_to_ssh_matches", "ResolveVesselByRemoteUrl HttpsToSsh Matches", TestTags.Positive, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "git@github.com:user/myapp.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "https://github.com/user/myapp");
                AssertNotNull(result);
                AssertEqual("myapp", result!.Name);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_no_match_returns_null", "ResolveVesselByRemoteUrl NoMatch ReturnsNull", TestTags.Negative, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "https://github.com/user/other.git");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_empty_url_returns_null", "ResolveVesselByRemoteUrl EmptyUrl ReturnsNull", TestTags.Negative, () =>
            {
                List<Vessel> vessels = new List<Vessel>
                {
                    new Vessel("myapp", "https://github.com/user/myapp.git")
                };

                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(vessels, "");
                AssertNull(result);
            }));

            cases.Add(Case("resolve_vessel_by_remote_url_null_list_returns_null", "ResolveVesselByRemoteUrl NullList ReturnsNull", TestTags.Negative, () =>
            {
                Vessel? result = EntityResolver.ResolveVesselByRemoteUrl(null!, "https://github.com/user/myapp");
                AssertNull(result);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.EntityResolver",
                displayName: "Entity Resolver",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.EntityResolver",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.EntityResolver",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
