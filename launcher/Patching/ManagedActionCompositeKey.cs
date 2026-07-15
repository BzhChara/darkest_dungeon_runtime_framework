using System.Text;

namespace DDRuntimeLoader;

internal static class ManagedActionCompositeKey
{
    public static string Build(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var value = part ?? string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }

        return builder.ToString();
    }
}
