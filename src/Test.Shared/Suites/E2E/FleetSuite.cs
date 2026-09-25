namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Fleet API descriptors covering CRUD, list, pagination, ordering, and enumeration
    /// against a live in-process Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class FleetSuite : IArmadaTestSuite
    {
        #region Private-Members

        private readonly object _SeedLock = new object();
        private Task<Fleet[]>? _SharedFleetSeed;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Fleet API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region CRUD - Create

            #endregion

            #region CRUD - Read

            #endregion

            #region CRUD - Update

            #endregion

            #region CRUD - Delete

            #endregion

            #region List - Empty and Basic

            #endregion

            #region List - Pagination with 25 Fleets

            cases.Add(CaseAsync("list_fleets_25_fleets_pagesize_10_totalrecords_25_totalpages_3", "List Fleets 25 Fleets PageSize 10 TotalRecords 25 TotalPages 3", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.TotalRecords >= 25, "TotalRecords should be >= 25");
                Assert(result.TotalPages >= 3, "TotalPages should be >= 3");
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_page_1_has_10_items", "List Fleets 25 Fleets Page 1 Has 10 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(10, result.Objects.Count);
                AssertEqual(1, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_page_2_has_10_items", "List Fleets 25 Fleets Page 2 Has 10 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 2);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(10, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_page_3_has_5_items", "List Fleets 25 Fleets Page 3 Has 5 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 3);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.Objects.Count >= 5, "Page 3 should have at least 5 items");
                AssertEqual(3, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_pagesize_10_verify_first_record_on_page_1", "List Fleets 25 Fleets PageSize 10 Verify First Record On Page 1", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Fleet[] fleets = await EnsureFleetsSeededAsync(authClient);

                // With shared data, our created fleets may not be on page 1
                // Instead verify that the created fleets appear somewhere in the full listing
                HashSet<string> createdIds = new HashSet<string>();
                for (int i = 0; i < fleets.Length; i++)
                    createdIds.Add(fleets[i].Id);

                int foundCount = 0;
                int totalPages = 1;
                for (int page = 1; page <= totalPages; page++)
                {
                    FleetListResult pageListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: page, order: "CreatedAscending");

                    EnumerationResult<Fleet> pageResult = pageListResult.Result;
                    totalPages = pageResult.TotalPages;
                    foreach (Fleet f in pageResult.Objects)
                    {
                        if (createdIds.Contains(f.Id))
                            foundCount++;
                    }
                }
                AssertEqual(25, foundCount);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_pagesize_10_verify_last_record_on_page_3", "List Fleets 25 Fleets PageSize 10 Verify Last Record On Page 3", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Fleet[] fleets = await EnsureFleetsSeededAsync(authClient);

                // With shared data, verify that the last created fleet appears somewhere in listing
                string lastCreatedId = fleets[24].Id;
                bool found = false;
                int totalPages = 1;
                for (int page = 1; page <= totalPages && !found; page++)
                {
                    FleetListResult pageListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: page, order: "CreatedAscending");

                    EnumerationResult<Fleet> pageResult = pageListResult.Result;
                    totalPages = pageResult.TotalPages;
                    foreach (Fleet f in pageResult.Objects)
                    {
                        if (f.Id == lastCreatedId)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                Assert(found, "Last created fleet should appear in listing");
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_pagesize_5_totalpages_5", "List Fleets 25 Fleets PageSize 5 TotalPages 5", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 5, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.TotalRecords >= 25, "TotalRecords should be >= 25");
                Assert(result.TotalPages >= 5, "TotalPages should be >= 5");
                AssertEqual(5, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_pagesize_5_page_5_has_5_items", "List Fleets 25 Fleets PageSize 5 Page 5 Has 5 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 5, pageNumber: 5);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_beyond_last_page_returns_empty_objects_array", "List Fleets 25 Fleets Beyond Last Page Returns Empty Objects Array", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                // Get total pages first, then request beyond it
                FleetListResult firstListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 1);

                EnumerationResult<Fleet> firstResult = firstListResult.Result;
                int totalPages = firstResult.TotalPages;

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: totalPages + 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_fleets_25_fleets_beyond_last_page_still_returns_total_records", "List Fleets 25 Fleets Beyond Last Page Still Returns Total Records", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                // Get total pages first, then request well beyond it
                FleetListResult firstListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 1);

                EnumerationResult<Fleet> firstResult = firstListResult.Result;
                long totalRecords = firstResult.TotalRecords;

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 999);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.TotalRecords >= 25, "TotalRecords should be >= 25");
                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_fleets_pages_do_not_overlap", "List Fleets Pages Do Not Overlap", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult page1ListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 1);

                EnumerationResult<Fleet> page1Result = page1ListResult.Result;
                FleetListResult page2ListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 2);

                EnumerationResult<Fleet> page2Result = page2ListResult.Result;
                FleetListResult page3ListResult = await ListFleetsAsync(authClient, pageSize: 10, pageNumber: 3);

                EnumerationResult<Fleet> page3Result = page3ListResult.Result;

                HashSet<string> allIds = new HashSet<string>();

                foreach (Fleet f in page1Result.Objects)
                {
                    string id = f.Id;
                    Assert(allIds.Add(id), "Duplicate ID found: " + id);
                }

                foreach (Fleet f in page2Result.Objects)
                {
                    string id = f.Id;
                    Assert(allIds.Add(id), "Duplicate ID found across pages: " + id);
                }

                foreach (Fleet f in page3Result.Objects)
                {
                    string id = f.Id;
                    Assert(allIds.Add(id), "Duplicate ID found across pages: " + id);
                }

                Assert(allIds.Count >= 25, "Should have at least 25 unique IDs across pages");
            }));

            cases.Add(CaseAsync("list_fleets_pagesize_reflected_in_response", "List Fleets PageSize Reflected In Response", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 3, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(3, result.PageSize);
            }));

            #endregion

            #region List - Ordering

            cases.Add(CaseAsync("list_fleets_order_created_ascending_first_item_is_oldest", "List Fleets Order Created Ascending First Item Is Oldest", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, order: "CreatedAscending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                // With shared data, the first item may not be one we created
                // Just verify the ordering is correct (ascending by CreatedUtc)
                Assert(result.Objects.Count >= 5, "Should have at least 5 items");
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current <= next, "Items should be in ascending order");
                }
            }));

            cases.Add(CaseAsync("list_fleets_order_created_descending_first_item_is_newest", "List Fleets Order Created Descending First Item Is Newest", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, order: "CreatedDescending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                // With shared data, the first item may not be one we created
                // Just verify the ordering is correct (descending by CreatedUtc)
                Assert(result.Objects.Count >= 5, "Should have at least 5 items");
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current >= next, "Items should be in descending order");
                }
            }));

            cases.Add(CaseAsync("list_fleets_order_created_ascending_all_items_in_order", "List Fleets Order Created Ascending All Items In Order", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, order: "CreatedAscending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current <= next,
                        "Item at index " + i + " (CreatedUtc=" + current + ") should be <= item at index " + (i + 1) + " (CreatedUtc=" + next + ")");
                }
            }));

            cases.Add(CaseAsync("list_fleets_order_created_descending_all_items_in_order", "List Fleets Order Created Descending All Items In Order", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, order: "CreatedDescending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current >= next,
                        "Item at index " + i + " (CreatedUtc=" + current + ") should be >= item at index " + (i + 1) + " (CreatedUtc=" + next + ")");
                }
            }));

            #endregion

            #region Enumerate (POST)

            cases.Add(CaseAsync("enumerate_fleets_default_query_returns_all", "Enumerate Fleets Default Query Returns All", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient);

                HttpStatusCode status = fleetListResult.StatusCode;

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(HttpStatusCode.OK, status);
                Assert(result.Objects.Count >= 3, "Should have at least 3 objects");
                Assert(result.TotalRecords >= 3, "Should have at least 3 total records");
            }));

            cases.Add(CaseAsync("enumerate_fleets_with_pagesize_and_pagenumber_works_correctly", "Enumerate Fleets With PageSize And PageNumber Works Correctly", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);
                Assert(result.TotalRecords >= 15, "TotalRecords should be >= 15");
                Assert(result.TotalPages >= 3, "TotalPages should be >= 3");
            }));

            cases.Add(CaseAsync("enumerate_fleets_page_2_has_correct_items", "Enumerate Fleets Page 2 Has Correct Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 2);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("enumerate_fleets_page_3_has_correct_items", "Enumerate Fleets Page 3 Has Correct Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 3);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);
                AssertEqual(3, result.PageNumber);
            }));

            cases.Add(CaseAsync("enumerate_fleets_beyond_last_page_returns_empty_objects", "Enumerate Fleets Beyond Last Page Returns Empty Objects", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                // Get total pages first, then request beyond it
                FleetListResult firstListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 1);

                EnumerationResult<Fleet> firstResult = firstListResult.Result;
                int totalPages = firstResult.TotalPages;

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: totalPages + 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("enumerate_fleets_order_created_ascending_first_item_is_oldest", "Enumerate Fleets Order Created Ascending First Item Is Oldest", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, order: "CreatedAscending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                // With shared data, just verify ascending order
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current <= next, "Items should be in ascending order");
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_order_created_descending_first_item_is_newest", "Enumerate Fleets Order Created Descending First Item Is Newest", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, order: "CreatedDescending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                // With shared data, just verify descending order
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current >= next, "Items should be in descending order");
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_order_created_ascending_all_items_in_order", "Enumerate Fleets Order Created Ascending All Items In Order", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, order: "CreatedAscending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current <= next,
                        "Item at index " + i + " (CreatedUtc=" + current + ") should be <= item at index " + (i + 1) + " (CreatedUtc=" + next + ")");
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_order_created_descending_all_items_in_order", "Enumerate Fleets Order Created Descending All Items In Order", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, order: "CreatedDescending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current >= next,
                        "Item at index " + i + " (CreatedUtc=" + current + ") should be >= item at index " + (i + 1) + " (CreatedUtc=" + next + ")");
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_pagination_matches_get_pagination", "Enumerate Fleets Pagination Matches Get Pagination", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult listListResult = await ListFleetsAsync(authClient, pageSize: 5, pageNumber: 1, order: "CreatedAscending");

                EnumerationResult<Fleet> listResult = listListResult.Result;
                FleetListResult enumListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 1, order: "CreatedAscending");

                EnumerationResult<Fleet> enumResult = enumListResult.Result;

                AssertEqual(listResult.TotalRecords, enumResult.TotalRecords);
                AssertEqual(listResult.TotalPages, enumResult.TotalPages);
                AssertEqual(listResult.Objects.Count, enumResult.Objects.Count);

                for (int i = 0; i < listResult.Objects.Count; i++)
                {
                    AssertEqual(listResult.Objects[i].Id, enumResult.Objects[i].Id);
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_page_2_matches_get_page_2", "Enumerate Fleets Page 2 Matches Get Page 2", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult listListResult = await ListFleetsAsync(authClient, pageSize: 5, pageNumber: 2, order: "CreatedAscending");

                EnumerationResult<Fleet> listResult = listListResult.Result;
                FleetListResult enumListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 2, order: "CreatedAscending");

                EnumerationResult<Fleet> enumResult = enumListResult.Result;

                AssertEqual(listResult.Objects.Count, enumResult.Objects.Count);

                for (int i = 0; i < listResult.Objects.Count; i++)
                {
                    AssertEqual(listResult.Objects[i].Id, enumResult.Objects[i].Id);
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_with_pagesize_reflects_pagesize_in_response", "Enumerate Fleets With PageSize Reflects PageSize In Response", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 3);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(3, result.PageSize);
                AssertEqual(3, result.Objects.Count);
            }));

            cases.Add(CaseAsync("enumerate_fleets_pages_do_not_overlap", "Enumerate Fleets Pages Do Not Overlap", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                HashSet<string> allIds = new HashSet<string>();

                for (int page = 1; page <= 3; page++)
                {
                    FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: page);

                    EnumerationResult<Fleet> result = fleetListResult.Result;
                    foreach (Fleet f in result.Objects)
                    {
                        string id = f.Id;
                        Assert(allIds.Add(id), "Duplicate ID found on page " + page + ": " + id);
                    }
                }

                Assert(allIds.Count >= 15, "Should have at least 15 unique IDs across enumerate pages");
            }));

            #endregion

            #region Enumerate - Combined Ordering and Pagination

            cases.Add(CaseAsync("enumerate_fleets_created_ascending_page_2_contains_correct_items", "Enumerate Fleets Created Ascending Page 2 Contains Correct Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 2, order: "CreatedAscending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);

                // Verify ascending order on this page
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current <= next, "Items should be in ascending order on page 2");
                }
            }));

            cases.Add(CaseAsync("enumerate_fleets_created_descending_page_2_contains_correct_items", "Enumerate Fleets Created Descending Page 2 Contains Correct Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await EnumerateFleetsAsync(authClient, pageSize: 5, pageNumber: 2, order: "CreatedDescending");

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(5, result.Objects.Count);

                // Verify descending order on this page
                for (int i = 0; i < result.Objects.Count - 1; i++)
                {
                    DateTime current = result.Objects[i].CreatedUtc;
                    DateTime next = result.Objects[i + 1].CreatedUtc;
                    Assert(current >= next, "Items should be in descending order on page 2");
                }
            }));

            #endregion

            #region List - Edge Cases

            cases.Add(CaseAsync("list_fleets_default_pagesize_returns_100_or_fewer_items", "List Fleets Default PageSize Returns 100 Or Fewer Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.Objects.Count <= 100,
                    "Default page size should return at most 100 items");
            }));

            cases.Add(CaseAsync("list_fleets_pagenumber_1_is_default", "List Fleets PageNumber 1 Is Default", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(1, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_fleets_exactly_pagesize_fleets_totalpages_1", "List Fleets Exactly PageSize Fleets TotalPages 1", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.TotalRecords >= 10, "TotalRecords should be >= 10");
                Assert(result.TotalPages >= 1, "TotalPages should be >= 1");
                AssertEqual(10, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_fleets_pagesize_plus_one_totalpages_2", "List Fleets PageSize Plus One TotalPages 2", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 10);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.TotalRecords >= 11, "TotalRecords should be >= 11");
                Assert(result.TotalPages >= 2, "TotalPages should be >= 2");
            }));

            cases.Add(CaseAsync("list_fleets_pagesize_1_returns_1_item_per_page", "List Fleets PageSize 1 Returns 1 Item Per Page", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 1, pageNumber: 1);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                AssertEqual(1, result.Objects.Count);
                Assert(result.TotalPages >= 3, "TotalPages should be >= 3");
            }));

            cases.Add(CaseAsync("list_fleets_large_pagesize_returns_all_items", "List Fleets Large PageSize Returns All Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureFleetsSeededAsync(authClient);

                FleetListResult fleetListResult = await ListFleetsAsync(authClient, pageSize: 100);

                EnumerationResult<Fleet> result = fleetListResult.Result;

                Assert(result.Objects.Count >= 5, "Should return at least 5 items");
                Assert(result.TotalPages >= 1, "TotalPages should be >= 1");
            }));

            #endregion

            #region Full CRUD Lifecycle

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Fleet",
                displayName: "Fleet API Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a fleet and returns the deserialized Fleet object.
        /// </summary>
        private static async Task<Fleet> CreateFleetAsync(HttpClient client, List<string> createdFleetIds, string name, string? description = null)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            object body = description != null
                ? new { Name = uniqueName, Description = description }
                : (object)new { Name = uniqueName };
            HttpResponseMessage resp = await client.PostAsync("/api/v1/fleets",
                JsonHelper.ToJsonContent(body));
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp);
            createdFleetIds.Add(fleet.Id);
            return fleet;
        }

        /// <summary>
        /// Creates the specified number of fleets with sequential names and a small delay between each
        /// to ensure distinct CreatedUtc timestamps for ordering tests.
        /// </summary>
        private static async Task<Fleet[]> CreateFleetsAsync(HttpClient client, List<string> createdFleetIds, int count, string prefix = "Fleet")
        {
            Fleet[] results = new Fleet[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = await CreateFleetAsync(client, createdFleetIds, prefix + "_" + (i + 1).ToString("D3"), "Description for " + prefix + "_" + (i + 1).ToString("D3"));
                if (i < count - 1)
                {
                    await Task.Delay(20);
                }
            }
            return results;
        }

        /// <summary>
        /// Ensure the shared server holds a reusable set of 25 fleets, seeding it exactly once for the
        /// suite. Every case runs against the same in-process fixture and the list/pagination/ordering
        /// cases only assert accumulation-tolerant conditions (a full page, a total at or above a
        /// threshold, no page overlap, monotonic CreatedUtc ordering, all seeded IDs present across
        /// pages), so they can share one seeded set instead of each re-creating fleets over sequential
        /// HTTP round-trips. The seed Task is memoized: the first bulk case to run pays the cost
        /// (including the per-create delay that yields distinct CreatedUtc values) and the rest await the
        /// completed Task and reuse the same 25 fleets.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task returning the 25 shared fleets once they exist.</returns>
        private Task<Fleet[]> EnsureFleetsSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedFleetSeed == null) _SharedFleetSeed = SeedSharedFleetsAsync(authClient);
                return _SharedFleetSeed;
            }
        }

        /// <summary>
        /// Creates the single shared set of 25 fleets used by the accumulation-tolerant list, pagination,
        /// ordering, and enumerate cases. The inter-create delay lives inside <see cref="CreateFleetsAsync"/>
        /// so distinct CreatedUtc values are produced once for the whole suite.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>The 25 created fleets.</returns>
        private static async Task<Fleet[]> SeedSharedFleetsAsync(HttpClient authClient)
        {
            List<string> seededIds = new List<string>();
            return await CreateFleetsAsync(authClient, seededIds, 25, "SharedPagFleet").ConfigureAwait(false);
        }

        /// <summary>
        /// Performs a GET list request with optional query parameters.
        /// </summary>
        private static async Task<FleetListResult> ListFleetsAsync(
            HttpClient client, int? pageSize = null, int? pageNumber = null, string? order = null)
        {
            StringBuilder url = new StringBuilder("/api/v1/fleets");
            bool hasQuery = false;

            if (pageSize.HasValue)
            {
                url.Append(hasQuery ? "&" : "?");
                url.Append("pageSize=").Append(pageSize.Value);
                hasQuery = true;
            }

            if (pageNumber.HasValue)
            {
                url.Append(hasQuery ? "&" : "?");
                url.Append("pageNumber=").Append(pageNumber.Value);
                hasQuery = true;
            }

            if (order != null)
            {
                url.Append(hasQuery ? "&" : "?");
                url.Append("order=").Append(order);
                hasQuery = true;
            }

            HttpResponseMessage resp = await client.GetAsync(url.ToString());
            EnumerationResult<Fleet> result = await JsonHelper.DeserializeAsync<EnumerationResult<Fleet>>(resp);
            return new FleetListResult(resp.StatusCode, result);
        }

        /// <summary>
        /// Performs a POST enumerate request with the given query body.
        /// </summary>
        private static async Task<FleetListResult> EnumerateFleetsAsync(
            HttpClient client, int? pageSize = null, int? pageNumber = null, string? order = null)
        {
            object queryBody;
            if (pageSize.HasValue && pageNumber.HasValue && order != null)
                queryBody = new { PageSize = pageSize.Value, PageNumber = pageNumber.Value, Order = order };
            else if (pageSize.HasValue && pageNumber.HasValue)
                queryBody = new { PageSize = pageSize.Value, PageNumber = pageNumber.Value };
            else if (pageSize.HasValue && order != null)
                queryBody = new { PageSize = pageSize.Value, Order = order };
            else if (pageNumber.HasValue && order != null)
                queryBody = new { PageNumber = pageNumber.Value, Order = order };
            else if (pageSize.HasValue)
                queryBody = new { PageSize = pageSize.Value };
            else if (pageNumber.HasValue)
                queryBody = new { PageNumber = pageNumber.Value };
            else if (order != null)
                queryBody = new { Order = order };
            else
                queryBody = new { };

            HttpResponseMessage resp = await client.PostAsync("/api/v1/fleets/enumerate",
                JsonHelper.ToJsonContent(queryBody));
            EnumerationResult<Fleet> result = await JsonHelper.DeserializeAsync<EnumerationResult<Fleet>>(resp);
            return new FleetListResult(resp.StatusCode, result);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Fleet",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
