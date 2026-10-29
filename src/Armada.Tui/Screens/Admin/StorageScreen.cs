namespace Armada.Tui.Screens.Admin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Storage;
    using Armada.Tui.Routing;
    using Armada.Tui.Screens.Kit;
    using Armada.Tui.Widgets;

    /// <summary>Read-only global administrator storage inventory. Refresh reads cached data; collection is explicit. UI-thread only.</summary>
    public class StorageScreen : GridScreen<StorageMetricRow>
    {
        private bool _Loading;

        /// <summary>Construct the storage screen and read its cached inventory.</summary>
        public StorageScreen(RouteMatch route, TuiContext context)
            : base(route, context, "Storage", "Managed file bytes, separate database allocation, records, volumes and retention. Enter opens details.", "storage", r => r.Group + r.Host + r.Name)
        {
            Header.AddButton("Refresh view", Refresh, "r");
            Header.AddButton("Collect now", () => Load(true, false), "c");
            Header.AddButton("Count records exactly", () => Load(true, true), "e");
            AddFixed(Header, w => Header.HeightFor(w));
            Grid.MultiSelect = false;
            Grid.AddColumn(new GridColumn<StorageMetricRow>("group", "Category", r => Context.Loc.T(r.Group)) { Width = 15 });
            Grid.AddColumn(new GridColumn<StorageMetricRow>("host", "Host", r => r.Host) { Width = 16 });
            Grid.AddColumn(new GridColumn<StorageMetricRow>("name", "Name", r => r.Name) { Weight = 3 });
            Grid.AddColumn(new GridColumn<StorageMetricRow>("amount", "Value", r => r.Amount) { Width = 22 });
            Grid.AddColumn(new GridColumn<StorageMetricRow>("status", "Status", r => Context.Loc.T(r.Status)) { Width = 14 });
            AddFill(Grid);
            Scope.Focus(Grid);
            Refresh();
        }

        /// <inheritdoc />
        public override void Refresh()
        {
            Load(false, false);
        }

        /// <inheritdoc />
        protected override void OnActivate(StorageMetricRow row)
        {
            ScreenOps.ShowText(Context, row.Name, row.Name + "\n" + row.Amount + "\n" + Context.Loc.T(row.Status) + "\n\n" + row.Detail);
        }

        private void Load(bool collect, bool exact)
        {
            if (!Context.Session.IsGlobalAdmin)
            {
                Grid.EmptyText = "Storage is available to system administrators only.";
                return;
            }
            if (_Loading) return;
            _Loading = true;
            _ = ScreenOps.Run(Context, async () =>
            {
                StorageSnapshot? read = collect
                    ? await Context.Client.RefreshStorageAsync(new StorageRefreshRequest { ExactCounts = exact }).ConfigureAwait(false)
                    : await Context.Client.GetStorageAsync().ConfigureAwait(false);
                // The snapshot carries host totals only; the host reports are paged.
                if (read != null) read.Hosts = await Context.Client.ListAllStorageHostsAsync().ConfigureAwait(false);
                return read;
            }, snapshot =>
            {
                _Loading = false;
                if (snapshot != null) Show(snapshot);
            }, "Storage could not be loaded", _ => _Loading = false);
        }

        private void Show(StorageSnapshot snapshot)
        {
            List<StorageMetricRow> rows = new List<StorageMetricRow>();
            foreach (StorageHostReport host in snapshot.Hosts)
            {
                string measured = host.MeasuredUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                foreach (StorageLocation location in host.Locations)
                {
                    string detail = "Measured: " + measured + "\nFiles: " + location.FileCount + "\nEntity: " + location.EntityId + "\n" + String.Join("\n", location.Warnings);
                    foreach (StorageFile file in location.LargestFiles) detail += "\n" + file.Path + ": " + Bytes(file.Bytes);
                    rows.Add(new StorageMetricRow { Group = location.Category, Host = host.Name, Name = location.Path, Amount = Bytes(location.Bytes), Status = host.Stale ? "Stale" : location.Status, Detail = detail });
                }
                foreach (StorageVolume volume in host.Volumes) rows.Add(new StorageMetricRow { Group = "Volumes", Host = host.Name, Name = volume.Path, Amount = Bytes(volume.AvailableBytes) + " free", Status = volume.Status, Detail = "Capacity including unrelated files: " + Bytes(volume.TotalBytes) });
            }
            rows.Add(new StorageMetricRow { Group = "Database", Host = snapshot.Database.Provider, Name = "Allocation (separate from file bytes)", Amount = Bytes(snapshot.Database.AllocatedBytes), Status = snapshot.Database.Status, Detail = String.Join("\n", snapshot.Database.Warnings) });
            foreach (StorageTable table in snapshot.Database.Tables) rows.Add(new StorageMetricRow { Group = "Records", Host = snapshot.Database.Provider, Name = table.Schema + "." + table.Name, Amount = table.Rows?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unavailable", Status = table.CountKind, Detail = "Logical data: " + Bytes(table.LogicalBytes) + "\nData allocation: " + Bytes(table.DataBytes) + "\nIndexes: " + Bytes(table.IndexBytes) + "\n" + table.Warning });
            foreach (KeyValuePair<string, int> policy in snapshot.Retention) rows.Add(new StorageMetricRow { Group = "Retention", Name = policy.Key, Amount = policy.Value.ToString(CultureInfo.CurrentCulture), Detail = "Days, with zero disabling expiry. BuildSlots counts retained builds. No cleanup actions." });
            foreach (StorageHistoryPoint point in snapshot.History) rows.Add(new StorageMetricRow { Group = "History", Name = point.MeasuredUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Amount = Bytes(point.ObservedFileBytes), Detail = "Process-local history resets on restart. Database allocation: " + Bytes(point.DatabaseBytes) });
            Grid.EmptyText = snapshot.Collecting ? "Collecting... Refresh view to read the result." : "No storage measurements yet.";
            Grid.SetLocalRows(rows);
        }

        private static string Bytes(long? value)
        {
            return value.HasValue ? value.Value.ToString("N0", CultureInfo.CurrentCulture) + " B" : "Unavailable";
        }
    }
}
