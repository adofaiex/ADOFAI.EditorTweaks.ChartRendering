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
        private static Type? jipperKeyViewerType;
        private static Type? boundJipperKeyViewerType;
        private static Type? jipperRainManagerType;
        private static FieldInfo? jipperRainListField;
        private static FieldInfo? jipperRainImageField;
        private static FieldInfo? jipperRainPoolCountField;
        private static FieldInfo? jipperRainPoolGhostField;
        private static int textureFixes;
        private static bool textureFixLogged;
        private static Func<long>? jipperTicksLong;
        private static Func<ulong>? jipperTicksUlong;

        private const string JipperAssemblyName = "JipperResourcePack";
        private const string JipperKeyViewerTypeName = "JipperResourcePack.KeyViewerContents.KeyViewer";
        // How often an offscreen render looks for JRP rain pools it has not prepared yet. JRP is
        // enabled asynchronously by JALib and may appear in the middle of a render.
        private const int JipperPoolCheckFrames = 30;
        private static readonly HashSet<object> PreparedJipperPools = new HashSet<object>();
        private static readonly HashSet<object> RepurgeJipperPools = new HashSet<object>();
        private static int jipperRepurgesLeft;
        private static int nextJipperRepurgeFrame;
        private static int renderFps = 60;

        // The initial pool size follows the rain lifetime (rainHeight * 0.3 / rainSpeed seconds) so a
        // slow or tall rain setting gets a bigger pool instead of running dry in the first seconds.
        // 60 presses per second per pool (two keys) is a deliberately generous upper bound; the size
        // is clamped so an extreme setting cannot allocate thousands of UI objects at render start.
        private const int JipperPoolMinNormal = 32;
        private const int JipperPoolMaxNormal = 96;
        private const int JipperPoolMinGhost = 16;
        // Runtime top-up: a pool below the low-water mark gets a batch while the canvas is restored to
        // Overlay. Checking every frame leaves several frames of headroom because each key can press
        // at most once per output frame, and caps the number of top-ups per pool per render.
        private const int JipperPoolLowWaterNormal = 8;
        private const int JipperPoolLowWaterGhost = 4;
        private const int JipperPoolTopUpNormal = 32;
        private const int JipperPoolTopUpGhost = 16;
        private const int JipperPoolMaxTopUps = 4;
        private static int jipperPoolNormal = JipperPoolMinNormal;
        private static int jipperPoolGhost = JipperPoolMinGhost;
        private static readonly Dictionary<object, int> JipperPoolTopUps = new Dictionary<object, int>();
        private static List<object>? currentJipperPools;
        private static int jipperRainAllocations;
        private static bool jipperCreatingRains;

        // Jipper creates Rain UI objects lazily on the first key press. If that first creation happens
        // after the canvas has been switched to ScreenSpaceCamera for offscreen capture, the pooled
        // RawImage objects can remain invisible for the rest of the session. Create a small pool while
        // the canvas is still using its normal Overlay mode, before ChartOffscreenGameViewCapture runs.
        //
        // Pools that appear later (JRP finished loading mid-render, or rebuilt its keys) are found by
        // Tick, and their rains are destroyed and recreated while the canvas is temporarily put back
        // into Overlay mode. Rains that were already on screen at that moment go back to the pool
        // later and would be reused first (the pool is a stack), so the same pools are rebuilt again
        // 5 and 15 seconds later.
        public static void PrewarmJipperRainBeforeOffscreen()
        {
            lock (Sync)
            {
                PreparedJipperPools.Clear();
                RepurgeJipperPools.Clear();
                JipperPoolTopUps.Clear();
                currentJipperPools = null;
                jipperRainAllocations = 0;
                jipperRepurgesLeft = 0;
                if (Main.Settings != null && !Main.Settings.ChartRenderCompatKeyViewers)
                {
                    return;
                }

                List<object>? pools = GetJipperPools();
                if (pools == null)
                {
                    // Not loaded yet; Tick prepares the pools once they appear.
                    ChartRenderDiagnostics.Log("JRP_PREWARM skipped: JRP KeyViewer is not loaded at render start.");
                    return;
                }

                PrepareJipperPools(pools, purgePooled: false, tag: "JRP_PREWARM");
            }
        }

        public static void SetOffscreenCapture(ChartOffscreenGameViewCapture? capture)
        {
            lock (Sync)
            {
                offscreenCapture = capture;
            }
        }

        private static Type? FindJipperKeyViewerType()
        {
            // JALib may rewrite a mod into a cache assembly named "<mod>-JAMod" before loading it
            // (JipperResourcePack becomes JipperResourcePack-JAMod), so the assembly name is matched
            // by prefix. When both the original and the rewritten copy are loaded, prefer the one
            // that owns the live KeyViewer instance.
            Type? cached = jipperKeyViewerType;
            if (cached != null && JipperInstanceOf(cached) != null)
            {
                return cached;
            }

            Type? fallback = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = assembly.GetName().Name;
                if (name == null || !name.StartsWith(JipperAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type? type = assembly.GetType(JipperKeyViewerTypeName, false);
                if (type == null)
                {
                    continue;
                }

                if (JipperInstanceOf(type) != null)
                {
                    jipperKeyViewerType = type;
                    return type;
                }

                fallback ??= type;
            }

            if (fallback != null)
            {
                jipperKeyViewerType = fallback;
            }

            return fallback;
        }

        private static object? JipperInstanceOf(Type keyViewer)
        {
            try
            {
                object? instance = AccessTools.Field(keyViewer, "Instance")?.GetValue(null);
                return instance is UnityEngine.Object unityObject && unityObject == null ? null : instance;
            }
            catch
            {
                return null;
            }
        }

        // The distinct RainPool objects of the current JRP key viewer, or null while JRP has no key
        // viewer yet. Several keys can share one pool.
        private static List<object>? GetJipperPools()
        {
            Type? keyViewer = FindJipperKeyViewerType();
            if (keyViewer == null)
            {
                return null;
            }

            object? instance = JipperInstanceOf(keyViewer);
            if (instance == null || !(AccessTools.Field(keyViewer, "Keys")?.GetValue(instance) is Array keys))
            {
                return null;
            }

            List<object> pools = new List<object>();
            HashSet<object> seen = new HashSet<object>();
            foreach (object? key in keys)
            {
                object? pool = key == null ? null : AccessTools.Field(key.GetType(), "RainPool")?.GetValue(key);
                if (pool != null && seen.Add(pool))
                {
                    pools.Add(pool);
                }
            }

            return pools;
        }

        // Called from Tick under Sync during offscreen renders.
        private static void CheckJipperPoolsLocked()
        {
            if (lastFrameIndex % JipperPoolCheckFrames == 0)
            {
                List<object>? pools = GetJipperPools();
                currentJipperPools = pools;
                if (pools != null)
                {
                    List<object> fresh = new List<object>();
                    foreach (object pool in pools)
                    {
                        if (!PreparedJipperPools.Contains(pool))
                        {
                            fresh.Add(pool);
                        }
                    }

                    if (fresh.Count > 0)
                    {
                        ChartRenderDiagnostics.Log("JRP rain pools appeared during an offscreen render at frame " + lastFrameIndex
                            + " (" + fresh.Count + " new); rebuilding them under Overlay mode.");
                        PrepareJipperPools(fresh, purgePooled: true, tag: "JRP_LATE_PREWARM");
                        foreach (object pool in fresh)
                        {
                            RepurgeJipperPools.Add(pool);
                        }

                        jipperRepurgesLeft = 2;
                        nextJipperRepurgeFrame = lastFrameIndex + renderFps * 5;
                    }
                }
            }

            if (jipperRepurgesLeft > 0 && lastFrameIndex >= nextJipperRepurgeFrame)
            {
                List<object>? pools = GetJipperPools();
                List<object> again = new List<object>();
                if (pools != null)
                {
                    foreach (object pool in pools)
                    {
                        if (RepurgeJipperPools.Contains(pool))
                        {
                            again.Add(pool);
                        }
                    }
                }

                if (again.Count > 0)
                {
                    PrepareJipperPools(again, purgePooled: true, tag: "JRP_LATE_REPURGE");
                }

                jipperRepurgesLeft--;
                nextJipperRepurgeFrame = lastFrameIndex + renderFps * 10;
            }

            // Checked every frame: each key can press at most once per output frame, so a pool with
            // this much headroom cannot run dry before the next check.
            if (currentJipperPools != null)
            {
                TopUpJipperPoolsLocked(currentJipperPools);
            }
        }

        // Bars created while the canvas is in ScreenSpaceCamera mode stay invisible, so a pool that
        // runs out mid-render leaves permanent holes. Top the pool up while it still has headroom and
        // the canvas can be restored to Overlay for the creation.
        private static void TopUpJipperPoolsLocked(List<object> pools)
        {
            if (offscreenCapture == null)
            {
                return;
            }

            foreach (object pool in pools)
            {
                int normal = ReadJipperPoolCount(pool, jipperRainPoolCountField);
                int ghost = ReadJipperPoolCount(pool, jipperRainPoolGhostField);
                bool needNormal = normal >= 0 && normal < JipperPoolLowWaterNormal;
                bool needGhost = jipperPoolGhost > 0 && ghost >= 0 && ghost < JipperPoolLowWaterGhost;
                if (!needNormal && !needGhost)
                {
                    continue;
                }

                if (!JipperPoolTopUps.TryGetValue(pool, out int topUps))
                {
                    topUps = 0;
                }

                if (topUps >= JipperPoolMaxTopUps)
                {
                    continue;
                }

                int addedNormal = 0;
                int addedGhost = 0;
                void Create()
                {
                    if (needNormal)
                    {
                        addedNormal = AddJipperRains(pool, false, JipperPoolTopUpNormal);
                    }

                    if (needGhost)
                    {
                        addedGhost = AddJipperRains(pool, true, JipperPoolTopUpGhost);
                    }
                }

                RectTransform? poolTransform = AccessTools.Field(pool.GetType(), "Transform")?.GetValue(pool) as RectTransform;
                Canvas? root = poolTransform == null ? null : poolTransform.GetComponentInParent<Canvas>()?.rootCanvas;
                bool restored = root != null && offscreenCapture != null
                    && offscreenCapture.TryRunWithCanvasRestored(root, Create);
                if (!restored)
                {
                    // Creating a bar while the canvas is in ScreenSpaceCamera mode leaves it invisible
                    // for the rest of the render, so only create when the canvas was never taken over.
                    if (root == null || root.renderMode != RenderMode.ScreenSpaceOverlay)
                    {
                        JipperPoolTopUps[pool] = topUps + 1;
                        ChartRenderDiagnostics.Log("JRP_POOL_TOPUP skipped at frame " + lastFrameIndex
                            + ": the rain canvas could not be restored to Overlay mode.");
                        continue;
                    }

                    Create();
                }

                JipperPoolTopUps[pool] = topUps + 1;
                ChartRenderDiagnostics.Log("JRP_POOL_TOPUP frame=" + lastFrameIndex
                    + " normal=" + normal + "->" + (normal + addedNormal)
                    + " ghost=" + ghost + "->" + (ghost + addedGhost)
                    + " restored=" + restored + ".");
            }
        }

        private static int ReadJipperPoolCount(object pool, FieldInfo? field)
        {
            try
            {
                return field != null && field.GetValue(pool) is int count ? count : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static int AddJipperRains(object pool, bool isGhost, int count)
        {
            MethodInfo? getRain = AccessTools.Method(pool.GetType(), "GetOrNewRain", new[] { typeof(bool) });
            MethodInfo? addPool = AccessTools.Method(pool.GetType(), "AddPool");
            if (getRain == null || addPool == null || count <= 0)
            {
                return 0;
            }

            List<object> created = new List<object>();
            bool previous = jipperCreatingRains;
            jipperCreatingRains = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    object? rain = getRain.Invoke(pool, new object[] { isGhost });
                    if (rain != null)
                    {
                        created.Add(rain);
                    }
                }

                foreach (object rain in created)
                {
                    addPool.Invoke(pool, new[] { rain, (object)isGhost });
                }
            }
            finally
            {
                jipperCreatingRains = previous;
            }

            return created.Count;
        }

        // Rain lifetime in seconds: the bar grows rainSpeed * 10/3 pixels per second and is removed
        // after it travels rainHeight pixels.
        private static void UpdateJipperPoolCapacityLocked()
        {
            int normal = JipperPoolMinNormal;
            int ghost = JipperPoolMinGhost;
            try
            {
                Type? keyViewer = FindJipperKeyViewerType();
                object? settings = keyViewer == null ? null : AccessTools.Field(keyViewer, "Settings")?.GetValue(null);
                if (settings != null)
                {
                    Type type = settings.GetType();
                    float speed = AccessTools.Field(type, "rainSpeed")?.GetValue(settings) is float s ? s : 100f;
                    float height = AccessTools.Field(type, "rainHeight")?.GetValue(settings) is float h ? h : 200f;
                    double lifetime = speed > 0.01f ? 0.3 * height / speed : 1.0;
                    normal = (int)Math.Min(JipperPoolMaxNormal,
                        Math.Max(JipperPoolMinNormal, Math.Ceiling(lifetime * 60.0)));
                    bool useGhost = !(AccessTools.Field(type, "useGhostRain")?.GetValue(settings) is bool on) || on;
                    ghost = useGhost ? Math.Max(JipperPoolMinGhost, normal / 2) : 0;
                }
            }
            catch
            {
            }

            jipperPoolNormal = normal;
            jipperPoolGhost = ghost;
        }

        private static void PrepareJipperPools(List<object> pools, bool purgePooled, string tag)
        {
            UpdateJipperPoolCapacityLocked();
            try
            {
                int pooledDestroyed = 0;
                int restoredCanvases = 0;

                // Group by root canvas so each canvas is switched back to Overlay only once.
                Dictionary<Canvas, List<object>> byCanvas = new Dictionary<Canvas, List<object>>();
                List<object> withoutCanvas = new List<object>();
                foreach (object pool in pools)
                {
                    PreparedJipperPools.Add(pool);
                    if (purgePooled)
                    {
                        pooledDestroyed += PurgePooledJipperRain(pool);
                    }

                    RectTransform? poolTransform = AccessTools.Field(pool.GetType(), "Transform")?.GetValue(pool) as RectTransform;
                    Canvas? canvas = poolTransform == null ? null : poolTransform.GetComponentInParent<Canvas>();
                    Canvas? root = canvas == null ? null : canvas.rootCanvas;
                    if (offscreenCapture == null || root == null)
                    {
                        withoutCanvas.Add(pool);
                        continue;
                    }

                    if (!byCanvas.TryGetValue(root, out List<object> group))
                    {
                        group = new List<object>();
                        byCanvas.Add(root, group);
                    }

                    group.Add(pool);
                }

                int[] counts = { 0, 0 };
                foreach (KeyValuePair<Canvas, List<object>> pair in byCanvas)
                {
                    List<object> group = pair.Value;
                    if (offscreenCapture != null
                        && offscreenCapture.TryRunWithCanvasRestored(pair.Key, () => CreateJipperRainBatches(group, counts)))
                    {
                        restoredCanvases++;
                    }
                    else
                    {
                        // Not a tracked canvas (or the restore failed): create as-is.
                        withoutCanvas.AddRange(group);
                    }
                }

                CreateJipperRainBatches(withoutCanvas, counts);

                ChartRenderDiagnostics.Log(tag + " pools=" + pools.Count
                    + " normal=" + counts[0] + " ghost=" + counts[1]
                    + (purgePooled ? " purged=" + pooledDestroyed : string.Empty)
                    + (restoredCanvases > 0 ? " restoredCanvases=" + restoredCanvases : string.Empty)
                    + ".");
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log(tag + " failed: " + exception.Message);
            }
        }

        private static void CreateJipperRainBatches(List<object> pools, int[] counts)
        {
            bool previous = jipperCreatingRains;
            jipperCreatingRains = true;
            try
            {
                foreach (object pool in pools)
                {
                    MethodInfo? getRain = AccessTools.Method(pool.GetType(), "GetOrNewRain", new[] { typeof(bool) });
                    MethodInfo? addPool = AccessTools.Method(pool.GetType(), "AddPool");
                    if (getRain != null && addPool != null)
                    {
                        CreateJipperRainBatch(pool, getRain, addPool, counts);
                    }
                }
            }
            finally
            {
                jipperCreatingRains = previous;
            }
        }

        private static void CreateJipperRainBatch(object pool, MethodInfo getRain, MethodInfo addPool, int[] counts)
        {
            List<object> created = new List<object>();
            for (int i = 0; i < jipperPoolNormal; i++)
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
            for (int i = 0; i < jipperPoolGhost; i++)
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
                renderFps = Math.Max(1, fps);
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
                Type? keyViewer = FindJipperKeyViewerType();
                BindJipperKeyViewerAdapter(keyViewer);

                if (!adapterReady || boundJipperKeyViewerType != keyViewer)
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

                // JALib can supply the live KeyViewer after rendering has begun, even if the
                // original assembly was already available when the adapter was installed.
                if (discoveryEnabled && lastFrameIndex % JipperPoolCheckFrames == 0)
                {
                    Type? keyViewer = FindJipperKeyViewerType();
                    if (keyViewer != null && keyViewer != boundJipperKeyViewerType)
                    {
                        BindJipperKeyViewerAdapter(keyViewer);
                        active = adapterReady && boundJipperKeyViewerType == keyViewer;
                        if (active)
                        {
                            ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility adapter (re)bound at frame "
                                + lastFrameIndex + ".");
                        }
                    }
                }

                if (discoveryEnabled && offscreenCapture != null)
                {
                    CheckJipperPoolsLocked();
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
                        + " rainPoolAllocations=" + jipperRainAllocations
                        + " handoff=continuous.");
                }

                active = false;
                discoveryEnabled = false;
                baselineSet = false;
                virtualTicks = 0.0;
                JipperPresses.Clear();
                // The capture is disposed after the render; do not keep it (or its pools) around.
                offscreenCapture = null;
                RepurgeJipperPools.Clear();
                JipperPoolTopUps.Clear();
                currentJipperPools = null;
                jipperRepurgesLeft = 0;
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
                offscreenCapture = null;
                PreparedJipperPools.Clear();
                RepurgeJipperPools.Clear();
                JipperPoolTopUps.Clear();
                currentJipperPools = null;
                jipperRainAllocations = 0;
                jipperRepurgesLeft = 0;
                textureFixes = 0;
                textureFixLogged = false;
                jipperKeyViewerType = null;
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

        private static void BindJipperKeyViewerAdapter(Type? keyViewer)
        {
            if (keyViewer == null || keyViewer == boundJipperKeyViewerType)
            {
                return;
            }

            if (harmony != null)
            {
                active = false;
                try
                {
                    harmony.UnpatchAll(HarmonyId);
                }
                catch (Exception exception)
                {
                    ChartRenderDiagnostics.Log("Could not rebind Jipper KeyViewer compatibility: " + exception.Message);
                    return;
                }

                ResetJipperAdapter();
                clockInitialized = false;
                baselineSet = false;
                JipperPresses.Clear();
            }

            ApplyJipperKeyViewerAdapter(keyViewer);
        }

        private static void ApplyJipperKeyViewerAdapter(Type keyViewer)
        {
            try
            {
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

                Assembly assembly = keyViewer.Assembly;
                jipperRainManagerType = assembly.GetType("JipperResourcePack.KeyViewerContents.RainManager", false);
                List<MethodInfo> clockReaders = new List<MethodInfo>();
                AddTarget(clockReaders, AccessTools.Inner(keyViewer, "KeyViewerUpdater"), "Update");
                AddTarget(clockReaders, jipperRainManagerType, "Update");
                if (clockReaders.Count != 2)
                {
                    ChartRenderDiagnostics.Log("Jipper KeyViewer compatibility skipped: the two update call sites were not found.");
                    ResetJipperAdapter();
                    return;
                }

                Type? rainType = assembly.GetType("JipperResourcePack.KeyViewerContents.Rain", false);
                Type? rainPoolType = assembly.GetType("JipperResourcePack.KeyViewerContents.RainPool", false);
                jipperRainListField = jipperRainManagerType == null ? null : AccessTools.Field(jipperRainManagerType, "RainList");
                jipperRainImageField = rainType == null ? null : AccessTools.Field(rainType, "Image");
                jipperRainPoolCountField = rainPoolType == null ? null : AccessTools.Field(rainPoolType, "_poolCount");
                jipperRainPoolGhostField = rainPoolType == null ? null : AccessTools.Field(rainPoolType, "_ghostPoolCount");
                MethodInfo? rainPostfix = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(JipperRainPostfix));
                MethodInfo? allocationPrefix = AccessTools.Method(typeof(ChartRenderCompatibility), nameof(JipperRainAllocationPrefix));
                MethodInfo? getOrNewRain = rainPoolType == null
                    ? null
                    : AccessTools.Method(rainPoolType, "GetOrNewRain", new[] { typeof(bool) });

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
                    if (getOrNewRain != null && allocationPrefix != null)
                    {
                        try
                        {
                            candidate.Patch(getOrNewRain, prefix: new HarmonyMethod(allocationPrefix));
                        }
                        catch (Exception allocationException)
                        {
                            ChartRenderDiagnostics.Log("JRP rain pool counter unavailable: " + allocationException.Message);
                        }
                    }

                    harmony = candidate;
                    boundJipperKeyViewerType = keyViewer;
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
            boundJipperKeyViewerType = null;
            jipperCurrentTicks = null;
            jipperReplacement = null;
            jipperRainManagerType = null;
            jipperRainListField = null;
            jipperRainImageField = null;
            jipperRainPoolCountField = null;
            jipperRainPoolGhostField = null;
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

        // Counts bars JRP had to allocate because the pool was empty. Such a bar was created while the
        // canvas was already in ScreenSpaceCamera mode and stays invisible for the rest of the render,
        // then returns to the pool and is reused, so this number must stay at zero.
        private static void JipperRainAllocationPrefix(object __instance, bool __0)
        {
            if (!active || jipperCreatingRains)
            {
                return;
            }

            FieldInfo? field = __0 ? jipperRainPoolGhostField : jipperRainPoolCountField;
            if (field == null)
            {
                return;
            }

            try
            {
                if (field.GetValue(__instance) is int count && count <= 0)
                {
                    jipperRainAllocations++;
                }
            }
            catch
            {
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
