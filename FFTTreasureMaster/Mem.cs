using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FFTTreasureMaster;

/// <summary>
/// In-process memory access via Read/WriteProcessMemory with dynamic ASLR rebasing.
/// </summary>
internal static class Mem
{
    private static readonly nint Self = GetCurrentProcess();
    [ThreadStatic] private static byte[]? _scratch;

    // Dynamic ASLR rebase offset: (Actual Game Base - 0x140000000)
    public static readonly long AslrOffset;

    static Mem()
    {
        try
        {
            long baseAddr = Process.GetCurrentProcess().MainModule?.BaseAddress.ToInt64() ?? 0x140000000L;
            AslrOffset = baseAddr - 0x140000000L;
            try 
            { 
                ModLogger.Event(LogVerb.Startup, $"ASLR active: game base is 0x{baseAddr:X} (offset: 0x{AslrOffset:X})"); 
            } 
            catch { }
        }
        catch (Exception ex)
        {
            AslrOffset = 0;
            try 
            { 
                ModLogger.Warn(LogVerb.Startup, $"ASLR detection failed: {ex.Message}"); 
            } 
            catch { }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Rebase(long a)
    {
        // Rebase any address captured relative to the 0x140000000 default module base
        if (AslrOffset != 0 && a >= 0x140000000L && a < 0x200000000L)
            return a + AslrOffset;
        return a;
    }

    public static byte[] ReadBytes(long a, int n)
    {
        a = Rebase(a);
        var buf = new byte[n];
        if (!ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) || (int)got != n)
            throw new InvalidOperationException("ReadProcessMemory failed");
        return buf;
    }

    public static bool TryReadBytes(long a, int n, out byte[] buf)
    {
        a = Rebase(a);
        buf = new byte[n];
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n;
    }

    public static int ReadInto(long a, byte[] buf, int n)
    {
        a = Rebase(a);
        if (n > buf.Length) return 0;
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n ? n : 0;
    }

    public static void WriteBytes(long a, byte[] data)
    {
        a = Rebase(a);
        WriteProcessMemory(Self, (nint)a, data, (nuint)data.Length, out _);
    }

    public static void W8(long a, byte v)
    {
        a = Rebase(a);
        var s = _scratch ??= new byte[8];
        s[0] = v;
        WriteProcessMemory(Self, (nint)a, s, 1, out _);
    }

    public static void W32(long a, uint v)
    {
        a = Rebase(a);
        var s = _scratch ??= new byte[8];
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16); s[3] = (byte)(v >> 24);
        WriteProcessMemory(Self, (nint)a, s, 4, out _);
    }

    private static bool ReadScalar(long a, int n)
    {
        a = Rebase(a);
        var s = _scratch ??= new byte[8];
        return ReadProcessMemory(Self, (nint)a, s, (nuint)n, out var got) && (int)got == n;
    }

    public static byte U8(long a) => ReadScalar(a, 1) ? _scratch![0] : (byte)0;
    public static ushort U16(long a) => ReadScalar(a, 2) ? (ushort)(_scratch![0] | (_scratch[1] << 8)) : (ushort)0;
    public static uint U32(long a) => ReadScalar(a, 4)
        ? (uint)(_scratch![0] | (_scratch[1] << 8) | (_scratch[2] << 16) | (_scratch[3] << 24)) : 0u;
    public static ulong U64(long a)
    {
        if (!ReadScalar(a, 8)) return 0UL;
        var s = _scratch!;
        return (ulong)s[0]        | ((ulong)s[1] << 8)  | ((ulong)s[2] << 16) | ((ulong)s[3] << 24)
             | ((ulong)s[4] << 32) | ((ulong)s[5] << 40) | ((ulong)s[6] << 48) | ((ulong)s[7] << 56);
    }

    public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    public static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    public static bool Readable(long addr, int len) => Probe(addr, len, false);
    public static bool Writable(long addr, int len) => Probe(addr, len, true);

    private static bool Probe(long addr, int len, bool needWrite)
    {
        addr = Rebase(addr);
        if (VirtualQueryEx(Self, (nint)addr, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;
        if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
        const uint writable = 0x04 | 0x08 | 0x40 | 0x80;
        if ((mbi.Protect & (needWrite ? writable : READABLE)) == 0) return false;
        long b = (long)mbi.BaseAddress, e = b + (long)mbi.RegionSize;
        return addr >= b && addr + len <= e;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;
    private const uint READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    public static IEnumerable<(long baseAddr, long size)> Regions()
    {
        long addr = 0;
        var mbi = new MEMORY_BASIC_INFORMATION();
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (VirtualQueryEx(Self, (nint)addr, out mbi, (uint)mbiSize) == 0)
                break;
            long b = (long)mbi.BaseAddress;
            long size = (long)mbi.RegionSize;
            long next = b + size;
            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && (mbi.Protect & WRITABLE) != 0
                && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
                yield return (b, size);
            addr = next > addr ? next : addr + 0x1000;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint h, nint addr, [Out] byte[] buf, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint h, nint addr, byte[] buf, nuint size, out nuint written);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
