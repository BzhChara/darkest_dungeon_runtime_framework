namespace DDRuntimeLoader;

internal sealed class LauncherLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();

    private LauncherLog(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public static LauncherLog Open(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        return new LauncherLog(Path.Combine(logDirectory, "launcher.log"));
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (_sync)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
