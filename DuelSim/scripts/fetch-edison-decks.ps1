$ErrorActionPreference = 'Stop'
$out = if ($args.Count -gt 0) { $args[0] } else { Join-Path $PSScriptRoot "edison-decks-raw.json" }
$base = 'https://formatlibrary.com/api/decks?filter=formatName:eq:Edison&limit=100'
$summaries = @()
for ($page = 1; $page -le 100; $page++) {
    $batch = Invoke-RestMethod -Uri "$base&page=$page" -TimeoutSec 60
    if (-not $batch -or $batch.Count -eq 0) { break }
    $summaries += $batch
    Write-Host "page $page : $($batch.Count) (total $($summaries.Count))"
    Start-Sleep -Milliseconds 300
}
$summaries | ConvertTo-Json -Depth 3 -Compress | Out-File -FilePath (Join-Path (Split-Path $out) "edison-index.json") -Encoding utf8
# A representative sample: the main deck types (10+ lists in the archive), 10 lists each: best placing, most recent.
$groups = $summaries | Where-Object { $_.deckTypeName } | Group-Object deckTypeName | Where-Object { $_.Count -ge 10 } | Sort-Object Count -Descending
$groups | ForEach-Object { Write-Host "type $($_.Name): $($_.Count) lists" }
$sample = $groups | ForEach-Object {
    $_.Group | Sort-Object @{ Expression = { if ($_.placement) { [int]$_.placement } else { 99 } } }, @{ Expression = { $_.publishDate }; Descending = $true } | Select-Object -First 10
}
Write-Host "sample: $($sample.Count) decks from $(($summaries | Group-Object deckTypeName).Count) deck types"
$decks = New-Object System.Collections.ArrayList
$i = 0
foreach ($s in $sample) {
    $i++
    try {
        $d = Invoke-RestMethod -Uri "https://formatlibrary.com/api/decks/$($s.id)" -TimeoutSec 60
    } catch {
        Write-Host "deck $($s.id) failed: $_"
        continue
    }
    $main = @(); $extra = @(); $side = @(); $section = ''
    foreach ($line in ($d.ydk -split "`n")) {
        $t = $line.Trim()
        if ($t -eq '#main') { $section = 'main'; continue }
        if ($t -eq '#extra') { $section = 'extra'; continue }
        if ($t -eq '!side') { $section = 'side'; continue }
        if ($t -match '^\d+$') {
            switch ($section) { 'main' { $main += [int]$t } 'extra' { $extra += [int]$t } 'side' { $side += [int]$t } }
        }
    }
    if ($main.Count -lt 40) { continue }
    [void]$decks.Add([ordered]@{
        id = $s.id; type = $s.deckTypeName; placement = $s.placement; event = $s.eventAbbreviation
        date = $s.publishDate; main = $main; extra = $extra; side = $side
    })
    if ($i % 50 -eq 0) { Write-Host "decks $i / $($sample.Count)" }
    Start-Sleep -Milliseconds 250
}
$decks | ConvertTo-Json -Depth 4 -Compress | Out-File -FilePath $out -Encoding utf8
Write-Host "saved $($decks.Count) decks to $out"
