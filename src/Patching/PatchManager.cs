using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;
using ADOFAI.EditorTweaks.ChartRendering.Features.RenderInputGuard;
using ADOFAI.EditorTweaks.ChartRendering.Features.VideoBackgroundSync;
using HarmonyLib;

namespace ADOFAI.EditorTweaks.ChartRendering.Patching
{
    internal enum PatchFeature { RenderInputGuard, ChartRendering }
    internal enum PatchGroupState { Inactive, Active, Failed, Blocked }

    internal sealed class PatchGroupStatus
    {
        public PatchGroupStatus(PatchFeature feature, string name) { Feature = feature; Name = name; }
        public PatchFeature Feature { get; }
        public string Name { get; }
        public PatchGroupState State { get; internal set; }
        public int PatchCount { get; internal set; }
        public string Reason { get; internal set; } = string.Empty;
    }

    internal sealed class PatchGroupDefinition
    {
        public PatchGroupDefinition(PatchFeature feature, string id, string name, Type[] roots, PatchFeature[] dependencies)
        { Feature = feature; Id = id; Name = name; Roots = roots; Dependencies = dependencies; }
        public PatchFeature Feature { get; }
        public string Id { get; }
        public string Name { get; }
        public Type[] Roots { get; }
        public PatchFeature[] Dependencies { get; }
    }

    internal static class PatchManager
    {
        private static readonly PatchGroupDefinition[] definitions =
        {
            new PatchGroupDefinition(PatchFeature.RenderInputGuard, "render-input-guard", "Render input guard",
                new[] { typeof(RenderInputGuardPatches) }, new PatchFeature[0]),
            new PatchGroupDefinition(PatchFeature.ChartRendering, "chart-rendering", "Chart rendering",
                new[]
                {
                    typeof(ChartRenderVisualClock), typeof(ChartRenderAutoPlayer), typeof(ChartRenderAudioPatches),
                    typeof(ChartRenderJudgmentPatches), typeof(ChartRenderAudioBufferCheckPatch),
                    typeof(ChartRenderCustomFrameRate), typeof(VideoBackgroundSyncPatches)
                }, new[] { PatchFeature.RenderInputGuard })
        };

        private static readonly Dictionary<PatchFeature, PatchGroupStatus> statuses = new Dictionary<PatchFeature, PatchGroupStatus>();
        private static readonly Dictionary<PatchFeature, Harmony> activeHarmonies = new Dictionary<PatchFeature, Harmony>();
        private static readonly Dictionary<PatchFeature, string> activeHarmonyIds = new Dictionary<PatchFeature, string>();

        public static IReadOnlyList<PatchGroupStatus> Statuses => definitions.Select(GetOrCreateStatus).ToArray();

        public static IReadOnlyList<PatchGroupStatus> ApplyAll(string modId)
        {
            ClearActivePatches();
            statuses.Clear();
            foreach (PatchGroupDefinition definition in definitions)
            {
                PatchGroupStatus status = GetOrCreateStatus(definition);
                PatchFeature? missing = null;
                foreach (PatchFeature dependency in definition.Dependencies)
                {
                    if (!IsAvailable(dependency))
                    {
                        missing = dependency;
                        break;
                    }
                }
                if (missing.HasValue)
                {
                    status.State = PatchGroupState.Blocked;
                    status.Reason = "Required patch group is unavailable.";
                    continue;
                }

                List<Type> patchTypes = new List<Type>();
                foreach (Type root in definition.Roots) CollectPatchTypes(root, patchTypes, new HashSet<Type>());
                status.PatchCount = patchTypes.Count;
                string harmonyId = modId + "." + definition.Id;
                Harmony harmony = new Harmony(harmonyId);
                try
                {
                    foreach (Type patchType in patchTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
                        harmony.CreateClassProcessor(patchType).Patch();
                    activeHarmonies[definition.Feature] = harmony;
                    activeHarmonyIds[definition.Feature] = harmonyId;
                    status.State = PatchGroupState.Active;
                    Main.Log("[PatchManager] Group '" + definition.Id + "' active (" + patchTypes.Count + " patches).");
                }
                catch (Exception exception)
                {
                    try { harmony.UnpatchAll(harmonyId); } catch { }
                    status.State = PatchGroupState.Failed;
                    status.Reason = exception.GetBaseException().Message;
                    Main.Mod?.Logger.Error("[PatchManager] Group '" + definition.Id + "' failed: " + exception);
                }
            }
            return Statuses;
        }

        public static void UnpatchAll()
        {
            ClearActivePatches();
            foreach (PatchGroupStatus status in statuses.Values)
            {
                status.State = PatchGroupState.Inactive;
                status.Reason = string.Empty;
            }
        }

        public static bool IsAvailable(PatchFeature feature)
        {
            return statuses.TryGetValue(feature, out PatchGroupStatus status) && status.State == PatchGroupState.Active;
        }

        public static string GetSummary()
        {
            return statuses.Values.Count(item => item.State == PatchGroupState.Active) + "/" + definitions.Length + " 渲染功能组可用";
        }

        private static PatchGroupStatus GetOrCreateStatus(PatchGroupDefinition definition)
        {
            if (!statuses.TryGetValue(definition.Feature, out PatchGroupStatus status))
            {
                status = new PatchGroupStatus(definition.Feature, definition.Name);
                statuses[definition.Feature] = status;
            }
            return status;
        }

        private static void CollectPatchTypes(Type type, ICollection<Type> result, ISet<Type> visited)
        {
            if (!visited.Add(type)) return;
            if (type.IsDefined(typeof(HarmonyPatch), true)) result.Add(type);
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                CollectPatchTypes(nested, result, visited);
        }

        private static void ClearActivePatches()
        {
            foreach (KeyValuePair<PatchFeature, Harmony> entry in activeHarmonies)
            {
                if (activeHarmonyIds.TryGetValue(entry.Key, out string harmonyId))
                {
                    try { entry.Value.UnpatchAll(harmonyId); } catch (Exception exception) { Main.Mod?.Logger.Error(exception.ToString()); }
                }
            }
            activeHarmonies.Clear();
            activeHarmonyIds.Clear();
        }
    }
}
