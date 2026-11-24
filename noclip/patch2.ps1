$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot 'noclip.cs'
if (-not (Test-Path $p)) { Write-Host '[-] no noclip.cs'; exit 1 }
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$nl = [string][char]13 + [char]10
$enc = New-Object Text.UTF8Encoding($true)

$pin = @'
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr mod, string name);
'@

$meth = @'
    static IntPtr stubRaw, blockRaw;

    static IntPtr CallRaw(IntPtr fn, IntPtr a1)
    {
        if (stubRaw == IntPtr.Zero)
        {
            stubRaw = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x80, 0x3000, 0x40);
            blockRaw = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x40, 0x3000, 0x04);
            if (stubRaw == IntPtr.Zero || blockRaw == IntPtr.Zero) return IntPtr.Zero;
            Mem.W(stubRaw, new byte[] { 0x53,0x48,0x8B,0xD9, 0x48,0x8B,0x4B,0x08,
                0x48,0x83,0xEC,0x30, 0xFF,0x13, 0x48,0x83,0xC4,0x30,
                0x48,0x89,0x43,0x10, 0x5B, 0xC3 });
        }
        byte[] b = new byte[0x18];
        BitConverter.GetBytes((long)fn).CopyTo(b, 0);
        BitConverter.GetBytes((long)a1).CopyTo(b, 8);
        Mem.W(blockRaw, b);
        IntPtr tid;
        IntPtr th = Native.CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, stubRaw, blockRaw, 0, out tid);
        if (th == IntPtr.Zero) { Log("thread: " + Marshal.GetLastWin32Error()); return IntPtr.Zero; }
        Native.WaitForSingleObject(th, 5000);
        Native.CloseHandle(th);
        return (IntPtr)Mem.R8(Mem.Add(blockRaw, 0x10));
    }

    static IntPtr ModuleBaseShared(string want)
    {
        IntPtr k32 = Native.GetModuleHandleA("kernel32.dll");
        if (k32 == IntPtr.Zero) return IntPtr.Zero;
        if (Mem.R4(k32) != 0x5A4D) return IntPtr.Zero;
        if (Native.GetProcAddress(k32, "Sleep") == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ghma = Native.GetProcAddress(k32, "GetModuleHandleA");
        if (ghma == IntPtr.Zero) return IntPtr.Zero;
        IntPtr baseAddr = CallRaw(ghma, Str(want));
        if (baseAddr == IntPtr.Zero) { Log("GetModuleHandle: 0"); return IntPtr.Zero; }
        if (Mem.R4(baseAddr) != 0x5A4D) return IntPtr.Zero;
        Log("shared base: " + baseAddr.ToString("X"));
        return baseAddr;
    }

'@

if (-not $t.Contains('ModuleBaseShared')) {
    $m1 = 'public static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);'
    if (-not $t.Contains($m1)) { Write-Host '[-] marker pinvokes'; exit 1 }
    $t = $t.Replace($m1, $m1 + $nl + $pin)

    $m2 = 'static IntPtr ModuleBasePeb(string want)'
    if (-not $t.Contains($m2)) { Write-Host '[-] marker peb'; exit 1 }
    $t = $t.Replace($m2, $meth + $m2)

    $m3 = 'r = ModuleBaseToolhelp(want);'
    if (-not $t.Contains($m3)) { Write-Host '[-] marker chain'; exit 1 }
    $t = $t.Replace($m3, $m3 + $nl + '            if (r != IntPtr.Zero) return r;' + $nl + '            r = ModuleBaseShared(want);')

    [IO.File]::WriteAllText($p, $t, $enc)
    Write-Host '[+] patched'
} else {
    Write-Host '[i] already patched'
}

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host '[-] csc.exe not found'; exit 1 }

$out = Join-Path $PSScriptRoot 'noclip.exe'
try { if (Test-Path $out) { Remove-Item $out -Force } } catch { }
if (Test-Path $out) { $out = Join-Path $PSScriptRoot 'noclip_new.exe'; Write-Host '[!] locked -> noclip_new.exe' }

$cargs = @('/nologo', '/platform:x64', '/target:winexe', '/optimize+', ('/out:' + $out),
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  (Join-Path $PSScriptRoot 'noclip.cs'))
& $csc $cargs
if ($LASTEXITCODE -ne 0) { Write-Host '[-] compile failed'; exit 1 }
Write-Host ('[+] ok: ' + $out)