using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace DDRuntimeLoader;

internal static class QuestBoardPolicyMaterializer
{
    private const int ReportVersion = 1;
    private const string ReportFileName = "quest_board_policy_materialize_report.json";
    private const string SelectionMode = "policyModeAwareWeightedPools";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static int _sequence;

    public static QuestBoardPolicyMaterializeReport Write(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        string? saveStateReportPath,
        int? slotLimit,
        int? seedOverride)
    {
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var issues = new List<QuestBoardPolicyMaterializeIssue>();
        var resolve = QuestBoardPolicyResolveReporter.Write(config, patchPlan, log, projectRoot, saveStateReportPath);
        var profileScope = ManagedActionProfileScopeResolver.FromSaveStateReport(resolve.SaveStateReportPath);
        var producer = patchPlan.ManagedActionProducers.Single(contract =>
            contract.Kind.Equals(ManagedActionProducerContractFactory.QuestBoardPolicySetKind, StringComparison.OrdinalIgnoreCase));
        var seed = seedOverride ?? resolve.Week ?? 0;
        if (resolve.WarningCount > 0)
        {
            issues.Add(new QuestBoardPolicyMaterializeIssue(
                "warning",
                "quest-board-policy-resolve-has-warnings",
                string.Empty,
                string.Empty,
                string.Empty,
                $"policy resolution reported {resolve.WarningCount} warning(s); inspect the resolve report for source details"));
        }

        var selection = resolve.Succeeded
            ? SelectCandidates(resolve, slotLimit, seed)
            : QuestBoardPolicyMaterializeSelection.Empty;

        if (!resolve.Succeeded)
        {
            issues.Add(new QuestBoardPolicyMaterializeIssue(
                "error",
                "quest-board-policy-resolve-failed",
                string.Empty,
                string.Empty,
                string.Empty,
                "quest board policy materialization is blocked because policy resolution reported errors"));
        }

        var artifactPath = string.Empty;
        var status = "blocked";
        if (resolve.Succeeded)
        {
            if (selection.SelectedQuestIds.Count > 0)
            {
                artifactPath = WriteArtifact(config, resolve, selection, producer, slotLimit, seed, profileScope, issues);
                status = "materialized";
            }
            else
            {
                artifactPath = WriteEmptyArtifact(config, resolve, producer, slotLimit, seed, profileScope, issues);
                status = "empty";
                issues.Add(new QuestBoardPolicyMaterializeIssue(
                    "info",
                    "quest-board-policy-no-selected-quests",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "no resolved policy candidates were selected, so an empty marker was written to supersede stale questBoardPolicies artifacts"));
            }
        }

        var report = new QuestBoardPolicyMaterializeReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            resolve.ReportPath,
            resolve.SaveStateReportPath,
            profileScope,
            status,
            artifactPath,
            SelectionMode,
            seed,
            slotLimit,
            resolve.PolicyCount,
            resolve.ResolvedQuestCount,
            selection.SelectedQuestIds.Count,
            selection.PoolCount,
            selection.DuplicateSkippedCount,
            selection.SlotLimitSkippedCount,
            issues.Count(issue => issue.Severity == "error"),
            issues.Count(issue => issue.Severity == "warning"),
            selection.SelectedQuestIds,
            selection.Policies,
            selection.Candidates,
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-policy-materialize report path={Quote(reportPath)} status={status} " +
            $"artifact={Quote(artifactPath)} selectedQuests={report.SelectedQuestCount} " +
            $"resolvedQuests={report.ResolvedQuestCount} pools={report.PoolCount} " +
            $"slotLimit={FormatNullableInt(slotLimit)} seed={seed} " +
            $"profileScope={Quote(profileScope.Kind)} profile={Quote(profileScope.ProfileId)} " +
            $"warnings={report.WarningCount} errors={report.ErrorCount}");

        foreach (var issue in issues)
        {
            var line =
                $"quest-board-policy-materialize issue severity={issue.Severity} code={issue.Code} " +
                $"plugin={Quote(issue.PluginId)} policy={Quote(issue.PolicyId)} entry={Quote(issue.EntryId)} " +
                $"message={Quote(issue.Message)}";
            if (issue.Severity == "error")
            {
                log.Error(line);
            }
            else if (issue.Severity == "warning")
            {
                log.Warn(line);
            }
            else
            {
                log.Info(line);
            }
        }

