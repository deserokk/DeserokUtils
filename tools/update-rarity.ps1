# Regenerates Features/Fanfare/Data/rarity.txt from the FFXIV Collect API.
# Run it when cutting a release, the plugin ships the snapshot and never calls out itself.
#
# Data from FFXIV Collect (https://ffxivcollect.com), by Raelys / skyborn-industries, MIT.
# https://github.com/skyborn-industries/ffxiv-collect
#
# Usage: pwsh tools/update-rarity.ps1 [-PatchDate 2026-09-08] [-NewPatch 7.56]
#
# Achievements from a just-released patch all sit at 0% because nobody has them yet, and 0% is
# below the rare threshold, so without -PatchDate every one of them would fire the rare chime.
# Pass the patch's release date and they get flagged as untracked for a week. After that week
# the number is believed: the people this data comes from are achievement hunters, so if they
# still do not have it seven days in, it is genuinely rare.

param(
    [string] $PatchDate = '',
    [string] $NewPatch  = ''
)

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

# Work out which patch counts as new. Explicit wins; otherwise the highest patch present.
# Patch strings are not versions: 3.55a exists, so [version] throws on the real data.
function Get-PatchRank([string] $p) {
    $m = [regex]::Match($p, '^(\d+)\.(\d+)([a-z]?)')
    if (-not $m.Success) { return -1 }
    $letter = 0
    if ($m.Groups[3].Value) { $letter = [int][char]$m.Groups[3].Value[0] - 96 }
    return ([int]$m.Groups[1].Value * 100000) + ([int]$m.Groups[2].Value * 100) + $letter
}

if ([string]::IsNullOrWhiteSpace($NewPatch)) {
    $seen = @{}
    foreach ($a in $payload.results) {
        if ($a.patch) { $seen[[string]$a.patch] = $true }
    }
    $NewPatch = ($seen.Keys | Sort-Object { Get-PatchRank $_ } | Select-Object -Last 1)
}

$newUntil = ''
if (-not [string]::IsNullOrWhiteSpace($PatchDate)) {
    $newUntil = ([datetime]::ParseExact($PatchDate, 'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture)).AddDays(7).ToString('yyyy-MM-dd')
}

$rows = New-Object System.Collections.Generic.List[string]
$skipped = 0
$flagged = 0

foreach ($achievement in $payload.results) {
    if ([string]::IsNullOrWhiteSpace($achievement.owned)) { $skipped++; continue }

    $percent = 0.0
    if (-not [double]::TryParse(($achievement.owned -replace '%', ''),
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture, [ref] $percent)) {
        $skipped++
        continue
    }

    $line = "{0}:{1}" -f $achievement.id, $percent.ToString([Globalization.CultureInfo]::InvariantCulture)

    # Only flag when we were told the release date. Flagging without one would mark rows as
    # untracked with no window to expire, which never clears.
    if ($newUntil -ne '' -and "$($achievement.patch)" -eq $NewPatch) {
        $line = $line + ':new'
        $flagged++
    }

    $rows.Add($line)
}

# A truncated fetch would ship as "most achievements are unknown", which makes everything
# sound common. Looks like a tuning problem rather than a broken download, so check the count.
if ($rows.Count -lt 3000) {
    throw "Only $($rows.Count) rows parsed, expected ~3900. Refusing to write a partial snapshot."
}

# Sorted by id so a regenerated file diffs as moved percentages, not a whole rewrite.
$sorted = $rows | Sort-Object { [int]($_ -split ':')[0] }

$header = New-Object System.Collections.Generic.List[string]
$header.Add("# Achievement rarity: percentage of tracked FFXIV Collect users who own each achievement.")
$header.Add("# Source: https://ffxivcollect.com (Raelys / skyborn-industries, MIT). Regenerate: tools/update-rarity.ps1")
$header.Add("# Not a percentage of all FFXIV players, the population is self-selected collectors.")
$header.Add("# Generated: $((Get-Date).ToString('yyyy-MM-dd'))")
if ($newUntil -ne '') {
    $header.Add("# NewUntil: $newUntil (patch $NewPatch, rows marked :new have no usable data until then)")
}
$header.Add("# Format: <achievementId>:<percent>[:new]")

Set-Content -Path $outPath -Value ($header + $sorted) -Encoding utf8

Write-Host "Wrote $($rows.Count) rows to $outPath (skipped $skipped, flagged $flagged as patch $NewPatch)."
if ($newUntil -ne '') { Write-Host "Flagged rows report no rarity until $newUntil." }
