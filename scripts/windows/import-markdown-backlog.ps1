param(
    [string]$BaseUrl = "http://localhost:7890",
    [string]$BearerToken = "default",
    [switch]$Commit
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$rootPath = $root.Path.TrimEnd("\")
$headers = @{
    Authorization = "Bearer $BearerToken"
    "Content-Type" = "application/json"
}

function Normalize-Text([string]$value) {
    return ($value -replace "\s+", " ").Trim()
}

function Trim-Marker([string]$line) {
    $text = $line -replace "^\s*[-*+]\s+\[[ xX~!]\]\s*", ""
    return Normalize-Text $text
}

function Get-DocTitle([string[]]$lines, [string]$fallback) {
    foreach ($line in $lines) {
        if ($line -match "^\s*#\s+(.+?)\s*$") {
            return Normalize-Text $Matches[1]
        }
    }

    return [IO.Path]::GetFileNameWithoutExtension($fallback)
}

function Get-MeaningfulHeading($stack, [string]$docTitle) {
    $ignored = @(
        "Checklist",
        "Tests",
        "Documentation",
        "File Map",
        "Verification",
        "Automated Verification",
        "Manual Smoke Checklist",
        "Definition Of Done",
        "Progress Log",
        "Files Modified Tracker",
        "Implementation Notes"
    )

    for ($i = $stack.Count - 1; $i -ge 0; $i--) {
        $heading = Normalize-Text $stack[$i].Text
        if ($ignored -notcontains $heading) {
            return $heading
        }
    }

    return $docTitle
}

function Get-MeaningfulHeadingPath($stack, [string]$docTitle) {
    $ignored = @(
        "Checklist",
        "Tests",
        "Documentation",
        "File Map",
        "Verification",
        "Automated Verification",
        "Manual Smoke Checklist",
        "Definition Of Done",
        "Progress Log",
        "Files Modified Tracker",
        "Implementation Notes"
    )

    $parts = @()
    foreach ($heading in $stack) {
        $text = Normalize-Text $heading.Text
        if ($ignored -notcontains $text) {
            $parts += $text
        }
    }

    if ($parts.Count -eq 0) {
        return $docTitle
    }

    $leaf = $parts[$parts.Count - 1]
    $genericLeaf = @("Capability Target", "Concrete Work", "API Surface Impact", "WebSocket candidates", "Database and Persistence Considerations", "Required test layers", "Tasks", "Exit Criteria")
    if ($genericLeaf -contains $leaf -and $parts.Count -gt 1) {
        $start = [Math]::Max(0, $parts.Count - 3)
        return ($parts[$start..($parts.Count - 1)] -join " / ")
    }

    return $leaf
}

function Get-Category([string]$relativePath) {
    $lower = $relativePath.ToLowerInvariant()
    if ($lower -match "i18n|dashboard|frontend|desktop") { return "Frontend" }
    if ($lower -match "remote|proxy|tunnel") { return "Remote Operations" }
    if ($lower -match "failure|dispatch|merge|lifecycle|stabil") { return "Reliability" }
    if ($lower -match "multi|auth|tenant|security") { return "Security" }
    if ($lower -match "mux|runtime|captain") { return "Runtime" }
    if ($lower -match "api|request|postman|mcp") { return "API" }
    if ($lower -match "docs|readme|getting_started|changelog") { return "Documentation" }
    return "Imported Planning"
}

function Get-Kind([string]$relativePath, [string]$title) {
    $text = ($relativePath + " " + $title).ToLowerInvariant()
    if ($text -match "bug|failure|fix|regression|race|stabil") { return "Bug" }
    if ($text -match "test|doc|manual|smoke|audit|cleanup") { return "Chore" }
    if ($text -match "research|decide|define|confirm|investigat") { return "Research" }
    if ($text -match "refactor|replace|restructure") { return "Refactor" }
    if ($text -match "initiative|phase|workstream|tier") { return "Initiative" }
    return "Feature"
}

function Get-Priority([string]$relativePath, [string]$title) {
    $text = ($relativePath + " " + $title).ToLowerInvariant()
    if ($text -match "p0|critical|stop the bleeding|t1-|failure_cases|dispatch_issues") { return "P1" }
    if ($text -match "manual smoke|docs|documentation|future|later|deferred") { return "P3" }
    return "P2"
}

function Get-Effort([int]$count) {
    if ($count -le 1) { return "XS" }
    if ($count -le 3) { return "S" }
    if ($count -le 8) { return "M" }
    if ($count -le 20) { return "L" }
    return "XL"
}

function Get-Title([string]$relativePath, [string]$docTitle, [string]$heading) {
    $prefix = [IO.Path]::GetFileNameWithoutExtension($relativePath)
    $prefix = ($prefix -replace "[_-]+", " ").Trim()
    if ($heading -eq $docTitle) {
        $title = $docTitle
    }
    else {
        $title = "$prefix`: $heading"
    }

    if ($title.Length -gt 180) {
        return $title.Substring(0, 177).TrimEnd() + "..."
    }

    return $title
}

$markdownFiles = Get-ChildItem -Path $root -Recurse -File -Include *.md,*.txt |
    Where-Object {
        $_.FullName -notmatch "\\.git\\" -and
        $_.FullName -notmatch "\\bin\\" -and
        $_.FullName -notmatch "\\obj\\" -and
        $_.FullName -notmatch "\\dist\\" -and
        $_.FullName -notmatch "\\node_modules\\"
    }

$groups = [ordered]@{}

foreach ($file in $markdownFiles) {
    $lines = [IO.File]::ReadAllLines($file.FullName)
    $relativePath = $file.FullName.Substring($rootPath.Length).TrimStart("\").Replace("\", "/")
    $docTitle = Get-DocTitle $lines $file.Name
    $headingStack = New-Object System.Collections.ArrayList

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match "^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$") {
            $level = $Matches[1].Length
            while ($headingStack.Count -gt 0 -and $headingStack[$headingStack.Count - 1].Level -ge $level) {
                $headingStack.RemoveAt($headingStack.Count - 1)
            }

            [void]$headingStack.Add([pscustomobject]@{
                Level = $level
                Text = Normalize-Text $Matches[2]
                Line = $i + 1
            })
            continue
        }

        if ($line -notmatch "^\s*[-*+]\s+\[[ ~!]\]\s+(.+?)\s*$") {
            continue
        }

        $task = Trim-Marker $line
        if ([string]::IsNullOrWhiteSpace($task)) {
            continue
        }

        $heading = Get-MeaningfulHeading $headingStack $docTitle
        $headingPath = Get-MeaningfulHeadingPath $headingStack $docTitle
        $headingLine = 1
        for ($h = $headingStack.Count - 1; $h -ge 0; $h--) {
            if ((Normalize-Text $headingStack[$h].Text) -eq $heading) {
                $headingLine = $headingStack[$h].Line
                break
            }
        }

        $key = "$relativePath`:$headingLine"
        if (-not $groups.Contains($key)) {
            $groups[$key] = [pscustomobject]@{
                Key = $key
                RelativePath = $relativePath
                DocTitle = $docTitle
                Heading = $heading
                HeadingPath = $headingPath
                HeadingLine = $headingLine
                Tasks = New-Object System.Collections.ArrayList
                Lines = New-Object System.Collections.ArrayList
            }
        }

        [void]$groups[$key].Tasks.Add($task)
        [void]$groups[$key].Lines.Add($i + 1)
    }
}

$items = @()
foreach ($group in $groups.Values) {
    $title = Get-Title $group.RelativePath $group.DocTitle $group.HeadingPath
    $lineList = ($group.Lines | Select-Object -First 12) -join ", "
    if ($group.Lines.Count -gt 12) {
        $lineList += ", ..."
    }

    $sourceKey = "markdown-import:$($group.Key)"
    $description = @"
Imported from markdown planning material.

Source: $($group.RelativePath):$($group.HeadingLine)
Section: $($group.HeadingPath)
Open checklist lines: $lineList

Use this backlog item as the Armada system-of-record replacement for this legacy TODO/roadmap/status/spec section. Keep implementation evidence attached here instead of adding new checklist rows to the source document.
"@.Trim()

    $items += [pscustomobject]@{
        Title = $title
        Description = $description
        Status = "Scoped"
        Kind = Get-Kind $group.RelativePath $title
        Category = Get-Category $group.RelativePath
        Priority = Get-Priority $group.RelativePath $title
        Rank = 1000 + $items.Count
        BacklogState = "Inbox"
        Effort = Get-Effort $group.Tasks.Count
        Tags = @("markdown-import", "legacy-planning")
        AcceptanceCriteria = @($group.Tasks)
        EvidenceLinks = @($sourceKey, "source:$($group.RelativePath):$($group.HeadingLine)")
    }
}

Write-Host "Discovered $($items.Count) candidate backlog items from markdown checklists."

$existing = @()
try {
    $existingResult = Invoke-RestMethod -Uri "$BaseUrl/api/v1/backlog?pageNumber=1&pageSize=500" -Headers $headers -Method Get
    $existing = @($existingResult.Objects)
}
catch {
    throw "Unable to read existing backlog from $BaseUrl. $($_.Exception.Message)"
}

$existingEvidence = New-Object "System.Collections.Generic.HashSet[string]"
foreach ($objective in $existing) {
    foreach ($link in @($objective.EvidenceLinks)) {
        if ($link -like "markdown-import:*") {
            [void]$existingEvidence.Add($link)
        }
    }
}

$pending = @($items | Where-Object { -not $existingEvidence.Contains($_.EvidenceLinks[0]) })
Write-Host "Existing imported sections: $($existingEvidence.Count)"
Write-Host "Pending imports: $($pending.Count)"

if (-not $Commit) {
    $pending |
        Select-Object Title, Kind, Category, Priority, Effort, @{Name="Criteria"; Expression = { $_.AcceptanceCriteria.Count }}, @{Name="Source"; Expression = { $_.EvidenceLinks[1] }} |
        Format-Table -AutoSize
    Write-Host "Dry run only. Re-run with -Commit to create pending backlog items."
    exit 0
}

$created = @()
foreach ($item in $pending) {
    $json = $item | ConvertTo-Json -Depth 12
    $createdItem = Invoke-RestMethod -Uri "$BaseUrl/api/v1/backlog" -Headers $headers -Method Post -Body $json
    $created += $createdItem
    Write-Host "Created $($createdItem.Id): $($createdItem.Title)"
}

Write-Host "Created $($created.Count) backlog items."
