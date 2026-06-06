# Save Field Map

Source snapshot: `logs/save_states/hash_resolution_final_probe_20260606_170955_850.json`.

This document summarizes the current campaign save field coverage before any gameplay-rule work. Numeric slots and dynamic ids are normalized as `[]` so repeated entries are represented once. The full scalar path list remains in `facts.persistFiles[].scalarFields` in the source snapshot.

## Coverage Rules

- `semantic`: exported into a named `facts.*` model with stable meaning.
- `scalar`: DSON scalar path and value are visible through `facts.persistFiles`, but no dedicated semantic model exists yet.
- `raw`: field is visible but still encoded as a raw/nested payload or object-only branch, so more decoding is needed.
- `object-only`: root/object path exists in this sample, but no scalar child was decoded from it.

## File Summary

| File | Scalars | Roots | Current semantic coverage | Mod value |
| --- | ---: | --- | --- | --- |
| `persist.game.json` | 48 | `date_time`, `dlc`, `profile_options`, `raid_save`, `totalelapsed` | strong partial: campaign identity, DLC, presented DLC, profile options | campaign mode, DLC gates, option-dependent rules |
| `persist.estate.json` | 25 | `wallet`, `estate_items`, `tampering`, `trinkets` | strong partial: wallet, estate items, highscore, tamper flags | resources, item economy, anti-tamper diagnostics |
| `persist.roster.json` | 9 plus nested hero payloads | `heroes`, `last_party`, counters | strong partial: hero nested facts and loadouts | party rules, hero availability, unlock-all modes |
| `persist.upgrades.json` | 913 | `purchases` | strong partial: purchases, tree definitions, missing requirements | building/hero upgrade state |
| `persist.quest.json` | 62 | `quests`, `plot_quest_total` | partial: quest entries and rewards | quest generation, post-game map/story chains |
| `persist.town_event.json` | 7 | current event, history, cost/free-upgrade roots | initial semantic facts | town event rules, temporary bonuses |
| `persist.town.json` | 570 | `buildings` | strong partial: buildings, stores, recruits, activity slots | town workflows, store/recruit/activity changes |
| `persist.progression.json` | 476 | achievements, dungeon, infestation, last quest/raid, totals | strong partial: counters, dungeon XP, infestation, achievements, real achievements | story gates, boss progress, post-game unlocks |
| `persist.game_knowledge.json` | 3 | dungeons, combat skills, videos | semantic: combat skill ids, int vectors, resolved hash names | UI/knowledge unlocks |
| `persist.journal.json` | 4 | page index lists | semantic: page index scalar snapshots | journal/collection state |
| `persist.narration.json` | 193 | narration entry logs | semantic: campaign/raid/town visit entry logs and summaries | narration replay/gating |
| `persist.tutorial.json` | 2 | dispatched tutorial events | semantic: dispatched-events int vector with resolved names | tutorial prompt gating |
| `persist.campaign_log.json` | 25 | chapters, total weeks | semantic: chapters, party/hero/dungeon log entries | campaign history and recap |
| `persist.campaign_mash.json` | 2 | roaming/dungeon mash roots | semantic: mash int vector snapshot and roaming map keys | roaming state, infestation support |

## Field Patterns

### Content hash catalog

Semantic now:

- `facts.hashCatalog.sourceScope` is `game_install_no_local_mods`.
- Hash names are scanned from the original game install, including official DLC, while local `DarkestDungeon/mods` is skipped.
- Current probe stats: 658 parsed source files, 5 skipped official source files with non-JSON content despite JSON extensions, 2178 names, 2178 hashes, 0 ambiguous hashes.
- `SaveStateSimpleScalarFacts.resolvedIntValues[]` maps decoded int-vector values to stable content ids when a catalog entry exists.

### `persist.game.json`

Semantic now:

- `base_root.version`
- `base_root.totalelapsed`
- `base_root.inraid`
- `base_root.raiddungeon`
- `base_root.estatename`
- `base_root.game_mode`
- `base_root.date_time`
- `base_root.profile_options.values.town_events`
- `base_root.profile_options.values.never_again`
- `base_root.raid_save`
- `base_root.dlc_init`
- `base_root.dd_options_altered`
- `base_root.dlc.[].name` as `campaign.dlcs[]`
- `base_root.dlc.[].source`
- `base_root.presented_dlc.dlc.[].name` as `campaign.presentedDlcs[]`
- `base_root.presented_dlc.dlc.[].source`
- `base_root.profile_options.values.*` as `campaign.profileOptions[]`, preserving raw option names and types

