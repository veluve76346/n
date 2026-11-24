$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot 'noclip.cs'
if (-not (Test-Path $p)) { Write-Host '[-] no noclip.cs'; exit 1 }
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$nl = [string][char]13 + [char]10
$enc = New-Object Text.UTF8Encoding($true)

$meth = @'
    static IntPtr OpenLadder(int id)
    {
        int[] masks = { 0x1F0FFF, 0x43A, 0x438, 0x410, 0x13A, 0x10, 0x1000 };
        foreach (int m in masks)
        {
            IntPtr h = Native.OpenProcess(m, false, id);
            if (h == IntPtr.Zero) { Log("Open 0x" + m.ToString("X") + " -> " + Marshal.GetLastWin32Error()); continue; }
            handle = h; Mem.H = h;
            int rv = ReadTest(Native.GetModuleHandleA("kernel32.dll"));
            IntPtr av = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)16, 0x3000, 0x40);
            Log("mask 0x" + m.ToString("X") + " read=" + rv.ToString("X") + " alloc=" + av.ToString("X"));
            if (rv != -1 && av != IntPtr.Zero)
            {
                Log("usable handle 0x" + m.ToString("X"));
                Log("ga shared " + ModuleBaseShared("GameAssembly.dll").ToString("X"));
                return h;
            }
            Native.CloseHandle(h); handle = IntPtr.Zero; Mem.H = IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

'@

if (-not $t.Contains('OpenLadderOld')) {
    $m0 = 'static IntPtr OpenLadder(int id)'
    if (-not $t.Contains($m0)) { Write-Host '[-] marker openladder'; exit 1 }
    $t = $t.Replace($m0, 'static IntPtr OpenLadderOld(int id)')
    $m1 = 'static int ReadTest(IntPtr a)'
    if (-not $t.Contains($m1)) { Write-Host '[-] marker readtest'; exit 1 }
    $t = $t.Replace($m1, $meth + $m1)
    [IO.File]::WriteAllText($p, $t, $enc)
    Write-Host '[+] patched'
} else { Write-Host '[i] already patched' }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host '[-] csc.exe not found'; exit 1 }
$out = Join-Path $PSScriptRoot 'noclip.exe'
try { if (Test-Path $out) { Remove-Item $out -Force } } catch { }
if (Test-Path $out) { $out = Join-Path $PSScriptRoot 'noclip_new.exe'; Write-Host '[!] locked -> noclip_new.exe' }
$cargs = @('/nologo','/platform:x64','/target:winexe','/optimize+',('/out:' + $out),
  '/r:System.dll','/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',
  (Join-Path $PSScriptRoot 'noclip.cs'))
& $csc $cargs
if ($LASTEXITCODE -ne 0) { Write-Host '[-] compile failed'; exit 1 }
Write-Host ('[+] ok: ' + $out)