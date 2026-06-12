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
        IReadOnlyList<string> OptionalCandidateFiles,
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
        IReadOnlyList<string> OptionalCandidateFiles,
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

    private sealed record MapFileInspectionReport(
        int Version,
        DateTimeOffset GeneratedAt,
        string MapFilePath,
        string MapName,
        SaveStateFileReport File,
        SaveStateMapFacts Map,
        IReadOnlyList<string> AccessIssues);

    private sealed record MapPrototypeMutationReport(
        int Version,
        DateTimeOffset GeneratedAt,
        string SourceMapFilePath,
        string OutputMapFilePath,
        string SourceMapName,
        string Mutation,
        string TargetFinalRoomId,
        int TargetFinalRoomHash,
        string? PreviousFinalRoomId,
        int? PreviousFinalRoomHash,
        int FieldOffset,
        int ValueOffset,
        bool Succeeded,
        SaveStateMapFacts SourceMap,
        SaveStateMapFacts OutputMap,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveStateFacts(
        IReadOnlyList<SaveStatePersistFileFacts> PersistFiles,
        SaveStateHashCatalogFacts HashCatalog,
        SaveStateCampaignFacts Campaign,
        SaveStateProgressionFacts Progression,
        SaveStateQuestFacts Quest,
        SaveStateTownEventFacts TownEvents,
        SaveStateGameKnowledgeFacts GameKnowledge,
        SaveStateJournalFacts Journal,
        SaveStateNarrationFacts Narration,
        SaveStateTutorialFacts Tutorial,
        SaveStateCampaignLogFacts CampaignLog,
        SaveStateCampaignMashFacts CampaignMash,
        SaveStateEstateFacts Estate,
        IReadOnlyDictionary<string, int> Wallet,
        SaveStateUpgradeFacts Upgrades,
        SaveStateHeroDefinitionFacts HeroDefinitions,
        SaveStateTownFacts Town,
        IReadOnlyList<string> BuildingIds,
        IReadOnlyList<string> HeroIds,
        SaveStateRosterFacts Roster,
        SaveStateMapFacts Map,
        SaveStateRaidFacts Raid,
        SaveStateCurioTrackerFacts CurioTracker,
        SaveStateLoadingScreenFacts LoadingScreen,
        SaveStateNoveltyTrackerFacts NoveltyTracker,
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

    private sealed record SaveStateSimpleScalarFacts(
        string Path,
        string Name,
        string Type,
        string? Value,
        bool HasDecodedValue,
        int ItemCount,
        IReadOnlyList<int> IntValues,
        IReadOnlyList<SaveStateResolvedHashFacts> ResolvedIntValues,
        IReadOnlyList<string> StringValues);

    private sealed record SaveStateHashCatalogFacts(
        string SourceScope,
        int SourceFileCount,
        int SkippedSourceFileCount,
        int NameCount,
        int HashCount,
        int AmbiguousHashCount);

    private sealed record SaveStateResolvedHashFacts(
        int Value,
        uint UnsignedValue,
        bool IsResolved,
        bool IsAmbiguous,
        IReadOnlyList<string> Names);

    private sealed record SaveStateEstateFacts(
        int? Version,
        int WalletItemCount,
        IReadOnlyList<SaveStateEstateItemFacts> WalletItems,
        int EstateItemCount,
        IReadOnlyList<SaveStateEstateItemFacts> EstateItems,
        int TrinketItemCount,
        IReadOnlyList<SaveStateEstateItemFacts> TrinketItemsList,
        int? EndlessWaveHighscore,
        bool? WasEndlessWaveHighscoreTampered,
        bool? PerformedBlueprintCorrectionCheck,
        bool? FoundGlobalTamperedFile,
        bool? FoundLocalTamperedFile,
        SaveStateObjectContainerFacts Trinkets,
        SaveStateObjectContainerFacts TrinketItems,
        SaveStateObjectContainerFacts DarkestDungeonTrinketUnlocks,
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
        int RootTrinketRetentionIdCount,
        IReadOnlyList<int> RootTrinketRetentionIds,
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
        int GoalIdCount,
        IReadOnlyList<string> GoalIds,
        SaveStateSimpleScalarFacts? ProgressionGoalIds,
        SaveStateQuestRewardFacts CompletionReward);

    private sealed record SaveStateQuestRewardFacts(
        int? ResolveXp,
        int? ResolveXpPerWaveKill,
        int? MaxTimesDungeonXpAwarded,
        int TrinketRetentionIdCount,
        IReadOnlyList<int> TrinketRetentionIds,
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
        IReadOnlyList<int> ResultEventHistoryValues,
        IReadOnlyList<int> DeadHeroEntryValues,
        IReadOnlyList<string> ResultEventHistoryIds,
        IReadOnlyList<string> DeadHeroEntryIds,
        IReadOnlyList<string> BonusHeroEntryIds,
        IReadOnlyList<string> EventCostIds,
        IReadOnlyList<string> FreeUpgradeTags,
        IReadOnlyList<string> NonRolledAdditionalChanceIds);

    private sealed record SaveStateGameKnowledgeFacts(
        int? Version,
        int CombatSkillCount,
        IReadOnlyList<string> CombatSkillIds,
        SaveStateSimpleScalarFacts? DungeonsUnlocked,
        SaveStateSimpleScalarFacts? PlayedVideoList);

    private sealed record SaveStateJournalFacts(
        int? Version,
        SaveStateSimpleScalarFacts? ReadPageIndexes,
        SaveStateSimpleScalarFacts? RaidReadPageIndexes,
        SaveStateSimpleScalarFacts? RaidUnreadPageIndexes);

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

    private sealed record SaveStateTutorialFacts(
        int? Version,
        SaveStateSimpleScalarFacts? DispatchedEvents);

    private sealed record SaveStateCurioTrackerFacts(
        int? Version,
        int TrackedResultCount,
        IReadOnlyList<SaveStateCurioTrackedResultFacts> TrackedResults);

    private sealed record SaveStateCurioTrackedResultFacts(
        string SlotId,
        int? PropNameHash,
        SaveStateResolvedHashFacts? PropName,
        int? ItemTypeHash,
        SaveStateResolvedHashFacts? ItemType,
        int? ItemIdHash,
        SaveStateResolvedHashFacts? ItemId,
        string? CurioTrackerId);

    private sealed record SaveStateLoadingScreenFacts(
        int? Version,
        string? BackgroundTexturePath,
        int? TitleId,
        SaveStateResolvedHashFacts? Title,
        int? TipId,
        SaveStateResolvedHashFacts? Tip,
        int? NarrationEntryId,
        SaveStateResolvedHashFacts? NarrationEntry,
        SaveStateSimpleScalarFacts? NarrationAudioEventQueueTags);

    private sealed record SaveStateNoveltyTrackerFacts(
        int? Version,
        int CategoryCount,
        int SeenEntryCount,
        IReadOnlyList<SaveStateNoveltyCategoryFacts> Categories);

    private sealed record SaveStateNoveltyCategoryFacts(
        string CategoryId,
        int SeenEntryCount,
        IReadOnlyList<string> SeenEntryIds);

    private sealed record SaveStateCampaignLogFacts(
        int? Version,
        int? TotalWeeks,
        int ChapterCount,
        int EntryCount,
        int HeroRosterEntryCount,
        int PartyEntryCount,
        int DungeonEntryCount,
        int PartyRaidRecordCount,
        int CompletedPartyRaidRecordCount,
        SaveStateCampaignLogPartyRaidRecordFacts? LatestCompletedPartyRaidRecord,
        IReadOnlyList<SaveStateCampaignLogPartyRaidRecordFacts> PartyRaidRecords,
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
        int? QuestHash,
        SaveStateResolvedHashFacts? Quest,
        int? QuestIdHash,
        SaveStateResolvedHashFacts? QuestId,
        int? DungeonTypeHash,
        SaveStateResolvedHashFacts? DungeonType,
        int? Difficulty,
        int? Length,
        int? Score,
        bool? Start,
        bool? Success,
        bool? IsWave,
        bool? IsHighscore,
        bool? EndlessWave,
        int HeroCount,
        IReadOnlyList<int> HeroGuids,
        IReadOnlyList<SaveStateCampaignLogHeroFacts> Heroes,
        IReadOnlyList<SaveStateCampaignLogScalarFacts> ExtraScalarFields);

    private sealed record SaveStateCampaignLogPartyRaidRecordFacts(
        string ChapterSlotId,
        int? ChapterIndex,
        string EntrySlotId,
        int? Rtti,
        int? QuestHash,
        SaveStateResolvedHashFacts? Quest,
        int? QuestIdHash,
        SaveStateResolvedHashFacts? QuestId,
        int? DungeonTypeHash,
        SaveStateResolvedHashFacts? DungeonType,
        int? Difficulty,
        int? Length,
        int? Score,
        bool? Start,
        bool? Success,
        bool? IsWave,
        bool? IsHighscore,
        bool? EndlessWave,
        int HeroCount,
        IReadOnlyList<int> HeroGuids,
        IReadOnlyList<SaveStateCampaignLogHeroFacts> Heroes);

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

    private sealed record SaveStateCampaignMashFacts(
        int? Version,
        SaveStateSimpleScalarFacts? AdditionalMashDisabledInfestationMonsterClassIds,
        int RoamingDungeonToIdCount,
        IReadOnlyList<string> RoamingDungeonToIdKeys,
        int RoamingIdToDungeonCount,
        IReadOnlyList<string> RoamingIdToDungeonKeys);

    private sealed record SaveStateRosterFacts(
        int? Version,
        int? DismissedHeroCount,
        int? HighestResolveXp,
        int? NextGuid,
        int LastPartyGuidCount,
        IReadOnlyList<int> LastPartyGuids,
        IReadOnlyList<int> LastPartyActiveHeroGuids);

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

    private sealed record SaveStateMapFacts(
        bool Exists,
        IReadOnlyList<double> MapBounds,
        bool HasStaticSave,
        SaveStateEmbeddedDsonFacts? StaticSave,
        bool? Populated,
        int? EntranceAreaHash,
        string? EntranceAreaId,
        int? FinalRoomHash,
        string? FinalRoomId,
        int AreaCount,
        int RoomCount,
        int CorridorCount,
        int TileCount,
        int ActiveDoorCount,
        int DynamicAreaCount,
        int DynamicTileCount,
        SaveStateMapTopologyFacts Topology,
        IReadOnlyList<SaveStateMapDynamicAreaFacts> DynamicAreas,
        IReadOnlyList<SaveStateMapAreaFacts> Areas);

    private sealed record SaveStateMapTopologyFacts(
        bool HasEntranceArea,
        bool HasFinalRoom,
        bool EntranceCanReachFinal,
        int ReachableAreaCount,
        IReadOnlyList<string> ReachableAreaIds,
        IReadOnlyList<string> UnreachableAreaIds,
        int AreaDoorEdgeCount,
        int TileDoorEdgeCount,
        int InvalidDoorTargetCount,
        IReadOnlyList<string> Issues);

    private sealed record SaveStateEmbeddedDsonFacts(
        int Length,
        int ObjectCount,
        int FieldCount,
        int ParsedScalarCount,
        int RawScalarCount,
        int ObjectPathCount,
        int RootChildCount,
        IReadOnlyList<string> RootChildIds);

    private sealed record SaveStateMapAreaFacts(
        string AreaId,
        int? AreaHash,
        int? Kind,
        string InferredRole,
        string? Name,
        bool? Torch,
        IReadOnlyList<double> Bounds,
        int TileCount,
        int DoorSlotCount,
        int ActiveDoorCount,
        IReadOnlyList<SaveStateMapDoorFacts> Doors,
        IReadOnlyList<SaveStateMapTileFacts> TileSamples);

    private sealed record SaveStateMapDoorFacts(
        string SlotId,
        int? TargetAreaHash,
        string? TargetAreaId,
        int? TargetTileIndex,
        int? DoorType,
        bool? Implied);

    private sealed record SaveStateMapTileFacts(
        string TileId,
        IReadOnlyList<double> MapPosition,
        IReadOnlyList<double> SidePosition,
        int? AreaHash,
        int? Type,
        int? Obstacle,
        int? TextureId,
        int? FrontTextureId,
        bool? IsTextureOverrideValid,
        bool? HdAlwaysAccessible,
        int? Current,
        SaveStateMapDoorFacts? DoorTo);

    private sealed record SaveStateMapDynamicAreaFacts(
        string AreaId,
        int? AreaHash,
        int? Knowledge,
        bool? Reversed,
        int TileCount,
        IReadOnlyList<SaveStateMapDynamicTileFacts> TileSamples);

    private sealed record SaveStateMapDynamicTileFacts(
        string TileId,
        int? Light,
        int? Content,
        int? CurioPropHash,
        int? Knowledge,
        int? TrapHash,
        int? MashIndex,
        int? MashType,
        bool? CritScout);

    private sealed record SaveStateRaidFacts(
        int? Version,
        SaveStateRaidInstanceFacts Instance,
        SaveStateRaidLocationFacts Location,
        SaveStateRaidBattleFacts Battle,
        SaveStateRaidPartyFacts Party,
        SaveStateRaidCampFacts Camp,
        SaveStateRaidMashFacts Mash,
        SaveStateRaidStatDatabaseFacts StatDatabase);

    private sealed record SaveStateRaidInstanceFacts(
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
        int GoalIdCount,
        IReadOnlyList<string> GoalIds,
        SaveStateSimpleScalarFacts? ProgressionGoalIds,
        SaveStateQuestRewardFacts CompletionReward,
        int ProgressGroupCount,
        IReadOnlyList<SaveStateRaidProgressGroupFacts> ProgressGroups);

    private sealed record SaveStateRaidProgressGroupFacts(
        string GroupId,
        string SourcePath,
        int ScalarFieldCount,
        IReadOnlyList<SaveStateRaidScalarFacts> ScalarFields);

    private sealed record SaveStateRaidScalarFacts(
        string LocalPath,
        string Name,
        string Type,
        string? Value);

    private sealed record SaveStateRaidLocationFacts(
        int? InAreaHash,
        string? InAreaId,
        int? AreaTile,
        int? LastRoomHash,
        string? LastRoomId,
        SaveStateRaidDoorwayFacts Doorway,
        double? StartElapsedTime,
        double? Torchlight,
        double? AmbushStartTorchlight,
        double? ShardConsumePercent,
        bool? Teleported,
        bool? InBattle);

    private sealed record SaveStateRaidDoorwayFacts(
        int? TargetAreaHash,
        string? TargetAreaId,
        int? TargetTileIndex,
        bool? Implied);

    private sealed record SaveStateRaidBattleFacts(
        bool Exists,
        bool? BeforeTurnSave,
        int? MonsterActorGuidOffset,
        int? Seed,
        int? Round,
        int? RoundStallCount,
        bool? PreviousStallAccelerated,
        bool? PartySurprised,
        bool? MonstersSurprised,
        int? RetreatAttempts,
        int? CurrentHeroConsecutiveMissAccBuff,
        int? ConsecutiveMonsterDodges,
        int? ConsecutiveMonsterCrits,
        int EnemyCount,
        IReadOnlyList<SaveStateRaidBattleEnemyFacts> Enemies,
        int HeroInitiativeCount,
        IReadOnlyList<SaveStateRaidBattleInitiativeFacts> HeroInitiative,
        int MonsterInitiativeCount,
        IReadOnlyList<SaveStateRaidBattleInitiativeFacts> MonsterInitiative);

    private sealed record SaveStateRaidBattleEnemyFacts(
        string SlotId,
        bool? IsHero,
        int? BattleGuid,
        string? MonsterClass,
        bool? CanSpawnLoot,
        int? PreviousMonsterClassHash,
        string? ActorName,
        double? CurrentHp,
        int? Stunned,
        bool? CombatReady,
        int? DamageSourceData,
        int? DamageSourceType,
        int? DamageType,
        int? ColourVariation,
        int? PerformingTurn,
        int? RoundsInRanks,
        int? CheckRoundRanks,
        int? DeathClassMonsterClassHash,
        int SkillCooldownKeyCount,
        IReadOnlyList<int> SkillCooldownKeys,
        int SkillCooldownValueCount,
        IReadOnlyList<int> SkillCooldownValues);

    private sealed record SaveStateRaidBattleInitiativeFacts(
        string SlotId,
        int? RosterGuid,
        int? BattleGuid,
        double? Initiative,
        bool? IsBonus,
        int? CombatSkillIdOverride);

    private sealed record SaveStateRaidPartyFacts(
        bool? IsMovingLeft,
        int? RetreatRoomHash,
        string? RetreatRoomId,
        int HeroCount,
        IReadOnlyList<int> HeroGuids,
        int? StartHeroSize,
        int InventoryItemCount,
        IReadOnlyList<SaveStateRaidItemFacts> InventoryItems,
        int? HungerRoomBuffer);

    private sealed record SaveStateRaidItemFacts(
        string SlotId,
        string? Id,
        string? Type,
        int? Amount);

    private sealed record SaveStateRaidCampFacts(
        int? Phase,
        int? CampingSkillPoints,
        int? CampFinishFlashbackClassId,
        int SkillLogEntryCount,
        IReadOnlyList<SaveStateRaidCampSkillLogFacts> SkillLog,
        int PartySkillLogEntryCount,
        IReadOnlyList<SaveStateRaidCampPartySkillLogFacts> PartySkillLog);

    private sealed record SaveStateRaidCampSkillLogFacts(
        string SlotId,
        int? RosterId,
        int? SkillId,
        int? Level,
        int? Count);

    private sealed record SaveStateRaidCampPartySkillLogFacts(
        string SlotId,
        int? PartySkillId,
        int? PartySkillLevel,
        int? PartySkillAmbush,
        int BuffResultCount,
        IReadOnlyList<SaveStateRaidCampPartySkillBuffResultFacts> BuffResults);

    private sealed record SaveStateRaidCampPartySkillBuffResultFacts(
        string SlotId,
        int? ActorGuid,
        int? PartySkillAmbush);

    private sealed record SaveStateRaidMashFacts(
        bool? HasMashData,
        SaveStateSimpleScalarFacts? ValidAdditionalMashEntryIndexes);

    private sealed record SaveStateRaidStatDatabaseFacts(
        int EventCount,
        int TotalEntryCount,
        IReadOnlyList<SaveStateRaidStatEventFacts> Events);

    private sealed record SaveStateRaidStatEventFacts(
        string EventId,
        int? Count,
        int EntryCount);

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
        SaveStateResolvedHashFacts? LastQuestPlayed,
        bool? LastQuestPlayedSuccessfully,
        int? LastQuestPlayedXp,
        int? LastRaidQuestId,
        SaveStateResolvedHashFacts? LastRaidQuest,
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
        SaveStateObjectContainerFacts CompletedPlotQuestsData,
        SaveStateObjectContainerFacts FlashbackCompletionCounts,
        int CompletedPlotQuestDataCount,
        IReadOnlyList<SaveStateProgressionCompletedPlotQuestFacts> CompletedPlotQuestData,
        int FlashbackCompletionCount,
        IReadOnlyList<SaveStateProgressionFlashbackCompletionFacts> FlashbackCompletions,
        IReadOnlyList<string> CompletedPlotQuestDataIds,
        IReadOnlyList<string> FlashbackCompletionCountIds);

    private sealed record SaveStateObjectContainerFacts(
        string Path,
        bool Exists,
        bool HasDirectChildren,
        int DirectChildCount,
        IReadOnlyList<string> DirectChildIds,
        int DescendantObjectPathCount,
        int DescendantScalarFieldCount);

    private sealed record SaveStateProgressionInfestationFacts(
        int? SequenceElementId,
        int? RngSeed,
        int? WeeksLeftInSequenceElement,
        int? WeeksTotalInSequenceElement);

    private sealed record SaveStateProgressionDungeonFacts(
        string DungeonId,
        int? Xp);

    private sealed record SaveStateProgressionCompletedPlotQuestFacts(
        string SlotId,
        int? PlotQuestId,
        int HeroCount,
        IReadOnlyList<SaveStateProgressionCompletedPlotQuestHeroFacts> Heroes);

    private sealed record SaveStateProgressionCompletedPlotQuestHeroFacts(
        string SlotId,
        int? Guid,
        bool? Survived,
        bool? LastBlow);

    private sealed record SaveStateProgressionFlashbackCompletionFacts(
        string FlashbackId,
        int? CompletionCount);

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
        string? RawHex)
    {
        public SaveStateEmbeddedDsonSummary? EmbeddedDson { get; init; }
    }

    private sealed record SaveStateEmbeddedDsonSummary(
        int Length,
        SaveStateDsonSummary DsonSummary,
        int ObjectPathCount,
        int RootChildCount,
        IReadOnlyList<string> RootChildIds,
        IReadOnlyList<SaveStateEmbeddedDsonScalarSample> ScalarSamples)
    {
        [property: JsonIgnore]
        public IReadOnlyList<SaveStateDsonScalar> AllScalars { get; init; } = [];

        [property: JsonIgnore]
        public IReadOnlyList<string> AllObjectPaths { get; init; } = [];
    }

    private sealed record SaveStateEmbeddedDsonScalarSample(
        string Path,
        string Name,
        string Type,
        string? Value);

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
