namespace DDRuntimeLoader;

internal static class DsonHash
{
    public static uint HashName(string value)
    {
        var hash = 0u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            unchecked
            {
                hash = hash * 53u + b;
            }
        }

        return hash;
    }

    public static int HashNameSigned(string value)
    {
        return unchecked((int)HashName(value));
    }
}
