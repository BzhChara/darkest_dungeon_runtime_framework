namespace DDRuntimeLoader;

internal sealed class SuspendedProcess : IDisposable
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint STILL_ACTIVE = 259;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private bool _resumed;
    private bool _terminated;

    private SuspendedProcess(IntPtr processHandle, IntPtr threadHandle, int processId)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public static SuspendedProcess Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startupInfo = new STARTUPINFO
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFO>()
        };

        var commandLine = new StringBuilder($"\"{executablePath}\"");
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            commandLine.Append(' ');
            commandLine.Append(QuoteCommandLineArgument(argument));
        }

        if (!CreateProcessW(
                executablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_SUSPENDED,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed");
        }

        return new SuspendedProcess(
            processInformation.hProcess,
            processInformation.hThread,
            (int)processInformation.dwProcessId);
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!argument.Any(ch => char.IsWhiteSpace(ch) || ch is '"'))
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(ch);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    public void Resume()
    {
        if (_resumed || _terminated) return;

        var result = ResumeThread(_threadHandle);
        if (result == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");

        _resumed = true;
    }

    public void Terminate(uint exitCode)
    {
        if (_terminated) return;

        if (!TerminateProcess(_processHandle, exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateProcess failed");

        _terminated = true;
    }

    public void Dispose()
    {
        if (!_resumed && !_terminated && _processHandle != IntPtr.Zero)
        {
            if (GetExitCodeProcess(_processHandle, out var exitCode) && exitCode == STILL_ACTIVE)
            {
                TerminateProcess(_processHandle, 3);
            }
        }

        if (_threadHandle != IntPtr.Zero)
        {
            CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }

        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
