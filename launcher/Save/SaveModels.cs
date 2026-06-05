namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private sealed record SaveStateReport(
        int Version,
        string SessionId,
        DateTimeOffset GeneratedAt,
        string ParseStatus,
        string Notes,
        ActiveProfileInference ActiveProfile,
        SaveStateFacts Facts,
        IReadOnlyList<string> CandidateFiles,
        IReadOnlyList<SaveStateFileReport> Files,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveStateFileReport(
        string FileName,
        string Path,
        bool Exists,
        long? Length,
        DateTime? LastWriteUtc,
        string? Sha256,
        string Format,
        string ParseStatus,
        string? BinaryHeaderHex,
        int? BinaryStringCount,
        int? BinaryStringIndexOffset,
        int? BinaryStringDataOffset,
        SaveStateDsonSummary? DsonSummary,
        IReadOnlyList<SaveStateDsonScalar> DsonScalars,
        [property: JsonIgnore] IReadOnlyList<SaveStateDsonScalar> AllDsonScalars,
        IReadOnlyList<string> DsonObjectPaths,
        IReadOnlyList<SaveStateHeroFacts> Heroes,
        IReadOnlyList<string> JsonTopLevelKeys,
        IReadOnlyList<string> MarkerStrings,
        IReadOnlyList<SaveStateValueCandidate> ValueCandidates,
        IReadOnlyList<string> StringSamples,
        IReadOnlyList<SaveStateBinaryString> BinaryStrings,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapReport(
        int Version,
        string SessionId,
        DateTimeOffset GeneratedAt,
        ActiveProfileInference ActiveProfile,
        string ActiveRoot,
        IReadOnlyList<string> CandidateFiles,
        IReadOnlyList<SaveFileMapEntry> Files,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapEntry(
        string FileName,
        string RelativePath,
        string Area,
        string Path,
        bool Exists,
        long? Length,
        DateTime? LastWriteUtc,
        string? Sha256,
        string Format,
        string ParseStatus,
        bool CandidateFile,
        int Priority,
        string Category,
        string ModRelevance,
        string Coverage,
        SaveStateDsonSummary? DsonSummary,
        IReadOnlyList<string> MarkerStrings,
        IReadOnlyList<string> ValueCandidateKeys,
        int DsonScalarSampleCount,
        int DsonObjectPathCount,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapClassification(
        int Priority,
        string Category,
        string ModRelevance);

    private sealed record SaveFileMapSource(
        string Path,
        string FileName,
        string RelativePath,
        string Area);

    private sealed record SaveStateFacts(
        SaveStateCampaignFacts Campaign,
        SaveStateProgressionFacts Progression,
        IReadOnlyDictionary<string, int> Wallet,
        SaveStateUpgradeFacts Upgrades,
        IReadOnlyList<string> BuildingIds,
        IReadOnlyList<string> HeroIds,
        IReadOnlyList<SaveStateHeroFacts> Heroes);

    private sealed record SaveStateUpgradeFacts(
        int? Version,
        int PurchaseCount,
        int PurchasedCount,
        int UnpurchasedCount,
        int UnknownPurchaseStateCount,
        int TreeCount,
        int DefinitionSourceFileCount,
        int DefinitionTreeCount,
        int NameCandidateCount,
        int MappedTreeCount,
        int UnmappedTreeCount,
        int AmbiguousTreeCount,
        IReadOnlyList<SaveStateUpgradePurchaseFacts> Purchases,
        IReadOnlyList<SaveStateUpgradeTreeFacts> Trees);

    private sealed record SaveStateUpgradePurchaseFacts(
        int Index,
        int? InstanceNumber,
        uint? TreeId,
        string? TreeName,
        bool TreeNameAmbiguous,
        string? DefinitionSource,
        IReadOnlyList<string> TreeTags,
        SaveStateUpgradeRequirementDefinitionFacts? RequirementDefinition,
        string? RequirementCode,
        bool? IsPurchased);

    private sealed record SaveStateUpgradeTreeFacts(
        uint TreeId,
        string? TreeName,
        bool TreeNameAmbiguous,
        string? DefinitionSource,
        bool? IsInstanced,
        IReadOnlyList<string> Tags,
        IReadOnlyList<SaveStateUpgradeRequirementDefinitionFacts> DefinedRequirements,
        int PurchaseCount,
        int PurchasedCount,
        int UnpurchasedCount,
        IReadOnlyList<int> InstanceNumbers,
        IReadOnlyList<string> RequirementCodes,
        IReadOnlyList<string> PurchasedRequirementCodes);

    private sealed record SaveStateUpgradeRequirementDefinitionFacts(
        string Code,
        IReadOnlyDictionary<string, int> CurrencyCost,
        int? PrerequisiteResolveLevel,
        IReadOnlyList<SaveStateUpgradePrerequisiteDefinitionFacts> Prerequisites);

    private sealed record SaveStateUpgradePrerequisiteDefinitionFacts(
        string TreeId,
        string RequirementCode);

    private sealed record SaveStateHeroFacts(
        string Id,
        string? Name,
        string? HeroClass,
        int? RosterStatus,
        int? ResolveXp,
        double? CurrentHp,
        double? Stress,
        int? WeaponRank,
        int? ArmourRank,
        bool? BackerHero,
        int RawDataLength,
        int NestedObjectCount,
        int NestedFieldCount,
        IReadOnlyList<string> QuirkIds,
        IReadOnlyList<string> CombatSkillIds,
        IReadOnlyList<string> CampingSkillIds,
        IReadOnlyList<string> TrinketIds);

    private sealed record SaveStateCampaignFacts(
        int? Version,
        double? TotalElapsed,
        bool? InRaid,
        string? RaidDungeon,
        string? EstateName,
        string? GameMode,
        string? DateTime,
        string? TownEvents,
        string? NeverAgain);

    private sealed record SaveStateProgressionFacts(
        int? TotalQuestsFinished,
        int? TotalSuccessfulQuestsFinished,
        int? LastQuestPlayedId,
        int? LastQuestPlayedXp,
        bool? LastRaidSuccess,
        bool? LastRaidWasPlotQuest);

    private sealed record SaveStateDsonSummary(
        int HeaderLength,
        int ObjectCount,
        int FieldCount,
        int DataLength,
        int DataOffset,
        int ParsedScalarCount,
        int RawScalarCount);

    private sealed record SaveStateDsonScalar(
        string Path,
        string Name,
        string Type,
        string? Value,
        int Offset,
        int Size,
        string? RawHex);

    private sealed record SaveStateValueCandidate(
        string Key,
        string Value,
        int Offset,
        int? StringIndex,
        string Confidence);

    private readonly record struct SaveStateBinaryString(
        int Offset,
        string Value,
        int? StringIndex,
        uint? Hash,
        uint? Metadata,
        int? RelativeOffset);

    private sealed class StableSaveChangeGroupComparer : IEqualityComparer<(string ProfileRoot, string Profile)>
    {
        public static readonly StableSaveChangeGroupComparer Instance = new();

        public bool Equals((string ProfileRoot, string Profile) x, (string ProfileRoot, string Profile) y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x.ProfileRoot, y.ProfileRoot)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Profile, y.Profile);
        }

        public int GetHashCode((string ProfileRoot, string Profile) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProfileRoot),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Profile));
        }
    }
}
