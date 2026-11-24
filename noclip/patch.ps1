$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot 'noclip.cs'
if (-not (Test-Path $p)) { Write-Host '[-] no noclip.cs'; exit 1 }
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$nl = [string][char]13 + [char]10
$enc = New-Object Text.UTF8Encoding($true)

$pin = @'
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int cls, byte[] buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32W
    {
        public uint dwSize; public uint th32ModuleID; public uint th32ProcessID;
        public uint GlblcntUsage; public uint ProccntUsage; public IntPtr modBaseAddr;
        public uint modBaseSize; public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);
'@

$meth = @'
    static IntPtr ModuleBase(string want)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr r = ModuleBasePsapi(want);
            if (r != IntPtr.Zero) return r;
            r = ModuleBasePeb(want);
            if (r != IntPtr.Zero) return r;
            r = ModuleBaseToolhelp(want);
            if (r != IntPtr.Zero) return r;
            Thread.Sleep(150);
        }
        return IntPtr.Zero;
    }

    static IntPtr ModuleBasePsapi(string want)
    {
        IntPtr[] mods = new IntPtr[2048];
        int need;
        if (!Native.EnumProcessModulesEx(handle, mods, mods.Length * IntPtr.Size, out need, 3))
        {
            Log("psapi: " + Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        int n = need / IntPtr.Size;
        for (int i = 0; i < n && i < mods.Length; i++)
        {
            StringBuilder sb = new StringBuilder(260);
            if (Native.GetModuleBaseNameA(handle, mods[i], sb, 260) > 0)
                if (string.Equals(sb.ToString(), want, StringComparison.OrdinalIgnoreCase)) return mods[i];
        }
        return IntPtr.Zero;
    }

    static IntPtr ModuleBaseToolhelp(string want)
    {
        IntPtr snap = Native.CreateToolhelp32Snapshot(0x8 | 0x10, pid);
        if (snap == (IntPtr)(-1)) return IntPtr.Zero;
        try
        {
            Native.MODULEENTRY32W me = new Native.MODULEENTRY32W();
            me.dwSize = (uint)Marshal.SizeOf(typeof(Native.MODULEENTRY32W));
            if (!Native.Module32FirstW(snap, ref me)) return IntPtr.Zero;
            do
            {
                if (string.Equals(me.szModule, want, StringComparison.OrdinalIgnoreCase)) return me.modBaseAddr;
            } while (Native.Module32NextW(snap, ref me));
            return IntPtr.Zero;
        }
        finally { Native.CloseHandle(snap); }
    }

    static IntPtr ModuleBasePeb(string want)
    {
        byte[] pbi = new byte[48];
        int len;
        if (Native.NtQueryInformationProcess(handle, 0, pbi, 48, out len) != 0) return IntPtr.Zero;
        long peb = BitConverter.ToInt64(pbi, 8);
        if (peb == 0) return IntPtr.Zero;
        long ldr = Mem.R8((IntPtr)(peb + 0x18));
        if (ldr == 0) return IntPtr.Zero;
        long head = ldr + 0x10;
        long cur = Mem.R8((IntPtr)head);
        for (int i = 0; i < 512 && cur != 0 && cur != head; i++)
        {
            long baseAddr = Mem.R8((IntPtr)(cur + 0x20));
            int nameLen = Mem.R4((IntPtr)(cur + 0x48)) & 0xFFFF;
            long namePtr = Mem.R8((IntPtr)(cur + 0x50));
            if (namePtr != 0 && nameLen > 0 && nameLen < 400)
            {
                string nm = Encoding.Unicode.GetString(Mem.R((IntPtr)namePtr, nameLen));
                if (string.Equals(nm, want, StringComparison.OrdinalIgnoreCase)) return (IntPtr)baseAddr;
            }
            cur = Mem.R8((IntPtr)cur);
        }
        return IntPtr.Zero;
    }
'@

if (-not $t.Contains('ModuleBasePeb')) {
    $m1 = 'static IntPtr handle, stubCall,'
    if ($t.Contains($m1) -and -not $t.Contains('static int pid;')) {
        $t = $t.Replace($m1, 'static int pid;' + $nl + '    ' + $m1)
    }
    $m2 = 'handle = Native.OpenProcess(0x1F0FFF, false, p.Id);'
    if ($t.Contains($m2) -and -not $t.Contains('pid = p.Id;')) {
        $t = $t.Replace($m2, 'pid = p.Id;' + $nl + '            ' + $m2)
    }
    $m3 = 'public static extern short GetAsyncKeyState(int vk);'
    if ($t.Contains($m3)) {
        $t = $t.Replace($m3, $m3 + $nl + $pin)
    }
    $m4 = 'static IntPtr ModuleBase(string want)'
    $m5 = 'static readonly object gate = new object();'
    if (-not $t.Contains($m4) -or -not $t.Contains($m5)) { Write-Host '[-] markers not found'; exit 1 }
    $t = $t.Replace($m4, 'static IntPtr ModuleBaseOld(string want)')
    $t = $t.Replace($m5, $m5 + $nl + $meth)
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
if (Test-Path $out) { $out = Join-Path $PSScriptRoot 'noclip_new.exe'; Write-Host '[!] noclip.exe locked -> noclip_new.exe' }

$cargs = @('/nologo', '/platform:x64', '/target:winexe', '/optimize+', ('/out:' + $out),
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  (Join-Path $PSScriptRoot 'noclip.cs'))
& $csc $cargs
if ($LASTEXITCODE -ne 0) { Write-Host '[-] compile failed'; exit 1 }
Write-Host ('[+] ok: ' + $out)