Still scalar/raw:

- `base_root.profile_options.values.corpses`
- `base_root.profile_options.values.curio_tracker`
- `base_root.profile_options.values.dd_mode`
- `base_root.profile_options.values.deaths_door_recovery_debuffs`
- `base_root.profile_options.values.deck_based_stage_coach`
- `base_root.profile_options.values.multiplied_enemy_crits`
- `base_root.profile_options.values.provision_warnings`
- `base_root.profile_options.values.quest_select_warnings`
- `base_root.profile_options.values.retreats_can_fail`
- `base_root.profile_options.values.stall_penalty`

Raw/object-only roots:

- `base_root.persistent_ugcs`

### `persist.estate.json`

Semantic now:

- `base_root.wallet.[].type`
- `base_root.wallet.[].amount`
- `base_root.estate_items.items.[].type`
- `base_root.estate_items.items.[].id`
- `base_root.estate_items.items.[].amount`
- `base_root.endless_wave_highscore`
- `base_root.was_endless_wave_highscore_tampered`
- `base_root.performed_blueprint_correction_check`
- `base_root.tampering.tampering_manager.foundGlobalTamperedFile`
- `base_root.tampering.tampering_manager.foundLocalTamperedFile`

Still scalar:

- `base_root.version`

Object-only roots:

- `base_root.trinkets`
- `base_root.darkest_dungeon_trinket_unlocks`

### `persist.roster.json`

Semantic now:

- `base_root.heroes.[].hero_file_data.raw_data` is decoded as nested hero facts:
- identity: `heroId`, `name`, `heroClass`, roster/missing/building/status fields
- runtime stats: resolve XP, HP, stress, weapon/armour rank, combat readiness, death-door state
- combat state counters: steps, kills, provisions, successful Darkest Dungeon count
- quirks: id, new/locked flags, mission count, replace/evolution fields
- skills: selected combat and camping skill ids
- trinkets: slot, id, type, amount
- loadouts: current skills and equipment linked to base hero definitions
- `base_root.version`
- `base_root.dismissed_hero_count`
- `base_root.highest_resolve_xp`
- `base_root.nextGuid`
- `base_root.last_party.last_party_guids` as `roster.lastPartyGuids[]`
- `roster.lastPartyActiveHeroGuids[]` filters out empty `-1` slots

### `persist.upgrades.json`

Semantic now:

- `base_root.purchases.[].tree_id`
- `base_root.purchases.[].requirement_code`
- `base_root.purchases.[].instance_number`
- `base_root.purchases.[].is_purchased`
- linked static definitions: tree name, category, tags, source, requirements, currency costs, prerequisites
- derived tree state: purchased/missing requirement codes, current/next requirement, complete flag

Still scalar:

- `base_root.version`

### `persist.quest.json`

Semantic now:

- `base_root.version`
- `base_root.plot_quest_total`
- `base_root.quests.[].id`
- `base_root.quests.[].dungeon`
- `base_root.quests.[].type`
- `base_root.quests.[].map_name`
- `base_root.quests.[].difficulty`
- `base_root.quests.[].length`
- `base_root.quests.[].is_plot_quest`
- `base_root.quests.[].counted_in_generation`
- `base_root.quests.[].is_from_town_event`
- `base_root.quests.[].completion_threshold`
- `base_root.quests.[].use_default_progression_goals`
- `base_root.quests.[].raid_rules_override`
- `base_root.quests.[].torch_setting`
- `base_root.quests.[].goal_ids` as `quests[].goalIds[]`
- `base_root.quests.[].progression_goal_ids` as scalar snapshot pending type confirmation
- `base_root.quests.[].completion_reward.resolve_xp`
- `base_root.quests.[].completion_reward.resolve_xp_per_wave_kill`
- `base_root.quests.[].completion_reward.max_times_dungeon_xp_awarded`
- `base_root.quests.[].completion_reward.trinket_retention_ids` as `completionReward.trinketRetentionIds[]`
- `base_root.quests.[].completion_reward.items_definition.items.[].type`
- `base_root.quests.[].completion_reward.items_definition.items.[].id`
- `base_root.quests.[].completion_reward.items_definition.items.[].amount`
- `base_root.trinket_retention_ids` as `quest.rootTrinketRetentionIds[]`

