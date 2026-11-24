$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot 'noclip.cs'
if (-not (Test-Path $p)) { Write-Host '[-] no noclip.cs'; exit 1 }
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$nl = [string][char]13 + [char]10
$enc = New-Object Text.UTF8Encoding($true)

$meth = @'
    static IntPtr OpenLadder(int id)
    {
        int[] masks = { 0x1F0FFF, 0x43A, 0x410, 0x13A, 0x10 };
        foreach (int m in masks)
        {
            IntPtr h = Native.OpenProcess(m, false, id);
            if (h != IntPtr.Zero)
            {
                handle = h; Mem.H = h;
                Log("OpenProcess ok, mask 0x" + m.ToString("X"));
                Diag();
                return h;
            }
            Log("OpenProcess 0x" + m.ToString("X") + " -> " + Marshal.GetLastWin32Error());
        }
        return IntPtr.Zero;
    }

    static int ReadTest(IntPtr a)
    {
        byte[] b = new byte[4]; IntPtr got;
        if (!Native.ReadProcessMemory(handle, a, b, (IntPtr)4, out got))
        {
            Log("read err " + Marshal.GetLastWin32Error());
            return -1;
        }
        return BitConverter.ToInt32(b, 0);
    }

    static void Diag()
    {
        IntPtr k32 = Native.GetModuleHandleA("kernel32.dll");
        Log("k32 local " + k32.ToString("X"));
        Log("read test " + ReadTest(k32).ToString("X"));
        IntPtr a = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)16, 0x3000, 0x40);
        Log("alloc " + a.ToString("X") + " err " + Marshal.GetLastWin32Error());
        if (a != IntPtr.Zero)
        {
            Mem.W(a, new byte[] { 0xC3 });
            IntPtr tid;
            IntPtr th = Native.CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, a, IntPtr.Zero, 0, out tid);
            Log("thread " + th.ToString("X") + " err " + Marshal.GetLastWin32Error());
            if (th != IntPtr.Zero) { Native.WaitForSingleObject(th, 3000); Native.CloseHandle(th); }
        }
        Log("ga shared " + ModuleBaseShared("GameAssembly.dll").ToString("X"));
    }

'@

if (-not $t.Contains('OpenLadder')) {
    $m0 = 'handle = Native.OpenProcess(0x1F0FFF, false, p.Id);'
    if (-not $t.Contains($m0)) { Write-Host '[-] marker openprocess'; exit 1 }
    $t = $t.Replace($m0, 'handle = OpenLadder(p.Id);')

    $m1 = 'static IntPtr ModuleBaseShared(string want)'
    if (-not $t.Contains($m1)) { Write-Host '[-] marker shared'; exit 1 }
    $t = $t.Replace($m1, $meth + $m1)

    $r1 = 'if (Mem.R4(k32) != 0x5A4D) return IntPtr.Zero;'
    if ($t.Contains($r1)) { $t = $t.Replace($r1, 'Log("k32 mz " + Mem.R4(k32).ToString("X"));') }
    $r2 = 'if (Mem.R4(baseAddr) != 0x5A4D) return IntPtr.Zero;'
    if ($t.Contains($r2)) { $t = $t.Replace($r2, 'Log("ga mz " + Mem.R4(baseAddr).ToString("X"));') }
    $r3 = 'if (baseAddr == IntPtr.Zero) { Log("GetModuleHandle: 0"); return IntPtr.Zero; }'
    if ($t.Contains($r3)) { $t = $t.Replace($r3, 'if (baseAddr == IntPtr.Zero) { Log("GetModuleHandle 0 err " + Marshal.GetLastWin32Error()); return IntPtr.Zero; }') }
    $r4 = 'if (Native.GetProcAddress(k32, "Sleep") == IntPtr.Zero) return IntPtr.Zero;'
    if ($t.Contains($r4)) { $t = $t.Replace($r4, 'if (Native.GetProcAddress(k32, "Sleep") == IntPtr.Zero) { Log("no Sleep export"); return IntPtr.Zero; }') }

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