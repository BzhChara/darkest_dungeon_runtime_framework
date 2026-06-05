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
                    var definitionFound = !string.IsNullOrWhiteSpace(hero.HeroClass)
                        && definitionsByClass.TryGetValue(hero.HeroClass!, out definition);
                    var allCombatSkillIds = definitionFound
                        ? definition.CombatSkills.Select(skill => skill.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : [];
                    var allCampingSkillIds = definitionFound
                        ? definition.CampingSkills.Select(skill => skill.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : [];

                    return new SaveStateHeroLoadoutFacts(
                        hero.Id,
                        hero.Name,
                        hero.HeroClass,
                        definitionFound,
                        hero.RosterStatus,
                        hero.ResolveXp,
                        definitionFound ? definition.SelectedCombatSkillsMax : null,
                        hero.CombatSkillIds,
                        allCombatSkillIds,
                        DifferenceIds(allCombatSkillIds, hero.CombatSkillIds),
                        DifferenceIds(hero.CombatSkillIds, allCombatSkillIds),
                        hero.CampingSkillIds,
                        allCampingSkillIds,
                        DifferenceIds(allCampingSkillIds, hero.CampingSkillIds),
                        DifferenceIds(hero.CampingSkillIds, allCampingSkillIds),
                        hero.WeaponRank,
                        definitionFound ? MaxEquipmentLevel(definition.Weapons) : null,
                        definitionFound ? FindEquipmentLevel(definition.Weapons, hero.WeaponRank) : null,
                        hero.ArmourRank,
                        definitionFound ? MaxEquipmentLevel(definition.Armours) : null,
                        definitionFound ? FindEquipmentLevel(definition.Armours, hero.ArmourRank) : null);
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
