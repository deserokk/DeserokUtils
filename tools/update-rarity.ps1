# Regenerates Features/Fanfare/Data/rarity.txt from the FFXIV Collect API.
# Run it when cutting a release, the plugin ships the snapshot and never calls out itself.
#
# Data from FFXIV Collect (https://ffxivcollect.com), by Raelys / skyborn-industries, MIT.
# https://github.com/skyborn-industries/ffxiv-collect
#
# Usage: pwsh tools/update-rarity.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root 'DeserokUtils\Features\Fanfare\Data\rarity.txt'
$endpoint = 'https://ffxivcollect.com/api/achievements?limit=5000'

Write-Host "Fetching $endpoint ..."
$response = Invoke-WebRequest -Uri $endpoint -UseBasicParsing
$payload = $response.Content | ConvertFrom-Json

if (-not $payload.results -or $payload.results.Count -eq 0) {
    throw "No results returned. Refusing to overwrite the existing snapshot with nothing."
}

$rows = New-Object System.Collections.Generic.List[string]
$skipped = 0

foreach ($achievement in $payload.results) {
    if ([string]::IsNullOrWhiteSpace($achievement.owned)) { $skipped++; continue }

    $percent = 0.0
    if (-not [double]::TryParse(($achievement.owned -replace '%', ''),
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture, [ref] $percent)) {
        $skipped++
        continue
    }

    $rows.Add(("{0}:{1}" -f $achievement.id, $percent.ToString([Globalization.CultureInfo]::InvariantCulture)))
}

# A truncated fetch would ship as "most achievements are unknown", which makes everything
# sound common. Looks like a tuning problem rather than a broken download, so check the count.
if ($rows.Count -lt 3000) {
    throw "Only $($rows.Count) rows parsed, expected ~3900. Refusing to write a partial snapshot."
}

# Sorted by id so a regenerated file diffs as moved percentages, not a whole rewrite.
$sorted = $rows | Sort-Object { [int]($_ -split ':')[0] }

$header = @(
    "# Achievement rarity: percentage of tracked FFXIV Collect users who own each achievement.",
    "# Source: https://ffxivcollect.com (Raelys / skyborn-industries, MIT). Regenerate: tools/update-rarity.ps1",
    "# Not a percentage of all FFXIV players, the population is self-selected collectors.",
    "# Generated: $((Get-Date).ToString('yyyy-MM-dd'))",
    "# Format: <achievementId>:<percent>"
)

Set-Content -Path $outPath -Value ($header + $sorted) -Encoding utf8

Write-Host "Wrote $($rows.Count) rows to $outPath (skipped $skipped)."
