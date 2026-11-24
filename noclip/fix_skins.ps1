param([string]$From = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$man = Join-Path $root 'assets\robrun_assets.txt'
if (-not (Test-Path $man)) { Write-Host '[-] РЅРµС‚ assets\robrun_assets.txt'; exit 1 }
$rows = Get-Content $man | Where-Object { $_ -match '\|' }
$miss = @()
foreach ($r in $rows) {
    $p = $r.Split('|')
    $dst = Join-Path $root ('assets\' + $p[0])
    if (-not (Test-Path $dst)) { $miss += ,@($p[0], [int]$p[2], $p[3]); continue }
    if ((Get-Item $dst).Length -ne [int]$p[2]) { $miss += ,@($p[0], [int]$p[2], $p[3]) }
}
Write-Host ('[*] РЅРµ С…РІР°С‚Р°РµС‚ РїР°РєРµС‚РѕРІ: ' + $miss.Count)
$miss | ForEach-Object { Write-Host ('    ' + $_[0] + '  ' + [math]::Round($_[1]/1MB,1) + ' РњР‘') }
if (-not $From) { Write-Host '[i] Р·Р°РїСѓСЃС‚Рё СЃ -From "РїСѓС‚СЊ\Рє\assets" С‡С‚РѕР±С‹ Р·Р°Р»РёС‚СЊ'; exit 0 }
$ok = 0; $bad = 0
foreach ($m in $miss) {
    $src = Join-Path $From $m[0]
    if (-not (Test-Path $src)) { Write-Host ('[-] РЅРµС‚: ' + $m[0]); $bad++; continue }
    if ((Get-FileHash $src -Algorithm SHA256).Hash.ToLower() -ne $m[2]) { Write-Host ('[!] С…РµС€ РЅРµ СЃРѕРІРїР°Р»: ' + $m[0]); $bad++; continue }
    Copy-Item $src (Join-Path $root ('assets\' + $m[0])) -Force
    Write-Host ('[+] ' + $m[0]); $ok++
}
Write-Host ('[*] СЃРєРѕРїРёСЂРѕРІР°РЅРѕ: ' + $ok + ', РїСЂРѕР±Р»РµРј: ' + $bad)