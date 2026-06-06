namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static IReadOnlyList<SaveStatePersistFileFacts> BuildPersistFileFacts(
            IReadOnlyList<SaveStateFileReport> files)
        {
            return files
                .OrderBy(file => ClassifyPersistFile(file.FileName).Priority)
                .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(BuildPersistFileFacts)
                .ToArray();
        }

        private static SaveStatePersistFileFacts BuildPersistFileFacts(SaveStateFileReport file)
        {
            var classification = ClassifyPersistFile(file.FileName);
            var scalars = GetDsonScalars(file);
            var rootChildIds = MergeAllDirectChildIds(
                ExtractAllDirectChildIds(file.DsonObjectPaths, "base_root"),
                ExtractAllDirectChildIds(scalars, "base_root"));

            return new SaveStatePersistFileFacts(
                file.FileName,
                classification.Category,
                classification.ModRelevance,
                file.Exists,
                file.Format,
                file.ParseStatus,
                scalars.Count,
                file.DsonObjectPaths.Count,
                rootChildIds.Count,
                rootChildIds,
                scalars
                    .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(scalar => new SaveStatePersistScalarFieldFacts(
                        scalar.Path,
                        scalar.Name,
                        scalar.Type,
                        scalar.Value))
                    .ToArray());
        }
    }
}
