using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Optional clock adapter for third-party key viewers that animate with wall-clock time.
    // This class handles JRP (JipperResourcePack) and drives the separate JKV and Quartz adapters
    // (ChartRenderJkvAdapter, ChartRenderQuartzAdapter) from the same render lifecycle.
    //
    // Jipper KeyViewer reads Stopwatch for rain/KPS. During an offline render one output frame
    // represents exactly 1/fps of video regardless of how long encoding takes, so every clock read
    // and every input-edge timestamp is mapped to requestedFrameIndex/fps. Outside a render the
    // adapted clock continues at real-time speed from the handoff point, keeping existing rain and
    // KPS timestamps valid instead of jumping backwards.
    internal static class ChartRenderCompatibility
    {
        private const string HarmonyId = "ADOFAI.EditorTweaks.ChartRendering.RenderTimeCompat";
        private const double TickRate = TimeSpan.TicksPerSecond;
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, JipperPressTimestamp> JipperPresses =
            new Dictionary<int, JipperPressTimestamp>();

        private struct JipperPressTimestamp
        {
            public double Real;
            public double Mapped;
        }

        private static Harmony? harmony;
        private static bool adapterReady;
        private static volatile bool active;
        private static bool discoveryEnabled;
        private static bool baselineSet;
        private static bool clockInitialized;
        private static double baseMappedTicks;
        private static double baseVirtualTicks;
        private static double realtimeOriginRealTicks;
        private static double realtimeOriginMappedTicks;
        private static double virtualTicks;
        private static double frameTicks;
        private static int lastFrameIndex = -1;
        private static int clockReads;
        private static int inputEvents;
        private static ChartOffscreenGameViewCapture? offscreenCapture;

        private static MethodInfo? jipperCurrentTicks;
        private static MethodInfo? jipperReplacement;
        private static Type? jipperRainManagerType;
        private static FieldInfo? jipperRainListField;
        private static FieldInfo? jipperRainImageField;
        private static int textureFixes;
        private static bool textureFixLogged;
        private static Func<long>? jipperTicksLong;
        private static Func<ulong>? jipperTicksUlong;

        // Jipper creates Rain UI objects lazily on the first key press. If that first creation happens
        // after the canvas has been switched to ScreenSpaceCamera for offscreen capture, the pooled
        // RawImage objects can remain invisible for the rest of the session. Create a small pool while
        // the canvas is still using its normal Overlay mode, before ChartOffscreenGameViewCapture runs.
        //
        // A late-load (Jipper is first initialised in the middle of an offscreen render) needs the same
        // treatment afterwards: the pooled objects it created while the canvas was already in camera
        // mode are invisible, so they are destroyed and replaced while the canvas is temporarily put
        // back into Overlay mode.
        public static void PrewarmJipperRainBeforeOffscreen()
        {
            PrewarmJipperRain(purgePooled: false, tag: "JRP_PREWARM");
        }

        public static void SetOffscreenCapture(ChartOffscreenGameViewCapture? capture)
        {
            offscreenCapture = capture;
        }

        private static void PrewarmJipperRain(bool purgePooled, string tag)
        {
            if (Main.Settings != null && !Main.Settings.ChartRenderCompatKeyViewers)
            {
                return;
            }

            try
            {
                Type? keyViewer = AccessTools.TypeByName("JipperResourcePack.KeyViewerContents.KeyViewer");
                if (keyViewer == null)
                {
                    if (purgePooled)
                    {
                        ChartRenderDiagnostics.Log(tag + " skipped: Jipper KeyViewer is not loaded.");
                    }

                    return;
                }

                object? instance = AccessTools.Field(keyViewer, "Instance")?.GetValue(null);
                if (instance == null || !(AccessTools.Field(keyViewer, "Keys")?.GetValue(instance) is Array keys))
                {
                    return;
                }

                HashSet<object> pools = new HashSet<object>();
                int normalCreated = 0;
                int ghostCreated = 0;
                int pooledDestroyed = 0;
                int restoredCanvases = 0;
                foreach (object? key in keys)
                {
                    if (key == null)
                    {
                        continue;
                    }

                    object? pool = AccessTools.Field(key.GetType(), "RainPool")?.GetValue(key);
                    if (pool == null || !pools.Add(pool))
                    {
                        continue;
                    }

                    MethodInfo? getRain = AccessTools.Method(pool.GetType(), "GetOrNewRain", new[] { typeof(bool) });
                    MethodInfo? addPool = AccessTools.Method(pool.GetType(), "AddPool");
                    if (getRain == null || addPool == null)
                    {
                        continue;
                    }

                    if (purgePooled)
                    {
                        pooledDestroyed += PurgePooledJipperRain(pool);
                    }

                    int[] counts = { 0, 0 };
                    Action create = () => CreateJipperRainBatch(pool, getRain, addPool, counts);
                    bool restored = false;
                    if (offscreenCapture != null)
                    {
                        RectTransform? poolTransform = AccessTools.Field(pool.GetType(), "Transform")?.GetValue(pool) as RectTransform;
                        Canvas? canvas = poolTransform == null ? null : poolTransform.GetComponentInParent<Canvas>();
                        if (canvas != null)
                        {
                            restored = offscreenCapture.TryRunWithCanvasRestored(canvas.rootCanvas, create);
                        }
                    }

                    if (!restored)
                    {
                        create();
                    }
                    else
                    {
                        restoredCanvases++;
                    }

                    normalCreated += counts[0];
                    ghostCreated += counts[1];
                }

                ChartRenderDiagnostics.Log(tag + " pools=" + pools.Count
                    + " normal=" + normalCreated + " ghost=" + ghostCreated
                    + (purgePooled ? " purged=" + pooledDestroyed : string.Empty)
                    + (restoredCanvases > 0 ? " restoredCanvases=" + restoredCanvases : string.Empty)
                    + ".");
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log(tag + " failed: " + exception.Message);
            }
        }

        private static void CreateJipperRainBatch(object pool, MethodInfo getRain, MethodInfo addPool, int[] counts)
        {
            List<object> created = new List<object>();
            for (int i = 0; i < 32; i++)
            {
                object? rain = getRain.Invoke(pool, new object[] { false });
                if (rain != null)
                {
                    created.Add(rain);
                }
            }

            foreach (object rain in created)
            {
                addPool.Invoke(pool, new[] { rain, (object)false });
                counts[0]++;
            }

            created.Clear();
            for (int i = 0; i < 16; i++)
            {
                object? rain = getRain.Invoke(pool, new object[] { true });
                if (rain != null)
                {
                    created.Add(rain);
                }
            }

            foreach (object rain in created)
            {
                addPool.Invoke(pool, new[] { rain, (object)true });
                counts[1]++;
            }
        }

        // Only pooled (inactive) rains are destroyed; active ones are still in RainList and are left
        // to fade out normally.
        private static int PurgePooledJipperRain(object pool)
        {
            int destroyed = 0;
            Type type = pool.GetType();
            foreach (string arrayName in new[] { "_pool", "_ghostPool" })
            {
                FieldInfo? arrayField = AccessTools.Field(type, arrayName);
                FieldInfo? countField = AccessTools.Field(type, arrayName == "_pool" ? "_poolCount" : "_ghostPoolCount");
                if (arrayField == null || countField == null)
                {
                    continue;
                }

                if (!(arrayField.GetValue(pool) is Array array) || !(countField.GetValue(pool) is int count))
                {
                    continue;
                }

                for (int i = 0; i < count && i < array.Length; i++)
                {
                    object? rain = array.GetValue(i);
                    if (rain == null)
                    {
                        continue;
                    }

                    if (AccessTools.Field(rain.GetType(), "GameObject")?.GetValue(rain) is GameObject rainObject && rainObject != null)
                    {
                        UnityEngine.Object.Destroy(rainObject);
                        destroyed++;
                    }

                    array.SetValue(null, i);
                }

                countField.SetValue(pool, 0);
            }

            return destroyed;
        }

        public static void Begin(int fps, bool capturesGameView)
        {
            lock (Sync)
            {
                active = false;
                discoveryEnabled = false;
                baselineSet = false;
                virtualTicks = 0.0;
                frameTicks = TickRate / Math.Max(1, fps);
                lastFrameIndex = 0;
                clockReads = 0;
                inputEvents = 0;
                JipperPresses.Clear();

                if (!capturesGameView || (Main.Settings != null && !Main.Settings.ChartRenderCompatKeyViewers))
                {
                    return;
                }

                discoveryEnabled = true;
                // JKV and Quartz have their own adapters; each fails on its own without affecting JRP.
                RunOtherAdapters(() => ChartRenderJkvAdapter.Begin(fps), "JKV begin");
                RunOtherAdapters(() => ChartRenderQuartzAdapter.Begin(fps), "Quartz begin");
                if (!adapterReady)
                {
                    ApplyJipperKeyViewerAdapter();
                }

                if (!adapterReady)
                {
                    return;
                }

                active = true;
                ChartRenderDiagnostics.Log("Jipper KeyViewer render clock enabled. fps=" + Math.Max(1, fps) + ".");
            }
        }

        public static void Tick(int frameIndex)
        {
            RunOtherAdapters(() => ChartRenderJkvAdapter.Tick(frameIndex), "JKV tick");
            RunOtherAdapters(() => ChartRenderQuartzAdapter.Tick(frameIndex), "Quartz tick");
            lock (Sync)
            {
                lastFrameIndex = Math.Max(0, frameIndex);
                virtualTicks = lastFrameIndex * frameTicks;

                // Jipper/JALib loads asynchronously and may finish after rendering has begun.
                if (!adapterReady && discoveryEnabled && (lastFrameIndex == 0 || lastFrameIndex % 300 == 0))
                {
                    ApplyJipperKeyViewerAdapter();
                    if (adapterReady)
                    {
                        active = true;
                        baselineSet = false;
                        ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility adapter late-loaded at frame "
                            + lastFrameIndex + ".");
                        if (offscreenCapture != null)
                        {
                            // Its first rain objects were created while the canvas was already in offscreen
                            // camera mode, so they can stay invisible. Replace them under Overlay mode.
                            ChartRenderDiagnostics.Log("Jipper KeyViewer loaded during an offscreen render; rebuilding its rain pool.");
                            PrewarmJipperRain(purgePooled: true, tag: "JRP_LATE_PREWARM");
                        }
                    }
                }
            }
        }

        public static void End()
        {
            RunOtherAdapters(ChartRenderJkvAdapter.End, "JKV end");
            RunOtherAdapters(ChartRenderQuartzAdapter.End, "Quartz end");
            lock (Sync)
            {
                if (active)
                {
                    double real = ReadJipperTicksLocked();
                    double mapped = MapTicksLocked(real);
                    realtimeOriginRealTicks = real;
                    realtimeOriginMappedTicks = mapped;
                    clockInitialized = true;
                    ChartRenderDiagnostics.Log("Jipper KeyViewer render clock ended. frames=" + lastFrameIndex
                        + " videoSeconds=" + (virtualTicks / TickRate).ToString("0.###")
                        + " clockReads=" + clockReads
                        + " inputEvents=" + inputEvents
                        + " textureFixes=" + textureFixes
                        + " handoff=continuous.");
                }

                active = false;
                discoveryEnabled = false;
                baselineSet = false;
                virtualTicks = 0.0;
                JipperPresses.Clear();
            }
        }

        public static void Shutdown()
        {
            RunOtherAdapters(ChartRenderJkvAdapter.Shutdown, "JKV shutdown");
            RunOtherAdapters(ChartRenderQuartzAdapter.Shutdown, "Quartz shutdown");
            lock (Sync)
            {
                active = false;
                discoveryEnabled = false;
                baselineSet = false;
                clockInitialized = false;
                realtimeOriginRealTicks = 0.0;
                realtimeOriginMappedTicks = 0.0;
                JipperPresses.Clear();
                textureFixes = 0;
                textureFixLogged = false;
                Harmony? oldHarmony = harmony;
                ResetJipperAdapter();
                if (oldHarmony != null)
                {
                    try
                    {
                        oldHarmony.UnpatchAll(HarmonyId);
                    }
                    catch (Exception exception)
                    {
                        Main.Log("Could not remove the Jipper KeyViewer compatibility adapter: " + exception.Message);
                    }
                }
            }
        }

        private static void RunOtherAdapters(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log("Key viewer compatibility (" + what + ") failed: " + exception.Message);
            }
        }

        private static void ApplyJipperKeyViewerAdapter()
        {
            try
            {
                Type? keyViewer = AccessTools.TypeByName("JipperResourcePack.KeyViewerContents.KeyViewer");
                if (keyViewer == null)
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: KeyViewer is not loaded.");
                    return;
                }

                MethodInfo? currentTicks = AccessTools.PropertyGetter(keyViewer, "CurrentTicks");
                if (currentTicks == null)
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: CurrentTicks was not found.");
                    return;
                }

                if (currentTicks.ReturnType == typeof(long))
                {
                    jipperTicksLong = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), currentTicks);
                    jipperReplacement = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(RenderJipperTicksLong));
                }
                else if (currentTicks.ReturnType == typeof(ulong))
                {
                    jipperTicksUlong = (Func<ulong>)Delegate.CreateDelegate(typeof(Func<ulong>), currentTicks);
                    jipperReplacement = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(RenderJipperTicksUlong));
                }
                else
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: unsupported CurrentTicks type "
                        + currentTicks.ReturnType.Name + ".");
                    ResetJipperAdapter();
                    return;
                }

                MethodInfo? work = AccessTools.Method(keyViewer, "Work",
                    new[] { typeof(int[]), typeof(bool), currentTicks.ReturnType });
                MethodInfo? transpiler = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(JipperCurrentTicksTranspiler));
                MethodInfo? workPrefix = AccessTools.Method(typeof(ChartRenderCompatibility),
                    currentTicks.ReturnType == typeof(long) ? nameof(JipperWorkPrefixLong) : nameof(JipperWorkPrefixUlong));
                if (work == null || transpiler == null || workPrefix == null || jipperReplacement == null)
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: Work or adapter methods were not found.");
                    ResetJipperAdapter();
                    return;
                }

                jipperRainManagerType = AccessTools.TypeByName("JipperResourcePack.KeyViewerContents.RainManager");
                List<MethodInfo> clockReaders = new List<MethodInfo>();
                AddTarget(clockReaders, AccessTools.Inner(keyViewer, "KeyViewerUpdater"), "Update");
                AddTarget(clockReaders, jipperRainManagerType, "Update");
                if (clockReaders.Count != 2)
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: the two update call sites were not found.");
                    ResetJipperAdapter();
                    return;
                }

                Type? rainType = AccessTools.TypeByName("JipperResourcePack.KeyViewerContents.Rain");
                jipperRainListField = jipperRainManagerType == null ? null : AccessTools.Field(jipperRainManagerType, "RainList");
                jipperRainImageField = rainType == null ? null : AccessTools.Field(rainType, "Image");
                MethodInfo? rainPostfix = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(JipperRainPostfix));

                jipperCurrentTicks = currentTicks;
                Harmony candidate = new Harmony(HarmonyId);
                try
                {
                    foreach (MethodInfo method in clockReaders)
                    {
                        bool isRain = jipperRainManagerType != null && method.DeclaringType == jipperRainManagerType;
                        if (isRain && rainPostfix != null)
                        {
                            candidate.Patch(method,
                                transpiler: new HarmonyMethod(transpiler),
                                postfix: new HarmonyMethod(rainPostfix));
                        }
                        else
                        {
                            candidate.Patch(method, transpiler: new HarmonyMethod(transpiler));
                        }
                    }

                    // Work is where press/release timestamps become rain start/finish times and KPS
                    // entries. Do not patch ToStopwatchTicks: its real-clock event offset must remain
                    // untouched for normal input processing.
                    candidate.Patch(work, prefix: new HarmonyMethod(workPrefix));

                    harmony = candidate;
                    adapterReady = true;
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility adapter ready (2 clock readers + Work prefix).");
                }
                catch (Exception exception)
                {
                    candidate.UnpatchAll(HarmonyId);
                    ResetJipperAdapter();
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility rolled back: " + exception);
                }
            }
            catch (Exception exception)
            {
                ResetJipperAdapter();
                ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility failed; rendering continues without it: " + exception);
            }
        }

        private static void ResetJipperAdapter()
        {
            harmony = null;
            adapterReady = false;
            jipperCurrentTicks = null;
            jipperReplacement = null;
            jipperRainManagerType = null;
            jipperRainListField = null;
            jipperRainImageField = null;
            jipperTicksLong = null;
            jipperTicksUlong = null;
        }

        private static void AddTarget(List<MethodInfo> targets, Type? type, string methodName)
        {
            MethodInfo? method = type == null ? null : AccessTools.Method(type, methodName);
            if (method != null && !targets.Contains(method))
            {
                targets.Add(method);
            }
        }

        private static void JipperRainPostfix(object __instance)
        {
            if ((!active && !clockInitialized) || jipperRainListField == null || jipperRainImageField == null)
            {
                return;
            }

            IList? rainList;
            try
            {
                rainList = jipperRainListField.GetValue(__instance) as IList;
            }
            catch
            {
                return;
            }

            if (rainList == null)
            {
                return;
            }

            for (int i = 0; i < rainList.Count; i++)
            {
                object? rain = rainList[i];
                if (rain == null || !(jipperRainImageField.GetValue(rain) is RawImage rawImage))
                {
                    continue;
                }

                bool changed = false;
                if (rawImage.texture == null)
                {
                    rawImage.texture = Texture2D.whiteTexture;
                    changed = true;
                    textureFixes++;
                }

                if (changed || active)
                {
                    rawImage.SetAllDirty();
                }
            }

            if (textureFixes > 0 && !textureFixLogged)
            {
                textureFixLogged = true;
                ChartRenderDiagnostics.Log("Jipper rain RawImage texture fallback applied.");
            }
        }

        private static IEnumerable<CodeInstruction> JipperCurrentTicksTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (jipperCurrentTicks != null && jipperReplacement != null && instruction.Calls(jipperCurrentTicks))
                {
                    instruction.operand = jipperReplacement;
                }

                yield return instruction;
            }
        }

        public static long RenderJipperTicksLong()
        {
            long real = jipperTicksLong == null ? 0L : jipperTicksLong();
            lock (Sync)
            {
                clockReads++;
                return ToLong(MapTicksLocked(real));
            }
        }

        public static ulong RenderJipperTicksUlong()
        {
            ulong real = jipperTicksUlong == null ? 0UL : jipperTicksUlong();
            lock (Sync)
            {
                clockReads++;
                return ToUlong(MapTicksLocked(real));
            }
        }

        private static void JipperWorkPrefixLong(int[] __0, bool __1, ref long __2)
        {
            lock (Sync)
            {
                if (jipperTicksLong != null)
                {
                    __2 = ToLong(MapJipperInputEventLocked(__0, __1, __2));
                }
            }
        }

        private static void JipperWorkPrefixUlong(int[] __0, bool __1, ref ulong __2)
        {
            lock (Sync)
            {
                if (jipperTicksUlong != null)
                {
                    __2 = ToUlong(MapJipperInputEventLocked(__0, __1, __2));
                }
            }
        }

        private static double MapJipperInputEventLocked(int[] indexes, bool pressed, double realEventTicks)
        {
            if (!active)
            {
                return clockInitialized ? MapTicksLocked(realEventTicks) : realEventTicks;
            }

            inputEvents++;
            double mappedNow = MapTicksLocked(ReadJipperTicksLocked());
            if (indexes == null || indexes.Length == 0)
            {
                return mappedNow;
            }

            if (pressed)
            {
                foreach (int index in indexes)
                {
                    if (!JipperPresses.ContainsKey(index))
                    {
                        JipperPresses.Add(index, new JipperPressTimestamp
                        {
                            Real = realEventTicks,
                            Mapped = mappedNow
                        });
                    }
                }

                return mappedNow;
            }

            bool foundPress = false;
            double realPress = 0.0;
            double mappedPress = 0.0;
            foreach (int index in indexes)
            {
                if (JipperPresses.TryGetValue(index, out JipperPressTimestamp timestamp))
                {
                    if (!foundPress)
                    {
                        foundPress = true;
                        realPress = timestamp.Real;
                        mappedPress = timestamp.Mapped;
                    }

                    JipperPresses.Remove(index);
                }
            }

            if (foundPress && mappedNow <= mappedPress)
            {
                double realDuration = Math.Max(0.0, realEventTicks - realPress);
                double preservedDuration = realDuration <= 0.0
                    ? frameTicks
                    : Math.Min(frameTicks, realDuration);
                return mappedPress + preservedDuration;
            }

            return mappedNow;
        }

        private static double ReadJipperTicksLocked()
        {
            if (jipperTicksLong != null)
            {
                return jipperTicksLong();
            }

            return jipperTicksUlong == null ? 0.0 : jipperTicksUlong();
        }

        private static double MapTicksLocked(double realTicks)
        {
            if (active)
            {
                if (!baselineSet)
                {
                    baseMappedTicks = clockInitialized
                        ? realtimeOriginMappedTicks + realTicks - realtimeOriginRealTicks
                        : realTicks;
                    baseVirtualTicks = virtualTicks;
                    baselineSet = true;
                }

                return Math.Max(0.0, baseMappedTicks + virtualTicks - baseVirtualTicks);
            }

            return clockInitialized
                ? Math.Max(0.0, realtimeOriginMappedTicks + realTicks - realtimeOriginRealTicks)
                : realTicks;
        }

        private static long ToLong(double ticks)
        {
            return (long)Math.Round(Math.Max(0.0, Math.Min(long.MaxValue, ticks)));
        }

        private static ulong ToUlong(double ticks)
        {
            return (ulong)Math.Round(Math.Max(0.0, Math.Min(ulong.MaxValue, ticks)));
        }
    }
}
