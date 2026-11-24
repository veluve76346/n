#requires -Version 5.1
<#
    Offline launcher (no GUI loader, no internet).

    Flow:
      overlay reads the session token from stdin (session.json)
      overlay takes menu/core packages from the local replay (tools\net.ps1)
      overlay decrypts them, accepts the session and injects core into the game
      overlay patches robrun_menu.dll before it is written: "FOCUS" -> "leak @syncty"

    Requires: the game ROB RUN is already running.

    tools\run.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [int]$Port = 8765,
    [int]$WaitStartSec = 25,
    [switch]$Status,
    [switch]$Stop,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

function Log  ([string]$t) { Write-Host ('[*] ' + $t) -ForegroundColor DarkGray }
function Ok   ([string]$t) { Write-Host ('[+] ' + $t) -ForegroundColor Green }
function Warn ([string]$t) { Write-Host ('[!] ' + $t) -ForegroundColor Yellow }
function Bad  ([string]$t) { Write-Host ('[-] ' + $t) -ForegroundColor Red }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

$dir      = $Root
$overlay  = Join-Path $Root 'overlay.exe'
$session  = Join-Path $Root 'session.json'
$endpoint = Join-Path $Root 'assets\endpoint.url'
$replay   = Join-Path $Root 'tools\net.ps1'
$logFile  = Join-Path $env:LOCALAPPDATA 'Focus\loader.log'

# ---------- stop ----------
if ($Stop) {
    Get-Process -Name 'overlay' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); Ok ('overlay ' + $_.Id + ' stopped') } catch { Warn ('cannot stop ' + $_.Id) }
    }
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'net\.ps1' } |
        ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force; Ok ('replay ' + $_.ProcessId + ' stopped') } catch { }
        }
    exit 0
}

# ---------- files ----------
foreach ($p in @($overlay, $session, $replay)) {
    if (-not (Test-Path -LiteralPath $p)) { Bad ('missing file: ' + $p); exit 1 }
}

$game = Get-Process -Name 'ROB RUN' -ErrorAction SilentlyContinue | Select-Object -First 1

if ($Status) {
    $rep = $false
    try { $r = Invoke-WebRequest ("http://127.0.0.1:{0}/api/v1/features/robrun.menu" -f $Port) -UseBasicParsing -TimeoutSec 3; $rep = ($r.StatusCode -eq 200) } catch { }
    Log ('replay: ' + $(if ($rep) { 'running' } else { 'down' }))
    Log ('overlay: ' + $(if (Get-Process -Name 'overlay' -ErrorAction SilentlyContinue) { 'running' } else { 'down' }))
    if ($game) { Log ('game: PID ' + $game.Id) } else { Warn 'game is not running' }
    if (Test-Path $logFile) { Get-Content $logFile -Tail 6 }
    exit 0
}

if (-not (Test-Admin)) {
    if ($Elevated) { Bad 'administrator rights required'; exit 1 }
    $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Root `"$Root`" -Port $Port -Elevated"
    if ($Status) { $a += ' -Status' }
    if ($Stop) { $a += ' -Stop' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a
    exit 0
}

# ---------- UI assets (portable) ----------
$uiSrc = Join-Path $Root 'assets\ui.png'
$logoSrc = Join-Path $Root 'assets\logo_blue.png'
$uiDst = Join-Path $env:LOCALAPPDATA 'Focus\ui'
if (Test-Path -LiteralPath $uiSrc) {
    if (-not (Test-Path -LiteralPath $uiDst)) { New-Item -ItemType Directory -Path $uiDst -Force | Out-Null }
    Copy-Item -LiteralPath $uiSrc -Destination (Join-Path $uiDst 'ui.png') -Force
    if (Test-Path -LiteralPath $logoSrc) { Copy-Item -LiteralPath $logoSrc -Destination (Join-Path $uiDst 'logo_blue.png') -Force }
}

# ---------- replay ----------
$replayAlive = $false
try {
    $r = Invoke-WebRequest ("http://127.0.0.1:{0}/api/v1/features/robrun.menu" -f $Port) -UseBasicParsing -TimeoutSec 3
    $replayAlive = ($r.StatusCode -eq 200)
} catch { }

if (-not $replayAlive) {
    Log 'starting local replay (HttpListener, no Python)'
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$replay`"", '-Root', "`"$Root`"", '-Port', $Port, '-Cache', 'cache') `
        -WorkingDirectory $Root -WindowStyle Hidden | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        try {
            $r = Invoke-WebRequest ("http://127.0.0.1:{0}/api/v1/features/robrun.menu" -f $Port) -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $replayAlive = $true; break }
        } catch { }
    }
}
if (-not $replayAlive) { Bad 'replay did not start'; exit 1 }
Ok ('replay on 127.0.0.1:' + $Port)

# ---------- endpoint ----------
Set-Content -LiteralPath $endpoint -Value ("http://127.0.0.1:$Port") -NoNewline -Encoding ASCII
Log ('endpoint: ' + (Get-Content -Raw $endpoint))

