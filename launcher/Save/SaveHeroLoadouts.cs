namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static IReadOnlyList<SaveStateHeroLoadoutFacts> BuildHeroLoadoutFacts(
            IReadOnlyList<SaveStateHeroFacts> heroes,
            SaveStateHeroDefinitionFacts heroDefinitions)
        {
            if (heroes.Count == 0)
            {
                return [];
            }

            var definitionsByClass = heroDefinitions.Classes
                .ToDictionary(definition => definition.HeroClass, StringComparer.OrdinalIgnoreCase);

            return heroes
                .OrderBy(hero => int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue)
                .ThenBy(hero => hero.Id, StringComparer.OrdinalIgnoreCase)
                .Select(hero =>
                {
                    SaveStateHeroClassDefinitionFacts? definition = null;
                    if (!string.IsNullOrWhiteSpace(hero.HeroClass))
                    {
                        definitionsByClass.TryGetValue(hero.HeroClass, out definition);
                    }

                    var allCombatSkillIds = definition is not null
                        ? definition.CombatSkills.Select(skill => skill.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : [];
                    var allCampingSkillIds = definition is not null
                        ? definition.CampingSkills.Select(skill => skill.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : [];

                    return new SaveStateHeroLoadoutFacts(
                        hero.Id,
                        hero.Name,
                        hero.HeroClass,
                        definition is not null,
                        hero.RosterStatus,
                        hero.ResolveXp,
                        definition?.SelectedCombatSkillsMax,
                        hero.CombatSkillIds,
                        allCombatSkillIds,
                        DifferenceIds(allCombatSkillIds, hero.CombatSkillIds),
                        DifferenceIds(hero.CombatSkillIds, allCombatSkillIds),
                        hero.CampingSkillIds,
                        allCampingSkillIds,
                        DifferenceIds(allCampingSkillIds, hero.CampingSkillIds),
                        DifferenceIds(hero.CampingSkillIds, allCampingSkillIds),
                        hero.WeaponRank,
                        definition is not null ? MaxEquipmentLevel(definition.Weapons) : null,
                        definition is not null ? FindEquipmentLevel(definition.Weapons, hero.WeaponRank) : null,
                        hero.ArmourRank,
                        definition is not null ? MaxEquipmentLevel(definition.Armours) : null,
                        definition is not null ? FindEquipmentLevel(definition.Armours, hero.ArmourRank) : null);
                })
                .ToArray();
        }

        private static IReadOnlyList<string> DifferenceIds(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            var rightSet = right.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return left
                .Where(value => !rightSet.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int? MaxEquipmentLevel(IReadOnlyList<SaveStateHeroEquipmentDefinitionFacts> equipment)
        {
            var levels = equipment
                .Select(item => item.Level)
                .Where(level => level.HasValue)
                .Select(level => level!.Value)
                .ToArray();
            return levels.Length == 0 ? null : levels.Max();
        }

        private static SaveStateHeroEquipmentDefinitionFacts? FindEquipmentLevel(
            IReadOnlyList<SaveStateHeroEquipmentDefinitionFacts> equipment,
            int? level)
        {
            if (!level.HasValue)
            {
                return null;
            }

            return equipment.FirstOrDefault(item => item.Level == level.Value);
        }
    }
}
