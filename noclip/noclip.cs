// noclip.cs - NOCLIP для ROB RUN (внешний модуль, без инжекта)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, int type, int protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr sa, IntPtr stack, IntPtr start, IntPtr param, int flags, out IntPtr tid);
    [DllImport("kernel32.dll")]
    public static extern int WaitForSingleObject(IntPtr h, int ms);
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int needed, int filter);
    [DllImport("psapi.dll", CharSet = CharSet.Ansi)]
    public static extern int GetModuleBaseNameA(IntPtr h, IntPtr mod, StringBuilder name, int size);
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);
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
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr mod, string name);
}

static class Mem
{
    public static IntPtr H;
    public static IntPtr Add(IntPtr p, long n) { return (IntPtr)((long)p + n); }
    public static byte[] R(IntPtr a, int len)
    {
        byte[] b = new byte[len]; IntPtr got;
        Native.ReadProcessMemory(H, a, b, (IntPtr)len, out got);
        return b;
    }
    public static int R4(IntPtr a) { byte[] b = R(a, 4); return BitConverter.ToInt32(b, 0); }
    public static long R8(IntPtr a) { byte[] b = R(a, 8); return BitConverter.ToInt64(b, 0); }
    public static float RF(IntPtr a) { byte[] b = R(a, 4); return BitConverter.ToSingle(b, 0); }
    public static string RS(IntPtr a, int max)
    {
        byte[] b = R(a, max); int n = 0;
        while (n < b.Length && b[n] != 0) n++;
        return Encoding.ASCII.GetString(b, 0, n);
    }
    public static void W(IntPtr a, byte[] b) { IntPtr w; Native.WriteProcessMemory(H, a, b, (IntPtr)b.Length, out w); }
    public static void W4(IntPtr a, int v) { W(a, BitConverter.GetBytes(v)); }
}

struct Vec3 { public float x, y, z; }

static class Engine
{
    public static Action<string> Log = delegate { };
    public static bool Attached = false;
    public static volatile bool Flying = false;
    public static double Speed = 18.0;

    static int pid;
    static IntPtr handle, stubCall, blockCall, stubLoop, blockLoop, sleepFn;
    static IntPtr fnDomainGet, fnDomainAsm, fnAsmImage, fnImageName, fnClassFromName, fnClassMethod;
    static IntPtr fnClassType, fnRuntimeInvoke, fnThreadAttach, fnThreadDetach;
    static IntPtr fnGetMain, fnGetTransform, fnGetPosition, fnGetForward, fnSetDetect, fnSetPos;
    static IntPtr domain, playerTransform, playerCc, camTransform;
    static List<IntPtr> images = new List<IntPtr>();
    static Thread ticker;
    static readonly object gate = new object();
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
            r = ModuleBaseShared(want);
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

