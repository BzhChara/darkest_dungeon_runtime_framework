namespace DDRuntimeLoader;

internal static class PeArchitecture
{
    public static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        stream.Seek(0x3C, SeekOrigin.Begin);
        var peOffset = reader.ReadInt32();
        stream.Seek(peOffset + 4, SeekOrigin.Begin);
        var machine = reader.ReadUInt16();
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            _ => $"unknown-0x{machine:x4}"
        };
    }
}