# ---------- driver ----------
$runDir = $Root
$mapper = Join-Path $runDir 'mapper.exe'
$drv    = Join-Path $runDir 'driver.sys'
$intel  = Join-Path $runDir 'intel.sys'

$driverOk = $false
if ((Test-Path $mapper) -and (Test-Path $drv) -and (Test-Path $intel)) {
    Log 'driver'
    Push-Location $runDir
    try {
        $out = & $mapper '.\intel.sys' '.\driver.sys' 2>&1
        $out | ForEach-Object { Write-Host ('    ' + $_) -ForegroundColor DarkGray }
    } catch {
        Warn ('mapper: ' + $_.Exception.Message)
    } finally { Pop-Location }

    $mapperText = ($out | Out-String)
    $hvci = $mapperText -match 'HVCI=ON|HVCI/VBS must be off'
    if ($hvci) {
        Write-Host ''
        Warn 'HVCI (Memory Integrity) is ON - the driver cannot be mapped'
        Warn 'Fix: Windows Security -> Device security -> Core isolation -> Memory Integrity = Off,'
        Warn '     disable VBS too, reboot, then run the launcher again.'
        Write-Host ''
    }

    $key = Join-Path $env:TEMP 'focus.key'
    if ((Test-Path $key) -and -not $hvci) {
        $driverOk = $true
        Ok ('session key: ' + (Get-Content -Raw $key).Trim())
    } else {
        Warn 'focus.key did not appear - core will not answer the driver'
    }
} else {
    Warn 'mapper.exe / driver.sys / intel.sys missing - skipping driver'
}

if (-not $game) {
    Bad 'game is not running - start ROB RUN.exe and retry'
    exit 1
}
Ok ('game PID ' + $game.Id)

# ---------- overlay ----------
Get-Process -Name 'overlay' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill() } catch { }
}
Start-Sleep -Milliseconds 800

# remove files from the previous run (locked ones cannot be removed - harmless)
$removed = 0
foreach ($pat in 'core_*.dll', 'camera_*.dll') {
    Get-ChildItem -Path $dir -Filter $pat -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($_.FullName)
            $removed++
        } catch { }
    }
}
$menuFile = Join-Path $dir 'robrun_menu.dll'
if (Test-Path -LiteralPath $menuFile) {
    try {
        [IO.File]::SetAttributes($menuFile, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($menuFile)
        $removed++
    } catch { }
}
if ($removed -gt 0) { Log ('cleaned old files: ' + $removed) }

$linesBefore = 0
if (Test-Path $logFile) { $linesBefore = (Get-Content $logFile | Measure-Object -Line).Lines }

$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $overlay
$psi.WorkingDirectory = $dir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.Arguments = ''

$p = [Diagnostics.Process]::Start($psi)
$bytes = [IO.File]::ReadAllBytes($session)
$p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
$p.StandardInput.BaseStream.Flush()
$p.StandardInput.Close()
Log ('overlay PID ' + $p.Id + ', session ' + $bytes.Length + ' bytes')

for ($i = 0; $i -lt $WaitStartSec * 2; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
}

Start-Sleep -Seconds 2

# ---------- result ----------
$new = @()
if (Test-Path $logFile) {
    $all = Get-Content $logFile
    if ($all.Count -gt $linesBefore) { $new = $all[$linesBefore..($all.Count - 1)] }
}
$new | ForEach-Object { Write-Host ('    ' + $_) -ForegroundColor DarkGray }

$accepted = $new | Where-Object { $_ -match 'core accepted' }
$assetMiss = $new | Where-Object { $_ -match 'status=404|asset prefetch failed' }
$cameraMiss = $new | Where-Object { $_ -match 'robrun\.camera' }
$coreLog = Join-Path $env:TEMP 'focus_robrun.log'
$init = $false
if (Test-Path $coreLog) { $init = (Get-Item $coreLog).LastWriteTime -gt $game.StartTime.AddSeconds(-2) }

Write-Host ''
if ($assetMiss -and -not $cameraMiss) {
    Warn 'some asset packs are missing from cache - 3D previews stay off, the menu itself works'
}
if ($cameraMiss) {
    Warn 'camera module (robrun.camera) is not in the build - the camera feature is off'
}

if ($accepted -and $init -and $driverOk) {
    Ok 'offline launch complete: session accepted, core initialized, driver loaded'
    exit 0
}
if ($accepted -and $init -and -not $driverOk) {
    Warn 'PARTIAL: menu is up, but the driver is not loaded - memory read/write features are dead'
    Warn 'Fix HVCI/VBS and reboot (see above), then run the launcher again.'
    exit 3
}
if ($accepted) {
    Warn 'session accepted, but no fresh core log'
    if (-not $driverOk) { Warn 'driver is also not loaded' }
    exit 2
}
Bad 'offline launch not confirmed'
exit 1