        static IntPtr OpenLadderOld(int id)
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
static IntPtr ModuleBaseShared(string want)
    {
        IntPtr k32 = Native.GetModuleHandleA("kernel32.dll");
        if (k32 == IntPtr.Zero) return IntPtr.Zero;
        Log("k32 mz " + Mem.R4(k32).ToString("X"));
        if (Native.GetProcAddress(k32, "Sleep") == IntPtr.Zero) { Log("no Sleep export"); return IntPtr.Zero; }
        IntPtr ghma = Native.GetProcAddress(k32, "GetModuleHandleA");
        if (ghma == IntPtr.Zero) return IntPtr.Zero;
        IntPtr baseAddr = CallRaw(ghma, Str(want));
        if (baseAddr == IntPtr.Zero) { Log("GetModuleHandle 0 err " + Marshal.GetLastWin32Error()); return IntPtr.Zero; }
        Log("ga mz " + Mem.R4(baseAddr).ToString("X"));
        Log("shared base: " + baseAddr.ToString("X"));
        return baseAddr;
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

    static void Put(byte[] b, int off, long v) { BitConverter.GetBytes(v).CopyTo(b, off); }

    static byte[] BuildCallStub()
    {
        return new byte[] {
            0x53, 0x48,0x8B,0xD9, 0x48,0x8B,0x4B,0x40, 0x48,0x83,0xEC,0x30,
            0xFF,0x53,0x30, 0x48,0x83,0xC4,0x30,
            0x48,0x8B,0x4B,0x08, 0x48,0x8B,0x53,0x10, 0x4C,0x8B,0x43,0x18, 0x4C,0x8B,0x4B,0x20,
            0x48,0x83,0xEC,0x30, 0xFF,0x13, 0x48,0x83,0xC4,0x30,
            0x48,0x89,0x43,0x28,
            0x48,0x8B,0x4B,0x40, 0x48,0x83,0xEC,0x30, 0xFF,0x53,0x38, 0x48,0x83,0xC4,0x30,
            0x5B, 0xC3
        };
    }

    static byte[] BuildLoopStub()
    {
        List<byte> c = new List<byte>();
        c.AddRange(new byte[] { 0x53, 0x48,0x8B,0xD9, 0x48,0x8B,0x4B,0x40, 0x48,0x83,0xEC,0x30, 0xFF,0x53,0x30, 0x48,0x83,0xC4,0x30 });
        int L = c.Count;
        c.AddRange(new byte[] { 0x8B,0x43,0x50, 0x85,0xC0 });
        int j1 = c.Count; c.Add(0x74); c.Add(0);
        c.AddRange(new byte[] { 0x48,0x8B,0x4B,0x48, 0x48,0x85,0xC9 });
        int j2 = c.Count; c.Add(0x74); c.Add(0);
        c.AddRange(new byte[] { 0x48,0x83,0xEC,0x30 });
        c.AddRange(new byte[] { 0x8B,0x43,0x58, 0x89,0x44,0x24,0x20 });
        c.AddRange(new byte[] { 0x8B,0x43,0x5C, 0x89,0x44,0x24,0x24 });
        c.AddRange(new byte[] { 0x8B,0x43,0x60, 0x89,0x44,0x24,0x28 });
        c.AddRange(new byte[] { 0x48,0x8D,0x54,0x24,0x20 });
        c.AddRange(new byte[] { 0xFF,0x53,0x68 });
        c.AddRange(new byte[] { 0x8B,0x4B,0x70 });
        c.AddRange(new byte[] { 0xFF,0x53,0x78 });
        c.AddRange(new byte[] { 0x48,0x83,0xC4,0x30 });
        int jb = c.Count; c.Add(0xEB); c.Add(0);
        int D = c.Count;
        c.AddRange(new byte[] { 0x48,0x8B,0x4B,0x40, 0x48,0x83,0xEC,0x30, 0xFF,0x53,0x38, 0x48,0x83,0xC4,0x30, 0x5B, 0xC3 });
        c[j1 + 1] = (byte)(D - (j1 + 2));
        c[j2 + 1] = (byte)(D - (j2 + 2));
        c[jb + 1] = (byte)(L - (jb + 2));
        return c.ToArray();
    }

    static Dictionary<string, IntPtr> Exports(IntPtr module)
    {
        Dictionary<string, IntPtr> d = new Dictionary<string, IntPtr>();
        if (Mem.R4(module) != 0x5A4D) return d;
        IntPtr nt = Mem.Add(module, Mem.R4(Mem.Add(module, 0x3C)));
        if (Mem.R4(nt) != 0x4550) return d;
        IntPtr edir = Mem.Add(module, Mem.R4(Mem.Add(nt, 0x88)));
        int cnt = Mem.R4(Mem.Add(edir, 0x18));
        IntPtr names = Mem.Add(module, Mem.R4(Mem.Add(edir, 0x20)));
        IntPtr ords = Mem.Add(module, Mem.R4(Mem.Add(edir, 0x24)));
        IntPtr funcs = Mem.Add(module, Mem.R4(Mem.Add(edir, 0x1C)));
        for (int i = 0; i < cnt; i++)
        {
            string n = Mem.RS(Mem.Add(module, Mem.R4(Mem.Add(names, i * 4))), 128);
            int ord = Mem.R4(Mem.Add(ords, i * 2)) & 0xFFFF;
            d[n] = Mem.Add(module, Mem.R4(Mem.Add(funcs, ord * 4)));
        }
        return d;
    }

    static IntPtr ModuleBaseOld(string want)
    {
        IntPtr[] mods = new IntPtr[1024];
        int need;
        if (!Native.EnumProcessModulesEx(handle, mods, mods.Length * IntPtr.Size, out need, 3)) return IntPtr.Zero;
        int n = need / IntPtr.Size;
        for (int i = 0; i < n && i < mods.Length; i++)
        {
            StringBuilder sb = new StringBuilder(260);
            if (Native.GetModuleBaseNameA(handle, mods[i], sb, 260) > 0)
                if (string.Equals(sb.ToString(), want, StringComparison.OrdinalIgnoreCase)) return mods[i];
        }
        return IntPtr.Zero;
    }

    static IntPtr Call(IntPtr fn, IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4)
    {
        byte[] b = new byte[0x48];
        Put(b, 0x00, (long)fn); Put(b, 0x08, (long)a1); Put(b, 0x10, (long)a2);
        Put(b, 0x18, (long)a3); Put(b, 0x20, (long)a4);
        Put(b, 0x30, (long)fnThreadAttach); Put(b, 0x38, (long)fnThreadDetach); Put(b, 0x40, (long)domain);
        Mem.W(blockCall, b);
        IntPtr tid;
        IntPtr t = Native.CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, stubCall, blockCall, 0, out tid);
        if (t == IntPtr.Zero) throw new Exception("CreateRemoteThread err " + Marshal.GetLastWin32Error());
        Native.WaitForSingleObject(t, 8000);
        Native.CloseHandle(t);
        return (IntPtr)Mem.R8(Mem.Add(blockCall, 0x28));
    }

