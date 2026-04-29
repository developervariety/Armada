param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot
)

$resolvedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

$targets = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object {
        $_.CommandLine -like '*Armada.Helm.dll* mcp stdio*' -and
        $_.CommandLine -like ('*' + $resolvedRepoRoot + '*')
    }

if (-not $targets) {
    Write-Host '[update] No repo-backed MCP stdio hosts found.'
    exit 0
}

foreach ($target in $targets) {
    Write-Host ('[update] Stopping MCP stdio host PID ' + $target.ProcessId + '...')
    Stop-Process -Id $target.ProcessId -Force
}
