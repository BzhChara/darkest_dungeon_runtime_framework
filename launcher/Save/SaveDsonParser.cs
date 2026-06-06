namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static BinaryContainerInfo? TryParseBinaryContainer(byte[] bytes, List<string> accessIssues)
        {
            return TryParseDsonContainer(bytes, 0, bytes.Length, accessIssues);
        }

        private static BinaryContainerInfo? TryParseDsonContainer(byte[] bytes, int baseOffset, int length, List<string> accessIssues)
        {
            if (baseOffset < 0
                || length < 0
                || baseOffset > bytes.Length
                || bytes.Length - baseOffset < length
                || length < 0x40)
            {
                return null;
            }

            var endOffset = baseOffset + length;
            var magic = ReadUInt32LittleEndian(bytes, baseOffset);
            var headerLength = ReadInt32LittleEndian(bytes, baseOffset + 0x08);
            var meta1Size = ReadInt32LittleEndian(bytes, baseOffset + 0x10);
            var objectCount = ReadInt32LittleEndian(bytes, baseOffset + 0x14);
            var meta1OffsetRelative = ReadInt32LittleEndian(bytes, baseOffset + 0x18);
            var stringCountRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x2C);
            var stringIndexOffsetRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x30);
            var dataLength = ReadInt32LittleEndian(bytes, baseOffset + 0x38);
            var stringDataOffsetRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x3C);
            if (magic != 0x0000B101
                || headerLength != 0x40
                || objectCount < 0
                || meta1OffsetRelative < 0
                || meta1Size < 0
                || dataLength < 0
                || stringCountRaw > 100_000
                || stringCountRaw > int.MaxValue
                || stringIndexOffsetRaw > int.MaxValue
                || stringDataOffsetRaw > int.MaxValue)
            {
                return null;
            }

            var stringCount = (int)stringCountRaw;
            var stringIndexOffsetRelative = (int)stringIndexOffsetRaw;
            var stringDataOffsetRelative = (int)stringDataOffsetRaw;
            var meta1Offset = baseOffset + meta1OffsetRelative;
            var stringIndexOffset = baseOffset + stringIndexOffsetRelative;
            var stringDataOffset = baseOffset + stringDataOffsetRelative;
            var objectTableSize = (long)objectCount * 16L;
            var meta1End = (long)meta1Offset + objectTableSize;
            var stringIndexEnd = (long)stringIndexOffset + (long)stringCount * 12L;
            var stringDataEnd = (long)stringDataOffset + dataLength;
            if (meta1OffsetRelative > length
                || stringIndexOffsetRelative > length
                || stringDataOffsetRelative > length
                || meta1End > endOffset
                || meta1Size < objectTableSize
                || stringIndexEnd > endOffset
                || stringDataEnd > endOffset
                || stringIndexEnd != stringDataOffset)
            {
                return null;
            }

            var objectEntries = ReadDsonObjectEntries(bytes, objectCount, meta1Offset);
            var fieldEntries = ReadDsonFieldEntries(bytes, stringCount, stringIndexOffset, stringDataOffset);
            var dsonObjectPaths = BuildDsonObjectPaths(objectEntries, fieldEntries);
            var dsonScalars = ExtractDsonScalars(bytes, fieldEntries, objectEntries, dsonObjectPaths, stringDataOffset, dataLength);
            var strings = new List<SaveStateBinaryString>();
            foreach (var field in fieldEntries)
            {
                if (field.AbsoluteOffset < baseOffset || field.AbsoluteOffset >= endOffset)
                {
                    accessIssues.Add($"DSON field {field.Index} points outside file: absoluteOffset={field.AbsoluteOffset}");
                    continue;
                }

                strings.Add(new SaveStateBinaryString(
                    field.AbsoluteOffset,
                    field.Name,
                    field.Index,
                    field.Hash,
                    field.Metadata,
                    field.RelativeOffset));
            }

            var dsonSummary = new SaveStateDsonSummary(
                headerLength,
                objectCount,
                stringCount,
                dataLength,
                stringDataOffsetRelative,
                dsonScalars.Count(scalar => !scalar.Type.Equals("raw", StringComparison.OrdinalIgnoreCase)),
                dsonScalars.Count(scalar => scalar.Type.Equals("raw", StringComparison.OrdinalIgnoreCase)));

            return new BinaryContainerInfo(
                stringCount,
                stringIndexOffsetRelative,
                stringDataOffsetRelative,
                strings,
                dsonSummary,
                dsonScalars,
                dsonObjectPaths);
        }

        private static IReadOnlyList<DsonObjectEntry> ReadDsonObjectEntries(byte[] bytes, int count, int offset)
        {
            var entries = new List<DsonObjectEntry>();
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + i * 16;
                entries.Add(new DsonObjectEntry(
                    i,
                    ReadInt32LittleEndian(bytes, entryOffset),
                    ReadInt32LittleEndian(bytes, entryOffset + 4),
                    ReadInt32LittleEndian(bytes, entryOffset + 8),
                    ReadInt32LittleEndian(bytes, entryOffset + 12)));
            }

            return entries;
        }

        private static IReadOnlyList<DsonFieldEntry> ReadDsonFieldEntries(byte[] bytes, int count, int offset, int dataOffset)
        {
            var entries = new List<DsonFieldEntry>();
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + i * 12;
                var hash = ReadUInt32LittleEndian(bytes, entryOffset);
                var relativeOffset = (int)ReadUInt32LittleEndian(bytes, entryOffset + 4);
                var metadata = ReadUInt32LittleEndian(bytes, entryOffset + 8);
                var nameLength = (int)((metadata & 0x7FC) >> 2);
                var absoluteOffset = dataOffset + relativeOffset;
                var name = nameLength > 0 && absoluteOffset + nameLength <= bytes.Length
                    ? ReadNullTerminatedUtf8(bytes, absoluteOffset, nameLength)
                    : string.Empty;
                entries.Add(new DsonFieldEntry(
                    i,
                    name,
                    relativeOffset,
                    absoluteOffset,
                    nameLength,
                    hash,
                    metadata,
                    (metadata & 1) != 0));
            }

            return entries;
        }

        private static IReadOnlyDictionary<int, string> BuildDsonObjectPaths(
            IReadOnlyList<DsonObjectEntry> objectEntries,
            IReadOnlyList<DsonFieldEntry> fieldEntries)
        {
            var fieldsByIndex = fieldEntries.ToDictionary(field => field.Index);
            var objectsByIndex = objectEntries.ToDictionary(entry => entry.ObjectIndex);
            var pathsByMeta2Index = new Dictionary<int, string>();

            foreach (var entry in objectEntries.OrderBy(entry => entry.ObjectIndex))
            {
                if (!fieldsByIndex.TryGetValue(entry.Meta2Index, out var field))
                {
                    continue;
                }

                if (entry.ParentObjectIndex < 0
                    || !objectsByIndex.TryGetValue(entry.ParentObjectIndex, out var parentEntry)
                    || !pathsByMeta2Index.TryGetValue(parentEntry.Meta2Index, out var parentPath))
                {
                    pathsByMeta2Index[entry.Meta2Index] = field.Name;
                    continue;
                }

                pathsByMeta2Index[entry.Meta2Index] = $"{parentPath}.{field.Name}";
            }

            return pathsByMeta2Index;
        }

        private static IReadOnlyList<SaveStateDsonScalar> ExtractDsonScalars(
            byte[] bytes,
            IReadOnlyList<DsonFieldEntry> fields,
            IReadOnlyList<DsonObjectEntry> objects,
            IReadOnlyDictionary<int, string> objectPaths,
            int dataOffset,
            int dataLength)
        {
            var scalars = new List<SaveStateDsonScalar>();
            var orderedFields = fields.OrderBy(field => field.Index).ToArray();
            for (var i = 0; i < orderedFields.Length; i++)
            {
                var field = orderedFields[i];
                if (field.IsObject)
                {
                    continue;
                }

                var nextRelativeOffset = i + 1 < orderedFields.Length
                    ? orderedFields[i + 1].RelativeOffset
                    : dataLength;
                var endOffset = dataOffset + nextRelativeOffset;
                var nameEnd = field.AbsoluteOffset + field.NameLength;
                if (endOffset < nameEnd || nameEnd > bytes.Length)
                {
                    continue;
                }

                var path = BuildDsonFieldPath(field, objects, objectPaths);
                var size = endOffset - field.AbsoluteOffset;
                var rawHex = ToHex(bytes.Skip(nameEnd).Take(Math.Min(16, Math.Max(0, endOffset - nameEnd))));
                var valueStart = Align4(nameEnd);
                var remaining = endOffset - nameEnd;
                var alignedRemaining = endOffset - valueStart;

                if (remaining == 1 && bytes[nameEnd] is 0 or 1)
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "bool", bytes[nameEnd] == 0 ? "false" : "true", field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (remaining == 1
                    && SingleByteStringFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                    && bytes[nameEnd] is >= 32 and <= 126)
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "string", ((char)bytes[nameEnd]).ToString(), field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (IsDsonVectorPath(path, IntVectorPathPatterns)
                    && TryReadDsonIntVector(bytes, valueStart, alignedRemaining, out var intVector))
                {
                    scalars.Add(new SaveStateDsonScalar(
                        path,
                        field.Name,
                        "intVector",
                        JsonSerializer.Serialize(intVector),
                        field.AbsoluteOffset,
                        size,
                        rawHex));
                    continue;
                }

                if (IsDsonVectorPath(path, StringVectorPathPatterns)
                    && TryReadDsonStringVector(bytes, valueStart, alignedRemaining, out var stringVector))
                {
                    scalars.Add(new SaveStateDsonScalar(
                        path,
                        field.Name,
                        "stringVector",
                        JsonSerializer.Serialize(stringVector),
                        field.AbsoluteOffset,
                        size,
                        rawHex));
                    continue;
                }

                if (alignedRemaining >= 4 && TryReadDsonString(bytes, valueStart, alignedRemaining, out var stringValue))
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "string", stringValue, field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (alignedRemaining == 4)
                {
                    if (FloatFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "float32",
                            BitConverter.ToSingle(bytes, valueStart).ToString("R", CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }
                    else if (UInt32FieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "uint32",
                            ReadUInt32LittleEndian(bytes, valueStart).ToString(CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }
                    else
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "int32",
                            ReadInt32LittleEndian(bytes, valueStart).ToString(CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }

                    continue;
                }

                scalars.Add(new SaveStateDsonScalar(path, field.Name, "raw", null, field.AbsoluteOffset, size, rawHex));
            }

            return scalars;
        }

        private static bool IsDsonVectorPath(string path, IReadOnlyList<string[]> patterns)
        {
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pattern in patterns)
            {
                if (pattern.Length > segments.Length)
                {
                    continue;
                }

                var matches = true;
                for (var i = 0; i < pattern.Length; i++)
                {
                    var expected = pattern[pattern.Length - 1 - i];
                    var actual = segments[segments.Length - 1 - i];
                    if (!expected.Equals("*", StringComparison.Ordinal)
                        && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadDsonIntVector(
            byte[] bytes,
            int offset,
            int remaining,
            out IReadOnlyList<int> values)
        {
            values = [];
            if (remaining < 4 || offset < 0 || offset + remaining > bytes.Length)
            {
                return false;
            }

            var count = ReadInt32LittleEndian(bytes, offset);
            if (count < 0 || count > 100_000 || remaining != (count + 1) * 4)
            {
                return false;
            }

            var result = new int[count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = ReadInt32LittleEndian(bytes, offset + 4 + i * 4);
            }

            values = result;
            return true;
        }

        private static bool TryReadDsonStringVector(
            byte[] bytes,
            int offset,
            int remaining,
            out IReadOnlyList<string> values)
        {
            values = [];
            if (remaining < 4 || offset < 0 || offset + remaining > bytes.Length)
            {
                return false;
            }

            var count = ReadInt32LittleEndian(bytes, offset);
            if (count < 0 || count > 100_000)
            {
                return false;
            }

            var cursor = offset + 4;
            var end = offset + remaining;
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                if (cursor + 4 > end)
                {
                    return false;
                }

                var length = ReadInt32LittleEndian(bytes, cursor);
                cursor += 4;
                if (length < 1 || cursor + length > end || bytes[cursor + length - 1] != 0)
                {
                    return false;
                }

                try
                {
                    result.Add(StrictUtf8.GetString(bytes, cursor, length - 1));
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }

                cursor += length;
                if (i < count - 1)
                {
                    cursor += (4 - ((cursor - (offset + 4)) % 4)) % 4;
                }
            }

            if (cursor != end)
            {
                return false;
            }

            values = result;
            return true;
        }

        private static string BuildDsonFieldPath(
            DsonFieldEntry field,
            IReadOnlyList<DsonObjectEntry> objects,
            IReadOnlyDictionary<int, string> objectPaths)
        {
            var parent = objects
                .Where(entry => entry.Meta2Index < field.Index && field.Index <= entry.Meta2Index + entry.AllChildCount)
                .OrderByDescending(entry => entry.Meta2Index)
                .FirstOrDefault();

            return parent is not null && objectPaths.TryGetValue(parent.Meta2Index, out var parentPath)
                ? $"{parentPath}.{field.Name}"
                : field.Name;
        }

        private static bool TryReadDsonString(byte[] bytes, int offset, int remaining, out string value)
        {
            value = string.Empty;
            if (remaining < 5)
            {
                return false;
            }

            var length = ReadInt32LittleEndian(bytes, offset);
            if (length < 1 || length != remaining - 4 || offset + 4 + length > bytes.Length || bytes[offset + 4 + length - 1] != 0)
            {
                return false;
            }

            try
            {
                value = StrictUtf8.GetString(bytes, offset + 4, length - 1);
                return true;
            }
            catch (DecoderFallbackException)
            {
                value = string.Empty;
                return false;
            }
        }

        private static int Align4(int value)
        {
            return (value + 3) & ~3;
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return BitConverter.ToUInt32(bytes, offset);
        }

        private static int ReadInt32LittleEndian(byte[] bytes, int offset)
        {
            return BitConverter.ToInt32(bytes, offset);
        }

        private static string ReadNullTerminatedAscii(byte[] bytes, int offset)
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        private static string ReadNullTerminatedUtf8(byte[] bytes, int offset, int maxLength)
        {
            var length = Math.Max(0, maxLength - 1);
            return StrictUtf8.GetString(bytes, offset, length);
        }

        private static IReadOnlyList<SaveStateBinaryString> ExtractPrintableStrings(byte[] bytes)
        {
            var strings = new List<SaveStateBinaryString>();
            var builder = new StringBuilder();
            var start = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (b is >= 32 and <= 126)
                {
                    if (builder.Length == 0) start = i;
                    builder.Append((char)b);
                    continue;
                }

                FlushString(strings, builder, start);
            }

            FlushString(strings, builder, start);
            return strings;
        }

        private static void FlushString(List<SaveStateBinaryString> strings, StringBuilder builder, int start)
        {
            if (builder.Length >= 4)
            {
                strings.Add(new SaveStateBinaryString(start, builder.ToString(), null, null, null, null));
            }

            builder.Clear();
        }

        private static IReadOnlyList<SaveStateValueCandidate> ExtractValueCandidates(
            IReadOnlyList<SaveStateBinaryString> strings,
            IReadOnlyList<SaveStateBinaryString> printableStrings)
        {
            var keys = ValueCandidateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<SaveStateValueCandidate>();
            for (var i = 0; i < strings.Count; i++)
            {
                var key = strings[i].Value;
                if (!keys.Contains(key)) continue;

                var keyEnd = strings[i].Offset + key.Length + 1;
                var value = printableStrings
                    .Where(item => item.Offset >= keyEnd && item.Offset - keyEnd <= MaxInlineValueDistance)
                    .OrderBy(item => item.Offset)
                    .FirstOrDefault(item => IsLikelyScalarCandidate(item.Value, keys));
                if (string.IsNullOrWhiteSpace(value.Value)) continue;

                candidates.Add(new SaveStateValueCandidate(key, value.Value, value.Offset, value.StringIndex, "inlineString"));
                if (candidates.Count >= 80) break;
            }

            return candidates;
        }

        private static bool IsLikelyScalarCandidate(string? value, HashSet<string> keys)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (keys.Contains(value)) return false;
            if (KnownMarkers.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
            return value.All(ch => ch is >= ' ' and <= '~');
        }

        private static string ToHex(IEnumerable<byte> bytes)
        {
            return string.Join(' ', bytes.Select(value => value.ToString("X2")));
        }

        private sealed record BinaryContainerInfo(
            int StringCount,
            int StringIndexOffset,
            int StringDataOffset,
            IReadOnlyList<SaveStateBinaryString> Strings,
            SaveStateDsonSummary DsonSummary,
            IReadOnlyList<SaveStateDsonScalar> DsonScalars,
            IReadOnlyDictionary<int, string> DsonObjectPathsByMeta2Index)
        {
            public IReadOnlyList<string> DsonObjectPaths { get; } = DsonObjectPathsByMeta2Index
                .Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private sealed record DsonObjectEntry(
            int ObjectIndex,
            int ParentObjectIndex,
            int Meta2Index,
            int DirectChildCount,
            int AllChildCount);

        private sealed record DsonFieldEntry(
            int Index,
            string Name,
            int RelativeOffset,
            int AbsoluteOffset,
            int NameLength,
            uint Hash,
            uint Metadata,
            bool IsObject);
    }
}
