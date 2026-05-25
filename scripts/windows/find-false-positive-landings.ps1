param(
  [string]$DatabasePath = "armada.db",
  [switch]$SqlOnly
)

$query = @"
SELECT
  m.id AS mission_id,
  m.title AS mission_title,
  m.status AS mission_status,
  m.commit_hash AS mission_commit_hash,
  m.branch_name AS mission_branch,
  e.id AS merge_entry_id,
  e.branch_name AS merge_branch,
  e.target_branch,
  e.completed_utc,
  v.id AS vessel_id,
  v.name AS vessel_name,
  v.local_path
FROM missions m
JOIN merge_entries e ON e.mission_id = m.id
JOIN vessels v ON v.id = COALESCE(e.vessel_id, m.vessel_id)
WHERE m.status = 'Complete'
  AND e.status = 'Landed'
  AND COALESCE(m.commit_hash, '') <> ''
ORDER BY e.completed_utc DESC;
"@

if ($SqlOnly) {
  $query
  return
}

$sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
if (-not $sqlite) {
  Write-Host "sqlite3 was not found. Run with -SqlOnly to print the candidate query, or install sqlite3 to perform git HEAD comparison."
  return
}

$rows = & $sqlite.Source -header -csv $DatabasePath $query | ConvertFrom-Csv
foreach ($row in $rows) {
  if ([string]::IsNullOrWhiteSpace($row.local_path) -or -not (Test-Path $row.local_path)) {
    continue
  }

  $targetBranch = if ([string]::IsNullOrWhiteSpace($row.target_branch)) { "main" } else { $row.target_branch }
  $targetHead = (& git -C $row.local_path rev-parse --verify $targetBranch 2>$null).Trim()
  if ([string]::IsNullOrWhiteSpace($targetHead)) {
    $targetHead = (& git -C $row.local_path rev-parse --verify "refs/remotes/origin/$targetBranch" 2>$null).Trim()
  }

  if ([string]::IsNullOrWhiteSpace($targetHead)) {
    continue
  }

  if ($row.mission_commit_hash -ieq $targetHead) {
    [pscustomobject]@{
      MissionId = $row.mission_id
      MergeEntryId = $row.merge_entry_id
      VesselId = $row.vessel_id
      Branch = if ([string]::IsNullOrWhiteSpace($row.merge_branch)) { $row.mission_branch } else { $row.merge_branch }
      TargetBranch = $targetBranch
      Head = $targetHead
      CompletedUtc = $row.completed_utc
      Title = $row.mission_title
    }
  }
}
