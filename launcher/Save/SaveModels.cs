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
        IReadOnlyList<SaveStatePersistFileFacts> PersistFiles,
        SaveStateCampaignFacts Campaign,
        SaveStateProgressionFacts Progression,
        SaveStateQuestFacts Quest,
        SaveStateTownEventFacts TownEvents,
        SaveStateNarrationFacts Narration,
        SaveStateCampaignLogFacts CampaignLog,
        SaveStateEstateFacts Estate,
        IReadOnlyDictionary<string, int> Wallet,
        SaveStateUpgradeFacts Upgrades,
        SaveStateHeroDefinitionFacts HeroDefinitions,
        SaveStateTownFacts Town,
        IReadOnlyList<string> BuildingIds,
        IReadOnlyList<string> HeroIds,
        IReadOnlyList<SaveStateHeroLoadoutFacts> HeroLoadouts,
        IReadOnlyList<SaveStateHeroFacts> Heroes);

    private sealed record SaveStatePersistFileFacts(
        string FileName,
        string Category,
        string ModRelevance,
        bool Exists,
        string Format,
        string ParseStatus,
        int ScalarCount,
        int ObjectPathCount,
        int RootChildCount,
        IReadOnlyList<string> RootChildIds,
        IReadOnlyList<SaveStatePersistScalarFieldFacts> ScalarFields);

    private sealed record SaveStatePersistScalarFieldFacts(
        string Path,
        string Name,
        string Type,
        string? Value);

    private sealed record SaveStateEstateFacts(
        int? Version,
        int WalletItemCount,
        IReadOnlyList<SaveStateEstateItemFacts> WalletItems,
        int EstateItemCount,
        IReadOnlyList<SaveStateEstateItemFacts> EstateItems,
        int? EndlessWaveHighscore,
        bool? WasEndlessWaveHighscoreTampered,
        bool? PerformedBlueprintCorrectionCheck,
        bool? FoundGlobalTamperedFile,
        bool? FoundLocalTamperedFile,
        IReadOnlyList<string> TrinketRootIds,
        IReadOnlyList<string> DarkestDungeonTrinketUnlockIds);

    private sealed record SaveStateEstateItemFacts(
        string SlotId,
        string? Type,
        string? Id,
        int? Amount);

    private sealed record SaveStateQuestFacts(
        int? Version,
        int? PlotQuestTotal,
        int QuestCount,
        int PlotQuestCount,
        int TownEventQuestCount,
        IReadOnlyList<SaveStateQuestEntryFacts> Quests);

    private sealed record SaveStateQuestEntryFacts(
        string SlotId,
        string? Id,
        string? Dungeon,
        string? Type,
        string? MapName,
        int? Difficulty,
        int? Length,
        bool? IsPlotQuest,
        bool? CountedInGeneration,
        bool? IsFromTownEvent,
        int? CompletionThreshold,
        bool? UseDefaultProgressionGoals,
        string? RaidRulesOverride,
        string? TorchSetting,
        SaveStateQuestRewardFacts CompletionReward);

    private sealed record SaveStateQuestRewardFacts(
        int? ResolveXp,
        int? ResolveXpPerWaveKill,
        int? MaxTimesDungeonXpAwarded,
        IReadOnlyList<SaveStateQuestRewardItemFacts> Items);

    private sealed record SaveStateQuestRewardItemFacts(
        string SlotId,
        string? Type,
        string? Id,
        int? Amount);

    private sealed record SaveStateTownEventFacts(
        int? Version,
        int? CurrentResultEventId,
        bool? HasUnclaimedInteraction,
        int? LastTownEventWeek,
        int? RngSeed,
        int ResultEventHistoryCount,
        int DeadHeroEntryCount,
        int BonusHeroEntryCount,
        int EventCostCount,
        int FreeUpgradeTagCount,
        int NonRolledAdditionalChanceCount,
        IReadOnlyList<string> ResultEventHistoryIds,
        IReadOnlyList<string> DeadHeroEntryIds,
        IReadOnlyList<string> BonusHeroEntryIds,
        IReadOnlyList<string> EventCostIds,
        IReadOnlyList<string> FreeUpgradeTags,
        IReadOnlyList<string> NonRolledAdditionalChanceIds);

    private sealed record SaveStateNarrationFacts(
        int? Version,
        int LogCount,
        int EntryCount,
        int CampaignEntryCount,
        int RaidEntryCount,
        int TownVisitEntryCount,
        int TotalPlaybackCount,
        int DistinctEntryTypeCount,
        int DistinctAudioEventTypeCount,
        IReadOnlyList<SaveStateNarrationLogFacts> Logs,
        IReadOnlyList<SaveStateNarrationSummaryFacts> EntryTypeCounts,
        IReadOnlyList<SaveStateNarrationSummaryFacts> AudioEventTypeCounts);

    private sealed record SaveStateNarrationLogFacts(
        string LogId,
        string SourcePath,
        int EntryCount,
        int TotalPlaybackCount,
        IReadOnlyList<SaveStateNarrationEntryFacts> Entries);

    private sealed record SaveStateNarrationEntryFacts(
        string LogId,
        string SlotId,
        string? EntryType,
        string? AudioEventType,
        int? Count);

    private sealed record SaveStateNarrationSummaryFacts(
        string Key,
        int EntryCount,
        int TotalPlaybackCount);

    private sealed record SaveStateCampaignLogFacts(
        int? Version,
        int? TotalWeeks,
        int ChapterCount,
        int EntryCount,
        int HeroRosterEntryCount,
        int PartyEntryCount,
        int DungeonEntryCount,
        IReadOnlyList<SaveStateCampaignLogChapterFacts> Chapters);

    private sealed record SaveStateCampaignLogChapterFacts(
        string ChapterSlotId,
        int? ChapterIndex,
        int EntryCount,
        IReadOnlyList<SaveStateCampaignLogEntryFacts> Entries);

    private sealed record SaveStateCampaignLogEntryFacts(
        string SlotId,
        int? Rtti,
        string EntryKind,
        string? Name,
        int? Guid,
        int? ClassHash,
        int? Level,
        int? DungeonId,
        int HeroCount,
        IReadOnlyList<SaveStateCampaignLogHeroFacts> Heroes,
        IReadOnlyList<SaveStateCampaignLogScalarFacts> ExtraScalarFields);

    private sealed record SaveStateCampaignLogHeroFacts(
        string SlotId,
        string? Name,
        int? Guid,
        int? ClassHash,
        bool? Died);

    private sealed record SaveStateCampaignLogScalarFacts(
        string LocalPath,
        string Name,
        string Type,
        string? Value);

    private sealed record SaveStateHeroLoadoutFacts(
        string HeroId,
        string? Name,
        string? HeroClass,
        bool DefinitionFound,
        int? RosterStatus,
        int? ResolveXp,
        int? SelectedCombatSkillsMax,
        IReadOnlyList<string> SelectedCombatSkillIds,
        IReadOnlyList<string> AllCombatSkillIds,
        IReadOnlyList<string> UnselectedCombatSkillIds,
        IReadOnlyList<string> UnknownSelectedCombatSkillIds,
        IReadOnlyList<string> SelectedCampingSkillIds,
        IReadOnlyList<string> AllCampingSkillIds,
        IReadOnlyList<string> UnselectedCampingSkillIds,
        IReadOnlyList<string> UnknownSelectedCampingSkillIds,
        int? CurrentWeaponRank,
        int? MaxWeaponRank,
        SaveStateHeroEquipmentDefinitionFacts? CurrentWeapon,
        int? CurrentArmourRank,
        int? MaxArmourRank,
        SaveStateHeroEquipmentDefinitionFacts? CurrentArmour);

    private sealed record SaveStateHeroDefinitionFacts(
        string SourceScope,
        string GameMode,
        int SourceFileCount,
        int HeroClassCount,
        int CombatSkillCount,
        int CampingSkillCount,
        int WeaponLevelCount,
        int ArmourLevelCount,
        IReadOnlyList<SaveStateHeroClassDefinitionFacts> Classes);

    private sealed record SaveStateHeroClassDefinitionFacts(
        string HeroClass,
        string SourceRelativePath,
        int? IdIndex,
        IReadOnlyList<string> Tags,
        bool? CanSelectCombatSkills,
        int? SelectedCombatSkillsMax,
        SaveStateHeroGenerationDefinitionFacts? Generation,
        IReadOnlyList<SaveStateHeroEquipmentDefinitionFacts> Weapons,
        IReadOnlyList<SaveStateHeroEquipmentDefinitionFacts> Armours,
        IReadOnlyList<SaveStateHeroCombatSkillDefinitionFacts> CombatSkills,
        IReadOnlyList<SaveStateHeroCampingSkillDefinitionFacts> CampingSkills);

    private sealed record SaveStateHeroGenerationDefinitionFacts(
        bool? IsGenerationEnabled,
        int? PositiveQuirksMin,
        int? PositiveQuirksMax,
        int? NegativeQuirksMin,
        int? NegativeQuirksMax,
        int? ClassSpecificCampingSkills,
        int? SharedCampingSkills,
        int? RandomCombatSkills,
        int? CardsInDeck,
        double? CardChance);

    private sealed record SaveStateHeroEquipmentDefinitionFacts(
        string Kind,
        int? Level,
        string? Name,
        int? UpgradeRequirementCode,
        string? Attack,
        string? Defense,
        int? DamageMin,
        int? DamageMax,
        string? Crit,
        string? Protection,
        int? Hp,
        int? Speed);

    private sealed record SaveStateHeroCombatSkillDefinitionFacts(
        string Id,
        int LevelCount,
        bool GenerationGuaranteed,
        IReadOnlyList<SaveStateHeroCombatSkillLevelDefinitionFacts> Levels);

    private sealed record SaveStateHeroCombatSkillLevelDefinitionFacts(
        int? Level,
        string? Type,
        string? Attack,
        string? Damage,
        string? Crit,
        string? Launch,
        string? Target,
        string? Move,
        string? Heal,
        int? PerBattleLimit,
        bool? IsCritValid,
        bool? IsStallInvalidating,
        IReadOnlyList<string> Effects);

    private sealed record SaveStateHeroCampingSkillDefinitionFacts(
        string Id,
        int? Level,
        int? Cost,
        int? UseLimit,
        string SourceRelativePath,
        IReadOnlyList<string> UpgradeRequirementCodes);

    private sealed record SaveStateTownFacts(
        int? Version,
        int BuildingCount,
        int StoreCount,
        int StoreItemCount,
        int RecruitCount,
        int ActivitySlotCount,
        int OccupiedActivitySlotCount,
        int QuirkTreatmentCount,
        int DeckHistoryEntryCount,
        IReadOnlyList<SaveStateTownBuildingFacts> Buildings,
        IReadOnlyList<SaveStateTownStoreFacts> Stores,
        IReadOnlyList<SaveStateTownStoreItemFacts> StoreItems,
        IReadOnlyList<SaveStateTownRecruitFacts> Recruits,
        IReadOnlyList<SaveStateTownActivitySlotFacts> ActivitySlots,
        IReadOnlyList<SaveStateTownQuirkTreatmentFacts> QuirkTreatments,
        IReadOnlyList<SaveStateTownDeckHistoryFacts> DeckHistory);

    private sealed record SaveStateTownBuildingFacts(
        string Id,
        bool HasActivities,
        bool HasStore,
        int ActivityCount,
        int StoreCount,
        int ActivitySlotCount,
        int StoreItemCount,
        int RecruitCount,
        IReadOnlyList<string> ActivityIds,
        IReadOnlyList<string> StoreIds);

    private sealed record SaveStateTownStoreFacts(
        string BuildingId,
        string StoreId,
        int InventoryItemCount,
        int RecruitCount,
        int DeckHistoryEntryCount);

    private sealed record SaveStateTownStoreItemFacts(
        string BuildingId,
        string StoreId,
        string SlotId,
        string? ItemId,
        string? ItemType,
        int? Amount);

    private sealed record SaveStateTownRecruitFacts(
        string BuildingId,
        string StoreId,
        string RecruitId,
        string? Name,
        string? HeroClass,
        int? ResolveXp,
        double? CurrentHp,
        double? Stress,
        int? WeaponRank,
        int? ArmourRank,
        bool? Rescued,
        bool? BackerHero,
        bool? IsFromTownEvent,
        IReadOnlyList<string> QuirkIds,
        IReadOnlyList<string> CombatSkillIds,
        IReadOnlyList<string> CampingSkillIds,
        IReadOnlyList<string> TrinketIds);

    private sealed record SaveStateTownActivitySlotFacts(
        string BuildingId,
        string ActivityId,
        int SlotIndex,
        int? HeroId,
        int? VisitsRemaining,
        int? ResidentOccupied,
        bool? IsSideEffectResult,
        bool HasHero);

    private sealed record SaveStateTownQuirkTreatmentFacts(
        string BuildingId,
        string ActivityId,
        int SlotIndex,
        string QuirkBucket,
        string? QuirkId,
        int? Action);

    private sealed record SaveStateTownDeckHistoryFacts(
        string BuildingId,
        string StoreId,
        string DeckVersionId,
        string EntryId,
        int? Count);

    private sealed record SaveStateUpgradeFacts(
        int? Version,
        string SourceScope,
        string GameMode,
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
        IReadOnlyList<SaveStateUpgradeTreeFacts> Trees,
        IReadOnlyList<SaveStateUpgradeDefinitionFacts> Definitions);

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
        string? CurrentRequirementCode,
        string? NextRequirementCode,
        bool IsComplete,
        IReadOnlyList<int> InstanceNumbers,
        IReadOnlyList<string> RequirementCodes,
        IReadOnlyList<string> PurchasedRequirementCodes,
        IReadOnlyList<string> MissingRequirementCodes);

    private sealed record SaveStateUpgradeDefinitionFacts(
        uint TreeId,
        string TreeName,
        string Category,
        bool TreeNameAmbiguous,
        string DefinitionKind,
        string SourceRelativePath,
        bool? IsInstanced,
        IReadOnlyList<string> Tags,
        int RequirementCount,
        IReadOnlyList<SaveStateUpgradeRequirementDefinitionFacts> Requirements,
        bool AppearsInSave,
        int SavePurchaseCount,
        int SavePurchasedCount,
        IReadOnlyList<string> SaveRequirementCodes,
        IReadOnlyList<string> SavePurchasedRequirementCodes,
        IReadOnlyList<string> MissingRequirementCodes);

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
        int? BeforeOnStartTownVisitStatus,
        int? MissingDuration,
        int? StoryVariation,
        string? MissingFrom,
        string? BuildingName,
        int? Timestamp,
        int? ResolveXp,
        double? CurrentHp,
        double? Stress,
        int? WeaponRank,
        int? ArmourRank,
        int? ColourVariation,
        bool? BackerHero,
        bool? CombatReady,
        int? Stunned,
        bool? DeathHeartAttackCompleted,
        bool? VisitedDeathsDoor,
        int? DeathsDoorEnterEffectRoundCooldown,
        bool? HasHadHeartAttack,
        int? StepsTaken,
        int? EnemiesKilled,
        int? ProvisionsConsumed,
        int? SuccessfulDarkestDungeonQuestCount,
        bool? IsFromTownEvent,
        string? AfflictionTypeId,
        int? AfflictionSeverity,
        string? VirtueTypeId,
        int RawDataLength,
        int NestedObjectCount,
        int NestedFieldCount,
        IReadOnlyList<string> QuirkIds,
        IReadOnlyList<SaveStateHeroQuirkFacts> Quirks,
        IReadOnlyList<string> CombatSkillIds,
        IReadOnlyList<string> CampingSkillIds,
        IReadOnlyList<string> TrinketIds,
        IReadOnlyList<SaveStateHeroTrinketFacts> Trinkets);

    private sealed record SaveStateHeroQuirkFacts(
        string Id,
        bool? IsNew,
        bool? IsLocked,
        int? MissionCount,
        int? ReplacesQuirk,
        bool? ReplacesQuirkViewed,
        int? EvolutionDurationRemaining);

    private sealed record SaveStateHeroTrinketFacts(
        string SlotId,
        string? ItemId,
        string? ItemType,
        int? Amount);

    private sealed record SaveStateCampaignFacts(
        int? Version,
        double? TotalElapsed,
        bool? InRaid,
        string? RaidDungeon,
        string? RaidSave,
        string? EstateName,
        string? GameMode,
        string? DateTime,
        bool? DlcInit,
        bool? DdOptionsAltered,
        string? TownEvents,
        string? NeverAgain,
        int DlcCount,
        IReadOnlyList<SaveStateCampaignDlcFacts> Dlcs,
        int PresentedDlcCount,
        IReadOnlyList<SaveStateCampaignDlcFacts> PresentedDlcs,
        int ProfileOptionCount,
        IReadOnlyList<SaveStateCampaignProfileOptionFacts> ProfileOptions,
        IReadOnlyList<string> PersistentUgcIds);

    private sealed record SaveStateCampaignDlcFacts(
        string SlotId,
        string? Name,
        string? Source);

    private sealed record SaveStateCampaignProfileOptionFacts(
        string Key,
        string Type,
        string? Value);

    private sealed record SaveStateProgressionFacts(
        int? Version,
        int? TotalQuestsFinished,
        int? TotalSuccessfulQuestsFinished,
        int? TotalRecruitedStageCoachHeroes,
        int? LastQuestPlayedId,
        bool? LastQuestPlayedSuccessfully,
        int? LastQuestPlayedXp,
        int? LastRaidQuestId,
        bool? LastRaidSuccess,
        bool? LastRaidWasPlotQuest,
        SaveStateProgressionInfestationFacts Infestation,
        int DungeonCount,
        IReadOnlyList<SaveStateProgressionDungeonFacts> Dungeons,
        int AchievementCount,
        int CompletedAchievementCount,
        int AwardedAchievementCount,
        IReadOnlyList<SaveStateProgressionAchievementFacts> Achievements,
        int RealAchievementCount,
        int CompletedRealAchievementCount,
        int AwardedRealAchievementCount,
        IReadOnlyList<SaveStateProgressionAchievementFacts> RealAchievements,
        IReadOnlyList<string> CompletedPlotQuestDataIds,
        IReadOnlyList<string> FlashbackCompletionCountIds);

    private sealed record SaveStateProgressionInfestationFacts(
        int? SequenceElementId,
        int? RngSeed,
        int? WeeksLeftInSequenceElement,
        int? WeeksTotalInSequenceElement);

    private sealed record SaveStateProgressionDungeonFacts(
        string DungeonId,
        int? Xp);

    private sealed record SaveStateProgressionAchievementFacts(
        string Key,
        string? Id,
        int? Rtti,
        bool? Completed,
        bool? Awarded,
        IReadOnlyList<SaveStateProgressionAchievementConditionFacts> Conditions,
        IReadOnlyList<SaveStateProgressionAchievementScalarFacts> ExtraScalarFields);

    private sealed record SaveStateProgressionAchievementConditionFacts(
        string SlotId,
        int? EnemiesKilled);

    private sealed record SaveStateProgressionAchievementScalarFacts(
        string LocalPath,
        string Name,
        string Type,
        string? Value);

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