        return report;
    }

    private static QuestBoardPolicyMaterializeSelection SelectCandidates(
        QuestBoardPolicyResolveReport resolve,
        int? slotLimit,
        int seed)
    {
        var selectedQuestIds = new List<string>();
        var selectedQuestIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateReports = new List<QuestBoardPolicyMaterializeCandidateReport>();
        var policyReports = new List<QuestBoardPolicyMaterializePolicyReport>();
        var random = new Random(seed);
        var poolCount = 0;
        var duplicateSkippedCount = 0;
        var slotLimitSkippedCount = 0;

        foreach (var policy in resolve.Policies)
        {
            var policyCandidateStart = candidateReports.Count;
            if (policy.Mode.Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = policy.Candidates
                    .Where(IsSelectableCandidate)
                    .ToArray();
                if (candidates.Length > 0)
                {
                    var groups = candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.Pool))
                        ? candidates.GroupBy(candidate => string.IsNullOrWhiteSpace(candidate.Pool)
                            ? BuildSyntheticPoolKey(policy)
                            : candidate.Pool, StringComparer.OrdinalIgnoreCase)
                        : candidates.GroupBy(_ => BuildSyntheticPoolKey(policy), StringComparer.OrdinalIgnoreCase);

                    foreach (var group in groups)
                    {
                        poolCount++;
                        SelectOneFromPool(
                            policy,
                            group.ToArray(),
                            random,
                            selectedQuestIds,
                            selectedQuestIdSet,
                            candidateReports,
                            slotLimit,
                            ref duplicateSkippedCount,
                            ref slotLimitSkippedCount);
                    }
                }
            }
            else
            {
                foreach (var candidate in policy.Candidates.Where(candidate => candidate.ResolutionStatus == "active"))
                {
                    TrySelectCandidate(
                        policy,
                        candidate,
                        "fixed",
                        selectedQuestIds,
                        selectedQuestIdSet,
                        candidateReports,
                        slotLimit,
                        ref duplicateSkippedCount,
                        ref slotLimitSkippedCount);
                }

                foreach (var group in policy.Candidates
                             .Where(candidate => candidate.ResolutionStatus == "eligiblePoolCandidate")
                             .GroupBy(candidate => string.IsNullOrWhiteSpace(candidate.Pool)
                                 ? BuildSyntheticPoolKey(policy)
                                 : candidate.Pool, StringComparer.OrdinalIgnoreCase))
                {
                    poolCount++;
                    SelectOneFromPool(
                        policy,
                        group.ToArray(),
                        random,
                        selectedQuestIds,
                        selectedQuestIdSet,
                        candidateReports,
                        slotLimit,
                        ref duplicateSkippedCount,
                        ref slotLimitSkippedCount);
                }
            }

            var policyCandidates = candidateReports
                .Skip(policyCandidateStart)
                .ToArray();
            var policySelectedQuestIds = policyCandidates
                .Where(candidate => candidate.MaterializeStatus == "selected")
                .Select(candidate => candidate.EffectiveQuestId)
                .ToArray();
            var policyStatus = policySelectedQuestIds.Length > 0
                ? "selected"
                : policyCandidates.Length > 0
                    ? "unselected"
                    : policy.Status;
            policyReports.Add(new QuestBoardPolicyMaterializePolicyReport(
                policy.PluginId,
                policy.PluginName,
                policy.ManifestPath,
                policy.RuleIndex,
                policy.Id,
                policy.Name,
                policy.Mode,
                policyStatus,
                policyCandidates.Length,
                policySelectedQuestIds.Length,
                policySelectedQuestIds));
        }

        return new QuestBoardPolicyMaterializeSelection(
            selectedQuestIds,
            policyReports,
            candidateReports,
            poolCount,
            duplicateSkippedCount,
            slotLimitSkippedCount);
    }

    private static void SelectOneFromPool(
        QuestBoardPolicyResolvePolicyReport policy,
        IReadOnlyList<QuestBoardPolicyResolveCandidateReport> candidates,
        Random random,
        List<string> selectedQuestIds,
        HashSet<string> selectedQuestIdSet,
        List<QuestBoardPolicyMaterializeCandidateReport> candidateReports,
        int? slotLimit,
        ref int duplicateSkippedCount,
        ref int slotLimitSkippedCount)
    {
        if (!HasSlot(selectedQuestIds, slotLimit))
        {
            foreach (var candidate in candidates)
            {
                AddCandidateReport(policy, candidate, "skippedSlotLimit", "slot limit was reached before this pool was drawn", candidateReports);
                slotLimitSkippedCount++;
            }

            return;
        }

        var poolCandidates = new List<QuestBoardPolicyResolveCandidateReport>();
        foreach (var candidate in candidates)
        {
            if (selectedQuestIdSet.Contains(candidate.EffectiveQuestId))
            {
                AddCandidateReport(policy, candidate, "skippedDuplicateQuest", "a previous policy entry already selected this quest id", candidateReports);
                duplicateSkippedCount++;
            }
            else
            {
                poolCandidates.Add(candidate);
            }
        }

        if (poolCandidates.Count == 0)
        {
            return;
        }

        var selected = DrawWeighted(poolCandidates, random);
        AddSelected(policy, selected, "weighted pool draw selected this candidate", selectedQuestIds, selectedQuestIdSet, candidateReports);
        foreach (var candidate in poolCandidates)
        {
            if (!ReferenceEquals(candidate, selected))
            {
                AddCandidateReport(policy, candidate, "notDrawn", "weighted pool draw selected another candidate", candidateReports);
            }
        }
    }

    private static void TrySelectCandidate(
        QuestBoardPolicyResolvePolicyReport policy,
        QuestBoardPolicyResolveCandidateReport candidate,
        string selectionKind,
        List<string> selectedQuestIds,
        HashSet<string> selectedQuestIdSet,
        List<QuestBoardPolicyMaterializeCandidateReport> candidateReports,
        int? slotLimit,
        ref int duplicateSkippedCount,
        ref int slotLimitSkippedCount)
    {
        if (!HasSlot(selectedQuestIds, slotLimit))
        {
            AddCandidateReport(policy, candidate, "skippedSlotLimit", "slot limit was reached before this candidate", candidateReports);
            slotLimitSkippedCount++;
            return;
        }

        if (selectedQuestIdSet.Contains(candidate.EffectiveQuestId))
        {
            AddCandidateReport(policy, candidate, "skippedDuplicateQuest", "a previous policy entry already selected this quest id", candidateReports);
            duplicateSkippedCount++;
            return;
        }

        AddSelected(policy, candidate, $"{selectionKind} candidate selected", selectedQuestIds, selectedQuestIdSet, candidateReports);
    }

    private static void AddSelected(
        QuestBoardPolicyResolvePolicyReport policy,
        QuestBoardPolicyResolveCandidateReport candidate,
        string reason,
        List<string> selectedQuestIds,
        HashSet<string> selectedQuestIdSet,
        List<QuestBoardPolicyMaterializeCandidateReport> candidateReports)
    {
        selectedQuestIds.Add(candidate.EffectiveQuestId);
        selectedQuestIdSet.Add(candidate.EffectiveQuestId);
        AddCandidateReport(policy, candidate, "selected", reason, candidateReports);
    }

    private static void AddCandidateReport(
        QuestBoardPolicyResolvePolicyReport policy,
        QuestBoardPolicyResolveCandidateReport candidate,
        string materializeStatus,
        string reason,
        List<QuestBoardPolicyMaterializeCandidateReport> candidateReports)
    {
        candidateReports.Add(new QuestBoardPolicyMaterializeCandidateReport(
            policy.PluginId,
            policy.PluginName,
            policy.ManifestPath,
            policy.RuleIndex,
            policy.Id,
            policy.Name,
            policy.Mode,
            candidate.Index,
            candidate.Id,
            candidate.QuestId,
            candidate.SourceQuestId,
            candidate.EffectiveQuestId,
            candidate.Pool,
            candidate.Weight,
            candidate.OnCompleted,
            candidate.ResolutionStatus,
            materializeStatus,
            [reason]));
    }

    private static bool IsSelectableCandidate(QuestBoardPolicyResolveCandidateReport candidate)
    {
        return candidate.ResolutionStatus is "active" or "eligiblePoolCandidate";
    }

    private static bool HasSlot(IReadOnlyList<string> selectedQuestIds, int? slotLimit)
    {
        return !slotLimit.HasValue || selectedQuestIds.Count < slotLimit.Value;
    }

    private static QuestBoardPolicyResolveCandidateReport DrawWeighted(
        IReadOnlyList<QuestBoardPolicyResolveCandidateReport> candidates,
        Random random)
    {
        var totalWeight = candidates.Sum(candidate => Math.Max(1, candidate.Weight ?? 1));
        var roll = random.NextDouble() * totalWeight;
        var cumulative = 0.0;
        foreach (var candidate in candidates)
        {
            cumulative += Math.Max(1, candidate.Weight ?? 1);
            if (roll < cumulative)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private static string WriteArtifact(
        RuntimeConfig config,
        QuestBoardPolicyResolveReport resolve,
        QuestBoardPolicyMaterializeSelection selection,
        ManagedActionProducerContract producer,
        int? slotLimit,
        int seed,
        ManagedActionProfileScope profileScope,
        IReadOnlyList<QuestBoardPolicyMaterializeIssue> issues)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref _sequence);
        var directory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(
            directory,
            $"{generatedAt:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{sequence:D4}_questBoardPolicies_questBoard.replaceWithFixedSet.json");

        File.WriteAllText(
            artifactPath,
            BuildArtifact(resolve, selection, producer, slotLimit, seed, profileScope, generatedAt, issues).ToJsonString(JsonOptions),
            Encoding.UTF8);
        return artifactPath;
    }

    private static string WriteEmptyArtifact(
        RuntimeConfig config,
        QuestBoardPolicyResolveReport resolve,
        ManagedActionProducerContract producer,
        int? slotLimit,
        int seed,
        ManagedActionProfileScope profileScope,
        IReadOnlyList<QuestBoardPolicyMaterializeIssue> issues)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref _sequence);
        var directory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(
            directory,
            $"{generatedAt:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{sequence:D4}_questBoardPolicies_questBoard.replaceWithFixedSet.empty.json");

        File.WriteAllText(
            artifactPath,
            BuildEmptyArtifact(resolve, producer, slotLimit, seed, profileScope, generatedAt, issues).ToJsonString(JsonOptions),
            Encoding.UTF8);
        return artifactPath;
    }

    private static JsonObject BuildArtifact(
        QuestBoardPolicyResolveReport resolve,
        QuestBoardPolicyMaterializeSelection selection,
        ManagedActionProducerContract producer,
        int? slotLimit,
        int seed,
        ManagedActionProfileScope profileScope,
        DateTimeOffset generatedAt,
        IReadOnlyList<QuestBoardPolicyMaterializeIssue> issues)
    {
        var questIds = new JsonArray();
        foreach (var questId in selection.SelectedQuestIds)
        {
            questIds.Add(questId);
        }

        var arguments = new JsonObject
        {
            ["target"] = "profile.quest_board",
            ["questIds"] = questIds,
            ["removeCompleted"] = false,
            ["source"] = "questBoardPolicies",
            ["selectionMode"] = SelectionMode,
            ["seed"] = seed,
            ["slotLimit"] = slotLimit,
            ["policies"] = BuildPolicyRows(selection.Policies)
        };

        return new JsonObject
        {
            ["version"] = ManagedActionProducerContractFactory.ArtifactVersion,
            ["generatedAtUtc"] = generatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = "materialized",
            ["eventId"] = "quest.board.policies.materialized",
            ["pluginId"] = "framework.quest_board_policy_materializer",
            ["sourceName"] = "Quest Board Policy Materializer",
            ["sourcePath"] = resolve.ReportPath,
            ["owners"] = BuildOwnerRows(selection.Policies.Select(policy => (policy.PluginId, policy.ManifestPath))),
            ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
            ["loadOrder"] = int.MaxValue,
            ["ruleIndex"] = 0,
            ["ruleId"] = "questBoardPolicies.materialized",
            ["actionIndex"] = 0,
            ["producer"] = producer.ToJson(),
            ["action"] = new JsonObject
            {
                ["type"] = "questBoard.replaceWithFixedSet",
                ["capability"] = "quest_board.replace_with_fixed_set",
                ["risk"] = "managed",
                ["required"] = false
            },
            ["payload"] = new JsonObject
            {
                ["source"] = "questBoardPolicies",
                ["resolveReportPath"] = resolve.ReportPath,
                ["saveStateReportPath"] = resolve.SaveStateReportPath,
                ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
                ["policyCount"] = resolve.PolicyCount,
                ["resolvedQuestCount"] = resolve.ResolvedQuestCount,
                ["selectedQuestCount"] = selection.SelectedQuestIds.Count
            },
            ["issues"] = BuildIssueRows(issues),
            ["plan"] = new JsonObject
            {
                ["kind"] = "questBoard.replaceWithFixedSet",
                ["effect"] = "replaceWithFixedSet",
                ["target"] = "profile.quest_board",
                ["source"] = "questBoardPolicies",
                ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
                ["arguments"] = arguments
            }
        };
    }

    private static JsonObject BuildEmptyArtifact(
        QuestBoardPolicyResolveReport resolve,
        ManagedActionProducerContract producer,
        int? slotLimit,
        int seed,
        ManagedActionProfileScope profileScope,
        DateTimeOffset generatedAt,
        IReadOnlyList<QuestBoardPolicyMaterializeIssue> issues)
    {
        var arguments = new JsonObject
        {
            ["target"] = "profile.quest_board",
            ["questIds"] = new JsonArray(),
            ["removeCompleted"] = false,
            ["source"] = "questBoardPolicies",
            ["selectionMode"] = SelectionMode,
            ["seed"] = seed,
            ["slotLimit"] = slotLimit,
            ["policies"] = BuildResolvePolicyRows(resolve.Policies)
        };

        return new JsonObject
        {
            ["version"] = ManagedActionProducerContractFactory.ArtifactVersion,
            ["generatedAtUtc"] = generatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = "empty",
            ["eventId"] = "quest.board.policies.empty",
            ["pluginId"] = "framework.quest_board_policy_materializer",
            ["sourceName"] = "Quest Board Policy Materializer",
            ["sourcePath"] = resolve.ReportPath,
            ["owners"] = BuildOwnerRows(resolve.Policies.Select(policy => (policy.PluginId, policy.ManifestPath))),
            ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
            ["loadOrder"] = int.MaxValue,
            ["ruleIndex"] = 0,
            ["ruleId"] = "questBoardPolicies.materialized",
            ["actionIndex"] = 0,
            ["producer"] = producer.ToJson(),
            ["action"] = new JsonObject
            {
                ["type"] = "questBoard.replaceWithFixedSet",
                ["capability"] = "quest_board.replace_with_fixed_set",
                ["risk"] = "managed",
                ["required"] = false
            },
            ["payload"] = new JsonObject
            {
                ["source"] = "questBoardPolicies",
                ["resolveReportPath"] = resolve.ReportPath,
                ["saveStateReportPath"] = resolve.SaveStateReportPath,
                ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
                ["policyCount"] = resolve.PolicyCount,
                ["resolvedQuestCount"] = resolve.ResolvedQuestCount,
                ["selectedQuestCount"] = 0
            },
            ["issues"] = BuildIssueRows(issues),
            ["plan"] = new JsonObject
            {
                ["kind"] = "questBoard.replaceWithFixedSet",
                ["effect"] = "replaceWithFixedSet",
                ["target"] = "profile.quest_board",
                ["source"] = "questBoardPolicies",
                ["profileScope"] = ManagedActionProfileScopeResolver.ToJson(profileScope),
                ["arguments"] = arguments
            }
        };
    }

    private static JsonArray BuildPolicyRows(IReadOnlyList<QuestBoardPolicyMaterializePolicyReport> policies)
    {
        var rows = new JsonArray();
        foreach (var policy in policies)
        {
            var selectedQuestIds = new JsonArray();
            foreach (var questId in policy.SelectedQuestIds)
            {
                selectedQuestIds.Add(questId);
            }

            rows.Add(new JsonObject
            {
                ["pluginId"] = policy.PluginId,
                ["sourcePath"] = policy.ManifestPath,
                ["ruleIndex"] = policy.RuleIndex,
                ["policyId"] = policy.Id,
                ["mode"] = policy.Mode,
                ["status"] = policy.Status,
                ["selectedQuestIds"] = selectedQuestIds
            });
        }

        return rows;
    }

    private static JsonArray BuildResolvePolicyRows(IReadOnlyList<QuestBoardPolicyResolvePolicyReport> policies)
    {
        var rows = new JsonArray();
        foreach (var policy in policies)
        {
            rows.Add(new JsonObject
            {
                ["pluginId"] = policy.PluginId,
                ["sourcePath"] = policy.ManifestPath,
                ["ruleIndex"] = policy.RuleIndex,
                ["policyId"] = policy.Id,
                ["mode"] = policy.Mode,
                ["status"] = policy.Status,
                ["selectedQuestIds"] = new JsonArray()
            });
        }

        return rows;
    }

    private static JsonArray BuildOwnerRows(IEnumerable<(string PluginId, string SourcePath)> owners)
    {
        var rows = new JsonArray();
        foreach (var owner in owners
                     .Distinct()
                     .OrderBy(owner => owner.PluginId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(owner => owner.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new JsonObject
            {
                ["pluginId"] = owner.PluginId,
                ["sourcePath"] = owner.SourcePath
            });
        }

        return rows;
    }

    private static JsonArray BuildIssueRows(IReadOnlyList<QuestBoardPolicyMaterializeIssue> issues)
    {
        var rows = new JsonArray();
        foreach (var issue in issues)
        {
            rows.Add(new JsonObject
            {
                ["severity"] = issue.Severity,
                ["code"] = issue.Code,
                ["pluginId"] = issue.PluginId,
                ["policyId"] = issue.PolicyId,
                ["entryId"] = issue.EntryId,
                ["message"] = issue.Message
            });
        }

        return rows;
    }

    private static string BuildSyntheticPoolKey(QuestBoardPolicyResolvePolicyReport policy)
    {
        return $"__policy_random__:{policy.PluginId}:{policy.Id}";
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "\"\"";
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed record QuestBoardPolicyMaterializeSelection(
        IReadOnlyList<string> SelectedQuestIds,
        IReadOnlyList<QuestBoardPolicyMaterializePolicyReport> Policies,
        IReadOnlyList<QuestBoardPolicyMaterializeCandidateReport> Candidates,
        int PoolCount,
        int DuplicateSkippedCount,
        int SlotLimitSkippedCount)
    {
        public static QuestBoardPolicyMaterializeSelection Empty { get; } = new([], [], [], 0, 0, 0);
    }
}

internal sealed record QuestBoardPolicyMaterializeReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string ResolveReportPath,
    string SaveStateReportPath,
    ManagedActionProfileScope ProfileScope,
    string Status,
    string ArtifactPath,
    string SelectionMode,
    int Seed,
    int? SlotLimit,
    int PolicyCount,
    int ResolvedQuestCount,
    int SelectedQuestCount,
    int PoolCount,
    int DuplicateSkippedCount,
    int SlotLimitSkippedCount,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<string> SelectedQuestIds,
    IReadOnlyList<QuestBoardPolicyMaterializePolicyReport> Policies,
    IReadOnlyList<QuestBoardPolicyMaterializeCandidateReport> Candidates,
    IReadOnlyList<QuestBoardPolicyMaterializeIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardPolicyMaterializePolicyReport(
    string PluginId,
    string PluginName,
    string ManifestPath,
    int RuleIndex,
    string Id,
    string Name,
    string Mode,
    string Status,
    int CandidateCount,
    int SelectedQuestCount,
    IReadOnlyList<string> SelectedQuestIds);

internal sealed record QuestBoardPolicyMaterializeCandidateReport(
    string PluginId,
    string PluginName,
    string ManifestPath,
    int RuleIndex,
    string PolicyId,
    string PolicyName,
    string PolicyMode,
    int EntryIndex,
    string EntryId,
    string QuestId,
    string SourceQuestId,
    string EffectiveQuestId,
    string Pool,
    int? Weight,
    string OnCompleted,
    string ResolutionStatus,
    string MaterializeStatus,
    IReadOnlyList<string> Reasons);

internal sealed record QuestBoardPolicyMaterializeIssue(
    string Severity,
    string Code,
    string PluginId,
    string PolicyId,
    string EntryId,
    string Message);