Still scalar/deeper:

- `base_root.quests.[].progression_goal_ids` is not in the referenced DSON vector type table; keep it as a scalar snapshot until a non-empty sample or static rule confirms the encoding.

### `persist.town_event.json`

Semantic now:

- `base_root.version`
- `base_root.current_result_event_id`
- `base_root.has_unclaimed_interaction`
- `base_root.last_town_event_week`
- `base_root.rng_seed`
- collection ids/counts for `result_event_history`, `dead_hero_entries`, `bonus_hero_entries`, `event_cost`, `free_upgrade_tags`, `non_rolled_additional_chances`
- `base_root.result_event_history` as `townEvents.resultEventHistoryValues[]`
- `base_root.dead_hero_entries` as `townEvents.deadHeroEntryValues[]`

Object-only roots:

- `base_root.bonus_hero_entries`
- `base_root.event_cost`
- `base_root.free_upgrade_tags`
- `base_root.non_rolled_additional_chances`

### `persist.town.json`

Semantic now:

- buildings: id, activity/store presence, activity ids, store ids
- activity slots: hero id, visits remaining, resident occupied, side effect, occupied flag
- store inventories: slot id, item id, item type, amount
- generated recruits: name, class, resolve XP, HP, stress, weapon/armour rank, event flags, quirks, skills, trinkets
- quirk treatments: building, activity, slot, bucket, quirk id, action
- deck history: building, store, deck version, entry id, count

Visible field patterns:

- `base_root.buildings.<building>.activities.<activity>.[].hero`
- `base_root.buildings.<building>.activities.<activity>.[].visitsRemaining`
- `base_root.buildings.<building>.activities.<activity>.[].resident_occupied`
- `base_root.buildings.<building>.activities.<activity>.[].is_side_effect_result`
- `base_root.buildings.<building>.activities.<activity>.quirk_treatment.[].<bucket>.quirk_treatment`
- `base_root.buildings.<building>.activities.<activity>.quirk_treatment.[].<bucket>.quirk_treatment_action`
- `base_root.buildings.<building>.store.<store>.inventory.items.[].type`
- `base_root.buildings.<building>.store.<store>.inventory.items.[].id`
- `base_root.buildings.<building>.store.<store>.inventory.items.[].amount`
- `base_root.buildings.<building>.store.<store>.deck_history_version_0.[].count`
- `base_root.buildings.stage_coach.store.<store>.generated.[].actor.*`
- `base_root.buildings.stage_coach.store.<store>.generated.[].heroClass`
- `base_root.buildings.stage_coach.store.<store>.generated.[].resolveXp`
- `base_root.buildings.stage_coach.store.<store>.generated.[].weapon_rank`
- `base_root.buildings.stage_coach.store.<store>.generated.[].armour_rank`
- `base_root.buildings.stage_coach.store.<store>.generated.[].quirks.<quirk>.*`
- `base_root.buildings.stage_coach.store.<store>.generated.[].skills.selected_combat_skills.*`
- `base_root.buildings.stage_coach.store.<store>.generated.[].skills.selected_camping_skills.*`
- `base_root.buildings.stage_coach.store.<store>.generated.[].trinkets.items.*`

### `persist.progression.json`

Semantic now:

- `base_root.version`
- `base_root.total_quests_finished`
- `base_root.total_successful_quests_finished`
- `base_root.total_recruited_stage_coach_heroes`
- `base_root.last_quest_played_id`
- `base_root.last_quest_played_successfully`
- `base_root.last_quest_played_xp`
- `base_root.last_raid_quest_id`
- `base_root.last_raid_success`
- `base_root.last_raid_was_a_plot_quest`
- `base_root.dungeon.<dungeon>.xp` as `progression.dungeons[]`
- `base_root.infestation.sequence_element_id`
- `base_root.infestation.rng_seed`
- `base_root.infestation.number_of_weeks_left_in_sequence_element`
- `base_root.infestation.number_of_weeks_total_in_sequence_element`
- `base_root.achievements.<id>.id` as `progression.achievements[]`
- `base_root.achievements.<id>.rtti`
- `base_root.achievements.<id>.completed`
- `base_root.achievements.<id>.awarded`
- `base_root.real_achievements.<id>.id` as `progression.realAchievements[]`
- `base_root.real_achievements.<id>.rtti`
- `base_root.real_achievements.<id>.completed`
- `base_root.real_achievements.<id>.awarded`
- `base_root.real_achievements.<id>.conditions.[].enemies_killed`
- non-standard achievement fields, such as `boss_battle`, as `extraScalarFields`

