# Observation cost probe: how fast does the display buffer grow, and how much
# does each step cost in context and on disk?
#
# Plays a fixed title -> new game -> main interface path and records, per step:
# display-buffer line count, lines omitted from the returned window, returned
# text size, the JSON size the caller actually receives, and the session audit
# file size. Writes only into the isolated runtime and a fresh artifact
# directory; the original game folder is never touched.
#
# The button values below encode this project's development game; adapt them
# before using it against another Era game.
[CmdletBinding()]
param(
    [string]$GameRoot,
    [string]$EngineExecutable,
    [string]$RuntimeDirectory,
    [string]$ConfigPath,
    [int]$MainScreenSteps = 4
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)

$build = & (Join-Path $PSScriptRoot '..\scripts\Build-Bridge.ps1') -GameRoot $GameRoot `
    -EngineExecutable $EngineExecutable -RuntimeDirectory $RuntimeDirectory -ConfigPath $ConfigPath | ConvertFrom-Json
$sessionFile = $build.sessionFile
$artifact = $build.artifactDirectory
$auditPath = Join-Path $artifact 'host-audit.jsonl'
$client = Join-Path $PSScriptRoot '..\scripts\Invoke-Bridge.ps1'
$rows = New-Object System.Collections.Generic.List[object]
$script:step = 0
$script:cur = $null

function AuditBytes { if (Test-Path $auditPath) { (Get-Item $auditPath).Length } else { 0 } }

function Call([hashtable]$p) {
    $raw = & $client @p
    return [pscustomobject]@{
        obj   = ($raw | ConvertFrom-Json)
        bytes = [Text.Encoding]::UTF8.GetByteCount(($raw -join "`n"))
    }
}

function Record([string]$label, $o, [int]$bytes) {
    $script:step++
    $rows.Add([pscustomobject]@{
        step            = $script:step
        label           = $label
        state           = $o.state
        inputKind       = $o.input.kind
        totalLines      = $o.totalLines
        omittedBefore   = $o.omittedBefore
        returnedLines   = $o.returnedLines
        rawTextChars    = $o.rawTextLength
        returnedChars   = if ($o.text) { $o.text.Length } else { 0 }
        activeButtons   = @($o.buttons | Where-Object { $_.active }).Count
        responseBytes   = $bytes
        auditBytesAfter = (AuditBytes)
    })
}

function Act([string]$label, [string]$kind, [string]$value, [string]$choiceId) {
    $p = @{
        SessionFile   = $sessionFile
        Action        = 'act'
        ObservationId = $script:cur.observationId
        ActionId      = [Guid]::NewGuid().ToString()
        Kind          = $kind
        Mode          = 'text'
    }
    if ($value) { $p.Value = $value }
    if ($choiceId) { $p.ChoiceId = $choiceId }
    $r = Call $p
    if (-not $r.obj.ok) { Write-Warning ('refused ' + $r.obj.code + ' at ' + $label); return $false }
    $script:cur = $r.obj.observation
    Record $label $script:cur $r.bytes
    return $true
}

function PickValue([string]$value) {
    $c = @($script:cur.buttons | Where-Object { $_.active -and $_.value -eq $value })
    if ($c.Count -eq 0) { return $null }
    return $c[0]
}

$open = Call @{ SessionFile = $sessionFile; Action = 'open'; Mode = 'text' }
$script:cur = $open.obj
Record 'open' $script:cur $open.bytes

try {
    $b = PickValue '1';     if ($b) { Act 'title:new-game' 'choose' '' $b.id | Out-Null }
    $b = PickValue '100';   if ($b) { Act 'setup:default' 'choose' '' $b.id | Out-Null }
    for ($n = 0; $n -lt 8; $n++) {
        if ($script:cur.input.kind -ne 'EnterKey') { break }
        Act ('prologue:' + $n) 'continue' '' '' | Out-Null
    }
    if ($script:cur.input.kind -eq 'IntValue') {
        $b = PickValue '100'; if ($b) { Act 'value-prompt' 'choose' '' $b.id | Out-Null }
    }
    if (@($script:cur.buttons | Where-Object { $_.active }).Count -eq 2) {
        $b = PickValue '2'; if ($b) { Act 'confrontation' 'choose' '' $b.id | Out-Null }
    }
    for ($n = 0; $n -lt 12; $n++) {
        if ($script:cur.input.kind -ne 'EnterKey') { break }
        Act ('narration:' + $n) 'continue' '' '' | Out-Null
    }

    $menu = @($script:cur.buttons | Where-Object { $_.active })
    Write-Output ('main-interface buttons=' + $menu.Count)

    # Round trips through full-screen pages: if the buffer were per-screen, the
    # line count would drop back after returning; if it is per-session, it climbs.
    $probe = @($menu | Where-Object { $_.value -in @('101', '504', '199') })
    for ($i = 0; $i -lt [Math]::Min($MainScreenSteps, $probe.Count); $i++) {
        $entry = $probe[$i]
        $value = $entry.value
        $mark = @($script:cur.buttons | Where-Object { $_.active -and $_.value -eq $value })
        if ($mark.Count -eq 0) { continue }
        if (-not (Act ('page:' + $value) 'choose' '' $mark[0].id)) { continue }
        for ($k = 0; $k -lt 3; $k++) {
            if ($script:cur.input.kind -ne 'EnterKey') { break }
            Act ('page:' + $value + ':enter' + $k) 'continue' '' '' | Out-Null
        }
        $back = @($script:cur.buttons | Where-Object { $_.active -and $_.value -eq '999' })
        if ($back.Count -gt 0) {
            Act ('page:' + $value + ':back') 'choose' '' $back[0].id | Out-Null
        }
    }
} finally {
    $close = Call @{ SessionFile = $sessionFile; Action = 'close' }
    Write-Output ('close=' + $close.obj.status)
}

$rows | Format-Table -AutoSize | Out-String -Width 220
$steps = @($rows | Where-Object { $_.step -gt 1 })
$delta = $rows[-1].auditBytesAfter - $rows[0].auditBytesAfter
[pscustomobject]@{
    steps          = $rows.Count
    bufferFrom     = $rows[0].totalLines
    bufferTo       = $rows[-1].totalLines
    avgResponseKB  = [math]::Round((($steps | Measure-Object responseBytes -Average).Average) / 1024, 1)
    maxResponseKB  = [math]::Round((($steps | Measure-Object responseBytes -Maximum).Maximum) / 1024, 1)
    auditPerStepKB = [math]::Round(($delta / 1024) / [Math]::Max(1, $steps.Count), 1)
    auditTotalKB   = [math]::Round((AuditBytes) / 1024, 1)
    projected100   = [math]::Round((($steps | Measure-Object responseBytes -Average).Average) * 100 / 1048576, 1)
    artifact       = $artifact
} | ConvertTo-Json
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifact 'observation-cost.json') -Encoding UTF8
