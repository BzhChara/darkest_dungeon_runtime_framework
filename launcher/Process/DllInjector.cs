namespace DDRuntimeLoader;

internal static class DllInjector
{
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint INFINITE = 0xFFFFFFFF;

    public static void Inject(int processId, string dllPath)
    {
        dllPath = Path.GetFullPath(dllPath);
        var dllBytes = Encoding.Unicode.GetBytes(dllPath + "\0");

        var process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE,
            false,
            processId);
        if (process == IntPtr.Zero) ThrowLastWin32("OpenProcess failed");

        var remoteMemory = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            remoteMemory = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)dllBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteMemory == IntPtr.Zero) ThrowLastWin32("VirtualAllocEx failed");

            if (!WriteProcessMemory(process, remoteMemory, dllBytes, (UIntPtr)dllBytes.Length, out var bytesWritten) || bytesWritten.ToUInt64() != (ulong)dllBytes.Length)
                ThrowLastWin32("WriteProcessMemory failed");

            var kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == IntPtr.Zero) ThrowLastWin32("GetModuleHandle(kernel32.dll) failed");

            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero) ThrowLastWin32("GetProcAddress(LoadLibraryW) failed");

            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remoteMemory, 0, out _);
            if (thread == IntPtr.Zero) ThrowLastWin32("CreateRemoteThread failed");

            var wait = WaitForSingleObject(thread, INFINITE);
            if (wait != WAIT_OBJECT_0) throw new Win32Exception($"WaitForSingleObject returned 0x{wait:x8}.");

            if (!GetExitCodeThread(thread, out var exitCode)) ThrowLastWin32("GetExitCodeThread failed");
            if (exitCode == 0) throw new InvalidOperationException("LoadLibraryW returned NULL in the target process.");
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (remoteMemory != IntPtr.Zero) VirtualFreeEx(process, remoteMemory, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    private static void ThrowLastWin32(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
