namespace DDRuntimeLoader;

internal sealed class ProcessEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);

    private ProcessEnvironmentScope(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            _previousValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static ProcessEnvironmentScope Apply(IReadOnlyDictionary<string, string> values) => new(values);

    public void Dispose()
    {
        foreach (var (key, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