    static IntPtr Invoke(IntPtr method, IntPtr obj, IntPtr[] args)
    {
        IntPtr p = IntPtr.Zero;
        if (args != null && args.Length > 0)
        {
            p = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)(args.Length * 8), 0x3000, 0x04);
            byte[] b = new byte[args.Length * 8];
            for (int i = 0; i < args.Length; i++) Put(b, i * 8, (long)args[i]);
            Mem.W(p, b);
        }
        return Call(fnRuntimeInvoke, method, obj, p, IntPtr.Zero);
    }

    static IntPtr Str(string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        IntPtr p = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)(b.Length + 1), 0x3000, 0x04);
        Mem.W(p, b);
        return p;
    }

    static IntPtr FindClass(string ns, string name)
    {
        foreach (IntPtr img in images)
        {
            IntPtr k = Call(fnClassFromName, img, Str(ns), Str(name), IntPtr.Zero);
            if (k != IntPtr.Zero) return k;
        }
        return IntPtr.Zero;
    }

    static IntPtr GetMethod(IntPtr klass, string name, int argc)
    {
        if (klass == IntPtr.Zero) return IntPtr.Zero;
        return Call(fnClassMethod, klass, Str(name), (IntPtr)argc, IntPtr.Zero);
    }

    static Vec3 GetVec(IntPtr fn, IntPtr tr)
    {
        Vec3 v = new Vec3();
        IntPtr box = Invoke(fn, tr, null);
        if (box == IntPtr.Zero) return v;
        v.x = Mem.RF(Mem.Add(box, 0x10));
        v.y = Mem.RF(Mem.Add(box, 0x14));
        v.z = Mem.RF(Mem.Add(box, 0x18));
        return v;
    }

    static void SetDetect(IntPtr cc, bool on)
    {
        IntPtr p = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)8, 0x3000, 0x04);
        Mem.W(p, new byte[] { (byte)(on ? 1 : 0), 0, 0, 0, 0, 0, 0, 0 });
        Invoke(fnSetDetect, cc, new IntPtr[] { p });
    }

    public static bool Attach(out string err)
    {
        err = null;
        if (Attached) return true;
        try
        {
            Process p = null;
            Process[] ps = Process.GetProcessesByName("ROB RUN");
            if (ps.Length == 0)
            {
                foreach (Process q in Process.GetProcesses())
                {
                    try { if (q.ProcessName.ToUpper().Replace(" ", "") == "ROBRUN") { p = q; break; } }
                    catch { }
                }
            }
            else p = ps[0];
            if (p == null) { err = "процесс ROB RUN не найден - запусти игру"; return false; }

            pid = p.Id;
            handle = OpenLadder(p.Id);
            if (handle == IntPtr.Zero)
            { err = "OpenProcess отказ (код " + Marshal.GetLastWin32Error() + "). Запусти от админа."; return false; }
            Mem.H = handle;
            Log("подключено, PID " + p.Id);

            IntPtr ga = ModuleBase("GameAssembly.dll");
            if (ga == IntPtr.Zero) { err = "GameAssembly.dll не найдена"; return false; }
            Log("GameAssembly " + ga.ToString("X"));

            Dictionary<string, IntPtr> ex = Exports(ga);
            string[] need = { "il2cpp_domain_get", "il2cpp_domain_get_assemblies", "il2cpp_assembly_get_image",
                "il2cpp_image_get_name", "il2cpp_class_from_name", "il2cpp_class_get_method_from_name",
                "il2cpp_class_get_type", "il2cpp_runtime_invoke", "il2cpp_thread_attach", "il2cpp_thread_detach" };
            foreach (string n in need)
                if (!ex.ContainsKey(n)) { err = "нет экспорта " + n; return false; }
            fnDomainGet = ex["il2cpp_domain_get"];
            fnDomainAsm = ex["il2cpp_domain_get_assemblies"];
            fnAsmImage = ex["il2cpp_assembly_get_image"];
            fnImageName = ex["il2cpp_image_get_name"];
            fnClassFromName = ex["il2cpp_class_from_name"];
            fnClassMethod = ex["il2cpp_class_get_method_from_name"];
            fnClassType = ex["il2cpp_class_get_type"];
            fnRuntimeInvoke = ex["il2cpp_runtime_invoke"];
            fnThreadAttach = ex["il2cpp_thread_attach"];
            fnThreadDetach = ex["il2cpp_thread_detach"];

            IntPtr k32 = ModuleBase("kernel32.dll");
            if (k32 != IntPtr.Zero)
            {
                Dictionary<string, IntPtr> ke = Exports(k32);
                if (ke.ContainsKey("Sleep")) sleepFn = ke["Sleep"];
            }
            if (sleepFn == IntPtr.Zero) { err = "нет kernel32!Sleep"; return false; }

            stubCall = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x100, 0x3000, 0x40);
            blockCall = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x100, 0x3000, 0x04);
            stubLoop = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x200, 0x3000, 0x40);
            blockLoop = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)0x100, 0x3000, 0x04);
            if (stubCall == IntPtr.Zero || blockCall == IntPtr.Zero || stubLoop == IntPtr.Zero || blockLoop == IntPtr.Zero)
            { err = "VirtualAllocEx не сработал"; return false; }
            Mem.W(stubCall, BuildCallStub());
            Mem.W(stubLoop, BuildLoopStub());

            domain = Call(fnDomainGet, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (domain == IntPtr.Zero) { err = "домен il2cpp пустой (игра ещё грузится?)"; return false; }

            IntPtr szP = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)8, 0x3000, 0x04);
            IntPtr asms = Call(fnDomainAsm, domain, szP, IntPtr.Zero, IntPtr.Zero);
            int cnt = Mem.R4(szP);
            Log("сборок: " + cnt);
            if (cnt <= 0 || cnt > 4000) { err = "список сборок пустой"; return false; }
            images.Clear();
            for (int i = 0; i < cnt; i++)
            {
                IntPtr a = (IntPtr)Mem.R8(Mem.Add(asms, i * 8));
                if (a == IntPtr.Zero) continue;
                IntPtr img = Call(fnAsmImage, a, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (img != IntPtr.Zero) images.Add(img);
            }

            IntPtr camClass = FindClass("UnityEngine", "Camera");
            IntPtr trClass = FindClass("UnityEngine", "Transform");
            IntPtr ccClass = FindClass("UnityEngine", "CharacterController");
            IntPtr objClass = FindClass("UnityEngine", "Object");
            IntPtr compClass = FindClass("UnityEngine", "Component");
            if (camClass == IntPtr.Zero || trClass == IntPtr.Zero || ccClass == IntPtr.Zero || objClass == IntPtr.Zero)
            { err = "классы Unity не найдены"; return false; }

            IntPtr mMain = GetMethod(camClass, "get_main", 0);
            IntPtr mTr = GetMethod(compClass, "get_transform", 0);
            IntPtr mPos = GetMethod(trClass, "get_position", 0);
            IntPtr mFwd = GetMethod(trClass, "get_forward", 0);
            IntPtr mSetPos = GetMethod(trClass, "set_position", 1);
            IntPtr mSetDet = GetMethod(ccClass, "set_detectCollisions", 1);
            if (mMain == IntPtr.Zero || mTr == IntPtr.Zero || mPos == IntPtr.Zero || mFwd == IntPtr.Zero
                || mSetPos == IntPtr.Zero || mSetDet == IntPtr.Zero)
            { err = "не найдены нужные методы Unity"; return false; }

            fnGetMain = mMain; fnGetTransform = mTr; fnGetPosition = mPos;
            fnGetForward = mFwd; fnSetDetect = mSetDet;
            fnSetPos = (IntPtr)Mem.R8(mSetPos);
            if (fnSetPos == IntPtr.Zero) { err = "адрес set_position пустой"; return false; }

            Attached = true;
            return true;
        }
        catch (Exception e) { err = e.Message; return false; }
    }

    static bool ResolvePlayer(out string err)
    {
        err = null;
        try
        {
            IntPtr cam = Call(fnGetMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (cam == IntPtr.Zero) { err = "камера не найдена (ты в мире?)"; return false; }
            camTransform = Invoke(fnGetTransform, cam, null);
            if (camTransform == IntPtr.Zero) { err = "трансформ камеры пустой"; return false; }

            Vec3 cp = GetVec(fnGetPosition, camTransform);
            Vec3 fw = GetVec(fnGetForward, camTransform);

            IntPtr ccClass = FindClass("UnityEngine", "CharacterController");
            IntPtr objClass = FindClass("UnityEngine", "Object");
            IntPtr mFind = GetMethod(objClass, "FindObjectsOfType", 1);
            IntPtr arr = IntPtr.Zero;
            IntPtr t = Call(fnClassType, ccClass, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (mFind != IntPtr.Zero) arr = Invoke(mFind, IntPtr.Zero, new IntPtr[] { t });
            if (arr == IntPtr.Zero)
            {
                IntPtr mFind2 = GetMethod(objClass, "FindObjectsByType", 2);
                if (mFind2 == IntPtr.Zero) { err = "FindObjectsOfType/ByType нет"; return false; }
                arr = Invoke(mFind2, IntPtr.Zero, new IntPtr[] { t, (IntPtr)0 });
            }
            if (arr == IntPtr.Zero) { err = "список контроллеров пустой"; return false; }

            int len = Mem.R4(Mem.Add(arr, 0x18));
            Log("контроллеров в мире: " + len);
            if (len <= 0 || len > 20000) { err = "странная длина массива: " + len; return false; }

            double best = double.MaxValue;
            IntPtr bestTr = IntPtr.Zero, bestCc = IntPtr.Zero;
            for (int i = 0; i < len; i++)
            {
                IntPtr cc = (IntPtr)Mem.R8(Mem.Add(arr, 0x20 + i * 8));
                if (cc == IntPtr.Zero) continue;
                IntPtr tr = Invoke(fnGetTransform, cc, null);
                if (tr == IntPtr.Zero) continue;
                Vec3 p = GetVec(fnGetPosition, tr);
                float dx = p.x - cp.x, dy = p.y - cp.y, dz = p.z - cp.z;
                float dot = dx * fw.x + dy * fw.y + dz * fw.z;
                if (dot < 0.3f) continue;
                float px = dx - fw.x * dot, py = dy - fw.y * dot, pz = dz - fw.z * dot;
                double score = Math.Sqrt(px * px + py * py + pz * pz) + 0.15 * dot;
                if (score < best) { best = score; bestTr = tr; bestCc = cc; }
            }
            if (bestTr == IntPtr.Zero) { err = "свой контроллер не найден"; return false; }
            playerTransform = bestTr; playerCc = bestCc;
            Log("игрок найден (score " + Math.Round(best, 2) + ")");
            return true;
        }
        catch (Exception e) { err = e.Message; return false; }
    }

    public static bool Start(out string err)
    {
        err = null;
        lock (gate)
        {
            if (Flying) return true;
            if (!ResolvePlayer(out err)) return false;
            try { SetDetect(playerCc, false); }
            catch (Exception e) { err = "detectCollisions: " + e.Message; return false; }

            byte[] b = new byte[0x80];
            Put(b, 0x30, (long)fnThreadAttach); Put(b, 0x38, (long)fnThreadDetach); Put(b, 0x40, (long)domain);
            Put(b, 0x48, (long)playerTransform); Put(b, 0x50, 1);
            Put(b, 0x68, (long)fnSetPos); Put(b, 0x70, 10); Put(b, 0x78, (long)sleepFn);
            Mem.W(blockLoop, b);

            IntPtr tid;
            IntPtr t = Native.CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, stubLoop, blockLoop, 0, out tid);
            if (t == IntPtr.Zero) { err = "поток полёта не создан: " + Marshal.GetLastWin32Error(); return false; }
            Native.CloseHandle(t);

            Flying = true;
            ticker = new Thread(Tick); ticker.IsBackground = true; ticker.Start();
            Log("NOCLIP включён");
            return true;
        }
    }

    public static void Stop()
    {
        lock (gate)
        {
            if (!Flying) return;
            Flying = false;
            if (ticker != null) { try { ticker.Join(400); } catch { } ticker = null; }
            try { Mem.W4(Mem.Add(blockLoop, 0x50), 0); } catch { }
            try { if (playerCc != IntPtr.Zero) SetDetect(playerCc, true); } catch { }
            Log("NOCLIP выключен");
        }
    }

    static bool Down(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

    static void Tick()
    {
        Stopwatch sw = Stopwatch.StartNew();
        Vec3 pos = GetVec(fnGetPosition, playerTransform);
        Vec3 fw = GetVec(fnGetForward, camTransform);
        double acc = 0;
        while (Flying)
        {
            double dt = sw.Elapsed.TotalMilliseconds; sw.Restart();
            acc += dt;
            if (acc > 100) { acc = 0; fw = GetVec(fnGetForward, camTransform); }

            double sp = Speed * (Down(0x10) ? 2.5 : 1.0) * (dt / 1000.0);
            double lx = fw.x, ly = fw.y, lz = fw.z;
            double len = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            if (len < 0.001) { lx = 0; ly = 0; lz = 1; len = 1; }
            lx /= len; ly /= len; lz /= len;
            double rx = -lz, rz = lx;
            double rl = Math.Sqrt(rx * rx + rz * rz);
            if (rl > 0.001) { rx /= rl; rz /= rl; } else { rx = 1; rz = 0; }

            double mx = 0, my = 0, mz = 0;
            if (Down(0x57)) { mx += lx; my += ly; mz += lz; }
            if (Down(0x53)) { mx -= lx; my -= ly; mz -= lz; }
            if (Down(0x44)) { mx += rx; mz += rz; }
            if (Down(0x41)) { mx -= rx; mz -= rz; }
            if (Down(0x20)) { my += 1; }
            if (Down(0x11)) { my -= 1; }

            pos.x += (float)(mx * sp); pos.y += (float)(my * sp); pos.z += (float)(mz * sp);

            byte[] b = new byte[12];
            BitConverter.GetBytes(pos.x).CopyTo(b, 0);
            BitConverter.GetBytes(pos.y).CopyTo(b, 4);
            BitConverter.GetBytes(pos.z).CopyTo(b, 8);
            Mem.W(Mem.Add(blockLoop, 0x58), b);
            Thread.Sleep(8);
        }
    }
}

public class MainForm : Form
{
    Label lblState;
    NumericUpDown num;
    TextBox log;
    Button btn;
    System.Windows.Forms.Timer pump;
    bool f1, kUp, kDn;

    public MainForm()
    {
        Text = "NOCLIP - ROB RUN";
        Width = 400; Height = 390;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 410, 80);
        BackColor = Color.FromArgb(22, 22, 26);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);

        Label t = new Label();
        t.Text = "NOCLIP"; t.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        t.ForeColor = Color.FromArgb(90, 170, 255); t.SetBounds(14, 8, 200, 34);
        Controls.Add(t);

        Label info = new Label();
        info.Text = "Полёт сквозь стены. Зайди в мир, нажми ПОДКЛЮЧИТЬСЯ,\nпотом F1 в игре.";
        info.SetBounds(14, 44, 370, 38); info.ForeColor = Color.Gray;
        Controls.Add(info);

        lblState = new Label();
        lblState.Text = "Состояние: не подключено";
        lblState.SetBounds(14, 86, 370, 22); lblState.ForeColor = Color.Silver;
        Controls.Add(lblState);

        Label s = new Label();
        s.Text = "Скорость (м/с):"; s.SetBounds(14, 118, 130, 22); s.ForeColor = Color.Silver;
        Controls.Add(s);

        num = new NumericUpDown();
        num.Minimum = 1; num.Maximum = 120; num.Value = 18;
        num.SetBounds(150, 116, 70, 24);
        num.BackColor = Color.FromArgb(35, 35, 42); num.ForeColor = Color.White;
        num.ValueChanged += delegate { Engine.Speed = (double)num.Value; };
        Controls.Add(num);

        btn = new Button();
        btn.Text = "ПОДКЛЮЧИТЬСЯ";
        btn.SetBounds(14, 150, 370, 34);
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = Color.FromArgb(35, 35, 42); btn.ForeColor = Color.White;
        btn.Click += delegate { Connect(); };
        Controls.Add(btn);

        Label k = new Label();
        k.Text = "F1 вкл/выкл • WASD полёт • Space/Ctrl вверх/вниз • Shift ускорение • F2/F3 скорость";
        k.SetBounds(14, 190, 370, 34); k.ForeColor = Color.Gray;
        Controls.Add(k);

        log = new TextBox();
        log.Multiline = true; log.ScrollBars = ScrollBars.Vertical; log.ReadOnly = true;
        log.SetBounds(14, 228, 370, 100);
        log.BackColor = Color.FromArgb(15, 15, 18); log.ForeColor = Color.Silver;
        log.BorderStyle = BorderStyle.None;
        Controls.Add(log);

        Engine.Log = delegate (string m) { Add(m); };
        FormClosing += delegate { try { Engine.Stop(); } catch { } };

        pump = new System.Windows.Forms.Timer();
        pump.Interval = 40;
        pump.Tick += delegate { Keys(); };
        pump.Start();
    }

    void Add(string m)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)delegate
            {
                log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n");
                log.SelectionStart = log.TextLength; log.ScrollToCaret();
            });
        }
        catch { }
    }

    void Connect()
    {
        string err;
        if (Engine.Attached) { Add("уже подключено"); return; }
        Add("подключаюсь...");
        if (Engine.Attach(out err)) { lblState.Text = "Состояние: подключено, жду F1"; btn.Text = "ПОДКЛЮЧЕНО"; }
        else { Add("ошибка: " + err); lblState.Text = "Состояние: ошибка"; }
    }

    void Keys()
    {
        bool now = (Native.GetAsyncKeyState(0x70) & 0x8000) != 0;
        if (now && !f1 && Engine.Attached)
        {
            string err;
            if (Engine.Flying) { Engine.Stop(); lblState.Text = "Состояние: выключено"; }
            else if (Engine.Start(out err)) lblState.Text = "Состояние: NOCLIP ВКЛЮЧЁН";
            else Add("ошибка: " + err);
        }
        f1 = now;

        bool up = (Native.GetAsyncKeyState(0x71) & 0x8000) != 0;
        bool dn = (Native.GetAsyncKeyState(0x72) & 0x8000) != 0;
        if (up && !kUp) num.Value = Math.Min(num.Maximum, num.Value + 2);
        if (dn && !kDn) num.Value = Math.Max(num.Minimum, num.Value - 2);
        kUp = up; kDn = dn;
    }

    [STAThread]
    static void Main()
    {
        try { Application.EnableVisualStyles(); Application.Run(new MainForm()); }
        catch (Exception e) { MessageBox.Show(e.Message, "noclip"); }
    }
}