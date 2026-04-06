using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RaceElement.Data.Games.RichardBurnsRally;

/// <summary>
/// Memory reader for RBR (Richard Burns Rally) to extract wheel speed data.
/// Based on Adaptive_Trigger_RBR.py memory reading implementation.
/// Process: RichardBurnsRally_SSE.exe
/// </summary>
internal sealed partial class RBRMemoryReader : IDisposable
{
    private const string ProcessName = "RichardBurnsRally_SSE";
    private const string AltProcessName = "RichardBurnsRally";
    private IntPtr _processHandle = IntPtr.Zero;
    private IntPtr _baseAddress = IntPtr.Zero;
    private bool _isConnected = false;

    // Pointer chain: base_address + 0x493038 -> +1032 -> +64 -> wheel speeds
    // Matches Python: rbr_memory_reader.base_address + 4796472 (0x493038)
    private const long BaseOffset1 = 4796472; // 0x493038
    private const uint Offset1032 = 1032;
    private const uint Offset64 = 64;
    private const int WheelSpeedFL = 988;
    private const int WheelSpeedFR = 1676;
    private const int WheelSpeedRL = 2364;
    private const int WheelSpeedRR = 3052;

    // Max reasonable wheel speed: 500 km/h = 138.9 m/s
    private const float MaxWheelSpeedMs = 138.9f;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    // psapi.dll: enumerate loaded modules to reliably get the main module base address
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, uint nSize);

    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;

    /// <summary>
    /// Wheel speeds in km/h
    /// </summary>
    internal readonly record struct WheelSpeeds(float FrontLeft, float FrontRight, float RearLeft, float RearRight);

    public RBRMemoryReader()
    {
        TryConnect();
    }

    private bool TryConnect()
    {
        try
        {
            var processes = Process.GetProcessesByName(ProcessName);
            if (processes.Length == 0)
                processes = Process.GetProcessesByName(AltProcessName);

            if (processes.Length == 0)
            {
                _isConnected = false;
                return false;
            }

            var process = processes[0];
            // Request both VM_READ and QUERY_INFORMATION so EnumProcessModules works
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, process.Id);

            if (_processHandle == IntPtr.Zero)
            {
                Debug.WriteLine($"[RBR] OpenProcess failed for PID {process.Id}. Win32Error: {Marshal.GetLastWin32Error()}");
                _isConnected = false;
                return false;
            }

            _baseAddress = GetModuleBaseAddress(_processHandle, process.Id);
            Debug.WriteLine($"[RBR] Connected to {process.ProcessName} (PID: {process.Id}), BaseAddress=0x{_baseAddress.ToInt64():X8}");

            _isConnected = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RBR] TryConnect exception: {ex.Message}");
            _isConnected = false;
            return false;
        }
    }

    private IntPtr GetModuleBaseAddress(IntPtr hProcess, int pid)
    {
        // Method 1: EnumProcessModules (reliable, requires PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)
        try
        {
            IntPtr[] modules = new IntPtr[1024];
            if (EnumProcessModules(hProcess, modules, (uint)(modules.Length * IntPtr.Size), out uint needed))
            {
                int count = (int)(needed / IntPtr.Size);
                var sb = new StringBuilder(260);
                GetModuleFileNameExW(hProcess, modules[0], sb, 260);
                Debug.WriteLine($"[RBR] EnumProcessModules: {count} modules, main={Path.GetFileName(sb.ToString())} @ 0x{modules[0].ToInt64():X8}");
                return modules[0];
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RBR] EnumProcessModules exception: {ex.Message}");
        }

        // Method 2: .NET Process.MainModule (may fail if elevation mismatch)
        try
        {
            var proc = Process.GetProcessById(pid);
            var baseAddr = proc.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (baseAddr != IntPtr.Zero)
                return baseAddr;
        }
        catch { }

        // Method 3: Hardcoded fallback — RBR is a 32-bit game without ASLR, typically loads at 0x400000
        Debug.WriteLine($"[RBR] Warning: Could not get module base address, falling back to 0x400000");
        return new IntPtr(0x400000);
    }

    private bool ReadUInt32(IntPtr address, out uint value)
    {
        value = 0;
        if (!_isConnected || address == IntPtr.Zero) return false;

        byte[] buffer = new byte[4];
        if (ReadProcessMemory(_processHandle, address, buffer, 4, out _))
        {
            value = BitConverter.ToUInt32(buffer, 0);
            return true;
        }
        return false;
    }

    private bool ReadFloat(IntPtr address, out float value)
    {
        value = 0f;
        if (!_isConnected || address == IntPtr.Zero) return false;

        byte[] buffer = new byte[4];
        if (ReadProcessMemory(_processHandle, address, buffer, 4, out _))
        {
            value = BitConverter.ToSingle(buffer, 0);
            return true;
        }
        return false;
    }

    public bool TryReadWheelSpeeds(out WheelSpeeds wheelSpeeds)
    {
        wheelSpeeds = default;

        if (!_isConnected && !TryConnect())
            return false;

        try
        {
            // Pointer chain: [base_address + 0x493038] -> +1032 -> +64 -> wheel speeds
            // Mirrors Python: num5 = read_int(base_address + 4796472); num5 = read_int(num5+1032); num5 = read_int(num5+64)
            IntPtr addr1 = new(_baseAddress.ToInt64() + BaseOffset1);
            if (!ReadUInt32(addr1, out uint ptr1) || ptr1 == 0)
            {
                _isConnected = false;
                return false;
            }

            IntPtr addr2 = new((long)ptr1 + Offset1032);
            if (!ReadUInt32(addr2, out uint ptr2) || ptr2 == 0)
                return false;

            IntPtr addr3 = new((long)ptr2 + Offset64);
            if (!ReadUInt32(addr3, out uint ptr3) || ptr3 == 0)
                return false;

            IntPtr baseWheelAddr = new((long)ptr3);

            if (!ReadFloat(baseWheelAddr + WheelSpeedFL, out float fl)) return false;
            if (!ReadFloat(baseWheelAddr + WheelSpeedFR, out float fr)) return false;
            if (!ReadFloat(baseWheelAddr + WheelSpeedRL, out float rl)) return false;
            if (!ReadFloat(baseWheelAddr + WheelSpeedRR, out float rr)) return false;

            // Sanity check: wheel speed must be a real number within physically possible range
            bool invalid = float.IsNaN(fl) || float.IsNaN(fr) || float.IsNaN(rl) || float.IsNaN(rr)
                        || float.IsInfinity(fl) || float.IsInfinity(fr) || float.IsInfinity(rl) || float.IsInfinity(rr)
                        || MathF.Abs(fl) > MaxWheelSpeedMs || MathF.Abs(fr) > MaxWheelSpeedMs
                        || MathF.Abs(rl) > MaxWheelSpeedMs || MathF.Abs(rr) > MaxWheelSpeedMs;

            if (invalid)
                return false;

            // Convert m/s -> km/h (matching Python: * 3.6)
            wheelSpeeds = new WheelSpeeds(fl * 3.6f, fr * 3.6f, rl * 3.6f, rr * 3.6f);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RBR] TryReadWheelSpeeds exception: {ex.Message}");
            _isConnected = false;
            return false;
        }
    }

    public void Dispose()
    {
        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        _isConnected = false;
    }
}