Object-only/raw roots:

- `base_root.completed_plot_quests_data`
- `base_root.flashback_completion_counts`

### `persist.game_knowledge.json`

Semantic now:

- `base_root.version`
- `base_root.dungeons_unlocked` as an int vector snapshot
- `base_root.played_video_list` as an int vector snapshot
- `base_root.combat_skills.<skill_id>` as `gameKnowledge.combatSkillIds[]`

Hash resolution:

- `dungeons_unlocked` and `played_video_list` values are resolved through `resolvedIntValues[]` when a matching catalog name exists.
- Current sample resolves dungeon hash `-630469331` to `crypts`.

Object-only roots:

- `base_root.combat_skills`

### `persist.journal.json`

Semantic now:

- `base_root.version`
- `base_root.read_page_indexes` as an int vector snapshot
- `base_root.raid_read_page_indexes` as an int vector snapshot
- `base_root.raid_unread_page_indexes` as an int vector snapshot

This sample has empty page-index vectors.

### `persist.narration.json`

Semantic now:

- `base_root.version`
- `base_root.campaign_entry_log.[].entry_type`
- `base_root.campaign_entry_log.[].audio_event_type`
- `base_root.campaign_entry_log.[].count`
- `base_root.raid_entry_log.[].entry_type`
- `base_root.raid_entry_log.[].audio_event_type`
- `base_root.raid_entry_log.[].count`
- `base_root.town_visit_entry_log.[].entry_type`
- `base_root.town_visit_entry_log.[].audio_event_type`
- `base_root.town_visit_entry_log.[].count`
- per-log entry counts and playback-count totals
- global entry-type and audio-event summaries

### `persist.tutorial.json`

Semantic now:

- `base_root.version`
- `base_root.dispatched_events` as an int vector snapshot

Hash resolution:

- `dispatched_events` values are resolved through `resolvedIntValues[]`.
- Current sample resolves all 15 tutorial event hashes, including `loot`, `death_class`, `embark`, `hallway_nav`, `map_nav`, and `curio`.

### `persist.campaign_log.json`

Semantic now:

- `base_root.version`
- `base_root.total_weeks`
- `base_root.chapters.[].chapterIndex`
- `base_root.chapters.[].[].rtti`
- `base_root.chapters.[].[].heroes.[].name`
- `base_root.chapters.[].[].heroes.[].guid`
- `base_root.chapters.[].[].heroes.[].class`
- `base_root.chapters.[].[].heroes.[].died`
- `base_root.chapters.[].[].name`
- `base_root.chapters.[].[].guid`
- `base_root.chapters.[].[].class`
- `base_root.chapters.[].[].level`
- `base_root.chapters.[].[].dungeon_id`
- derived entry kind: `party`, `heroRoster`, `dungeon`, or `unknown`
- non-standard entry scalar fields are preserved as `extraScalarFields`

### `persist.campaign_mash.json`

Semantic now:

- `base_root.version`
- `base_root.additional_mash_disabled_infestation_monster_class_ids` as an int vector snapshot
- child keys under `base_root.roaming_dungeon_2_ids`
- child keys under `base_root.roaming_id_2_dungeon`

Hash resolution:

- `additional_mash_disabled_infestation_monster_class_ids` values are resolved through `resolvedIntValues[]` when non-empty and when a matching catalog name exists.

Object-only roots:

- `base_root.roaming_dungeon_2_ids`
- `base_root.roaming_id_2_dungeon`

## Parsing Backlog Before Gameplay Rules

1. Remaining uncertain containers: `progression_goal_ids`, `completed_plot_quests_data`, `flashback_completion_counts`, estate trinkets, and Darkest Dungeon trinket unlocks need non-empty samples or deeper object/raw decoding before they can be considered complete.
2. Hash catalog coverage should be expanded only when future probes expose unresolved hash values that matter to a gameplay rule.

Gameplay systems such as building-upgrade scheduling or post-Ancestor story expansion should wait until the relevant backlog items are either parsed or explicitly marked unnecessary.
