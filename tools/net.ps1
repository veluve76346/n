#requires -Version 5.1
<#
    Local FCS2 replay on the built-in HttpListener.
    No Python, no external services.

    tools\net.ps1 -Port 8765 -Cache cache
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [int]$Port = 8765,
    [string]$Cache = 'cache',
    [string]$PidFile,
    [int]$DelayMs = 0
)

$ErrorActionPreference = 'Stop'

$cacheDir = Join-Path $Root $Cache
$files = @{
    'robrun.menu'   = Join-Path $cacheDir 'menu.bin'
    'robrun.core'   = Join-Path $cacheDir 'core.bin'
    'robrun.camera' = Join-Path $cacheDir 'camera.bin'
    'robrun.sky'    = Join-Path $cacheDir 'sky.bin'
}
$headersFile = Join-Path $cacheDir 'headers.json'

if (-not (Test-Path -LiteralPath $headersFile)) { throw ('missing file: ' + $headersFile) }
foreach ($k in $files.Keys) {
    if (-not (Test-Path -LiteralPath $files[$k])) { throw ('missing file: ' + $files[$k]) }
}

$headers = Get-Content -LiteralPath $headersFile -Raw | ConvertFrom-Json

if ($PidFile) { Set-Content -LiteralPath $PidFile -Value $PID -Encoding ASCII }

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

try {
    while ($listener.IsListening) {
        $ctx = $null
        try { $ctx = $listener.GetContext() } catch { break }
        if (-not $ctx) { continue }

        try {
            $path = $ctx.Request.Url.AbsolutePath
            $name = $null
            foreach ($k in $files.Keys) {
                if ($path -eq "/api/v1/features/$k") { $name = $k; break }
            }

            if (-not $name) {
                $ctx.Response.StatusCode = 404
                $ctx.Response.Close()
                continue
            }

            $data = [IO.File]::ReadAllBytes($files[$name])
            $h = $headers.$name

            if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
            $ctx.Response.StatusCode = 200
            $ctx.Response.ContentType = 'application/octet-stream'
            $ctx.Response.Headers['Cache-Control'] = 'no-store'
            $ctx.Response.Headers['X-Focus-Kdf'] = 'hkdf'
            $ctx.Response.Headers['X-Focus-Feature-Id'] = $name
            foreach ($hn in 'X-Focus-Data-Id', 'X-Focus-Server-Key', 'X-Focus-Signature', 'Content-Disposition') {
                if ($h -and $h.$hn) { $ctx.Response.Headers[$hn] = [string]$h.$hn }
            }
            $ctx.Response.ContentLength64 = $data.Length
            $ctx.Response.OutputStream.Write($data, 0, $data.Length)
        } catch {
            try { $ctx.Response.StatusCode = 500 } catch { }
        } finally {
            try { $ctx.Response.Close() } catch { }
        }
    }
} finally {
    try { $listener.Stop() } catch { }
    try { $listener.Close() } catch { }
}
