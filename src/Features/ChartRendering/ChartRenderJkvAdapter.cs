using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Render clock adapter for JipperKeyViewer (JKV, github.com/adofaiex/JipperKeyViewer).
    //
    // JKV animates rain, key scale, counter bounce and KPS with Time.unscaledTime,
    // Time.unscaledDeltaTime and a Stopwatch, all of which follow the wall clock and therefore run
    // at the wrong speed in a rendered video. Its key input already arrives through its own replay
    // shim (Creplay drives it), so only the clocks need adapting.
    //
    // The call sites move between JKV releases (1.7.2 and master differ), so instead of naming
    // methods this scans JKV's display code (namespace JipperKeyViewer.KeyViewer, excluding the
    // editor and settings GUI) and rewrites those clock reads in every method that has them. If any
    // method fails to patch, all of them are rolled back so JKV never runs on a mix of clocks.
    internal static class ChartRenderJkvAdapter
    {
        private const string HarmonyId = "ADOFAI.EditorTweaks.ChartRendering.RenderTimeCompat.Jkv";
        private const string JkvAssemblyName = "JipperKeyViewer";
        private const string JkvNamespace = "JipperKeyViewer.KeyViewer";
        private static readonly string[] ExcludedNamespaces =
        {
            "JipperKeyViewer.KeyViewer.Editor",
            "JipperKeyViewer.KeyViewer.GUI",
        };

        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly MethodInfo UnscaledTimeGetter =
            AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
        private static readonly MethodInfo UnscaledTimeAsDoubleGetter =
            AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTimeAsDouble));
        private static readonly MethodInfo UnscaledDeltaTimeGetter =
            AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));
        private static readonly MethodInfo StopwatchElapsedMsGetter =
            AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.ElapsedMilliseconds));
        private static readonly MethodInfo StopwatchElapsedTicksGetter =
            AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.ElapsedTicks));
        private static readonly MethodInfo StopwatchElapsedGetter =
            AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.Elapsed));

        private static readonly object Sync = new object();
        private static readonly ChartRenderContinuousClock UnscaledClock = new ChartRenderContinuousClock();
        private static readonly List<StopwatchClock> StopwatchClocks = new List<StopwatchClock>();

        private sealed class StopwatchClock
        {
            public StopwatchClock(Stopwatch stopwatch)
            {
                Stopwatch = new WeakReference(stopwatch);
            }

            public readonly WeakReference Stopwatch;
            public readonly ChartRenderContinuousClock Clock = new ChartRenderContinuousClock();
        }

        private static Harmony? harmony;
        private static bool ready;
        private static bool discovery;
        private static volatile bool active;
        private static double frameSeconds = 1.0 / 60.0;
        private static double videoSeconds;
        private static int patchedMethods;
        private static int timeReads;
        private static int stopwatchReads;

        public static void Begin(int fps)
        {
            lock (Sync)
            {
                active = false;
                discovery = true;
                frameSeconds = 1.0 / Math.Max(1, fps);
                videoSeconds = 0.0;
                timeReads = 0;
                stopwatchReads = 0;
                if (!ready)
                {
                    TryApply(logMissing: true);
                }

                if (ready)
                {
                    active = true;
                    ChartRenderDiagnostics.Log("JKV render clock enabled. fps=" + Math.Max(1, fps)
                        + " patchedMethods=" + patchedMethods + ".");
                }
            }
        }

        public static void Tick(int frameIndex)
        {
            lock (Sync)
            {
                if (!discovery)
                {
                    return;
                }

                videoSeconds = Math.Max(0, frameIndex) * frameSeconds;

                // JKV may finish loading after the render started.
                if (!ready && frameIndex > 0 && frameIndex % 60 == 0)
                {
                    TryApply(logMissing: false);
                    if (ready)
                    {
                        active = true;
                        ChartRenderDiagnostics.Log("JKV render clock late-loaded at frame " + frameIndex + ".");
                    }
                }
            }
        }

        public static void End()
        {
            lock (Sync)
            {
                try
                {
                    if (active)
                    {
                        EndClocksLocked();
                    }
                }
                finally
                {
                    // Even if a read fails, never leave JKV frozen on the video timeline.
                    active = false;
                    discovery = false;
                }
            }
        }

        private static void EndClocksLocked()
        {
            UnscaledClock.EndRender(Time.unscaledTimeAsDouble, videoSeconds);
            double videoTicks = videoSeconds * TimeSpan.TicksPerSecond;
            for (int i = StopwatchClocks.Count - 1; i >= 0; i--)
            {
                if (StopwatchClocks[i].Stopwatch.Target is Stopwatch stopwatch)
                {
                    StopwatchClocks[i].Clock.EndRender(stopwatch.Elapsed.Ticks, videoTicks);
                }
                else
                {
                    StopwatchClocks.RemoveAt(i);
                }
            }

            ChartRenderDiagnostics.Log("JKV render clock ended. videoSeconds=" + videoSeconds.ToString("0.###")
                + " timeReads=" + timeReads + " stopwatchReads=" + stopwatchReads
                + " stopwatches=" + StopwatchClocks.Count + " handoff=continuous.");
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                active = false;
                discovery = false;
                UnscaledClock.Reset();
                StopwatchClocks.Clear();
                Harmony? old = harmony;
                harmony = null;
                ready = false;
                patchedMethods = 0;
                if (old != null)
                {
                    try
                    {
                        old.UnpatchAll(HarmonyId);
                    }
                    catch (Exception exception)
                    {
                        Main.Log("Could not remove the JKV compatibility adapter: " + exception.Message);
                    }
                }
            }
        }

        private static void TryApply(bool logMissing)
        {
            Assembly? jkv = FindJkvAssembly();
            if (jkv == null)
            {
                if (logMissing)
                {
                    ChartRenderDiagnostics.Log("JKV compatibility skipped: JipperKeyViewer is not loaded.");
                }

                return;
            }

            List<MethodBase> targets;
            try
            {
                targets = FindClockReaders(jkv);
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log("JKV compatibility skipped: scanning failed: " + exception.Message);
                return;
            }

            if (targets.Count == 0)
            {
                ChartRenderDiagnostics.Log("JKV compatibility skipped: no clock reads found in " + jkv.GetName().Version + ".");
                return;
            }

            MethodInfo transpiler = AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(Transpiler));
            Harmony candidate = new Harmony(HarmonyId);
            try
            {
                foreach (MethodBase method in targets)
                {
                    candidate.Patch(method, transpiler: new HarmonyMethod(transpiler));
                }

                harmony = candidate;
                ready = true;
                patchedMethods = targets.Count;
                ChartRenderDiagnostics.Log("JKV compatibility adapter ready. version=" + jkv.GetName().Version
                    + " methods=" + targets.Count + ".");
            }
            catch (Exception exception)
            {
                try
                {
                    candidate.UnpatchAll(HarmonyId);
                }
                catch
                {
                }

                ChartRenderDiagnostics.Log("JKV compatibility rolled back; rendering continues without it: " + exception);
            }
        }

        private static Assembly? FindJkvAssembly()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, JkvAssemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static List<MethodBase> FindClockReaders(Assembly jkv)
        {
            Type?[] types;
            try
            {
                types = jkv.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            List<MethodBase> targets = new List<MethodBase>();
            foreach (Type? type in types)
            {
                if (type == null || !IsDisplayType(type))
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(AllDeclared))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                    {
                        continue;
                    }

                    if (ReadsClock(method))
                    {
                        targets.Add(method);
                    }
                }
            }

            return targets;
        }

        private static bool IsDisplayType(Type type)
        {
            // Compiler-generated iterator/lambda classes are nested; judge them by their outer type.
            Type outer = type;
            while (outer.DeclaringType != null)
            {
                outer = outer.DeclaringType;
            }

            string ns = outer.Namespace ?? string.Empty;
            if (!ns.StartsWith(JkvNamespace, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (string excluded in ExcludedNamespaces)
            {
                if (ns.StartsWith(excluded, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ReadsClock(MethodInfo method)
        {
            foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
            {
                if (instruction.Value is MethodInfo called && Replacement(called) != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static MethodInfo? Replacement(MethodInfo called)
        {
            if (called == UnscaledTimeGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(UnscaledTime));
            }

            if (called == UnscaledTimeAsDoubleGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(UnscaledTimeAsDouble));
            }

            if (called == UnscaledDeltaTimeGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(UnscaledDeltaTime));
            }

            if (called == StopwatchElapsedMsGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(StopwatchElapsedMilliseconds));
            }

            if (called == StopwatchElapsedTicksGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(StopwatchElapsedTicks));
            }

            if (called == StopwatchElapsedGetter)
            {
                return AccessTools.Method(typeof(ChartRenderJkvAdapter), nameof(StopwatchElapsed));
            }

            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo called
                    && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt))
                {
                    MethodInfo? replacement = Replacement(called);
                    if (replacement != null)
                    {
                        // Stopwatch getters are instance calls; the replacements are static methods
                        // taking the Stopwatch, so the stack shape is unchanged.
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                }

                yield return instruction;
            }
        }

        public static float UnscaledTime()
        {
            return (float)UnscaledTimeAsDouble();
        }

        public static double UnscaledTimeAsDouble()
        {
            double real = Time.unscaledTimeAsDouble;
            lock (Sync)
            {
                timeReads++;
                return UnscaledClock.Map(real, active, videoSeconds);
            }
        }

        public static float UnscaledDeltaTime()
        {
            // Renders step exactly one video frame per Unity frame (Time.captureFramerate = fps).
            return active ? (float)frameSeconds : Time.unscaledDeltaTime;
        }

        public static long StopwatchElapsedMilliseconds(Stopwatch stopwatch)
        {
            return MapStopwatchTicks(stopwatch) / TimeSpan.TicksPerMillisecond;
        }

        public static long StopwatchElapsedTicks(Stopwatch stopwatch)
        {
            // Stopwatch.ElapsedTicks is in Stopwatch.Frequency units, not TimeSpan ticks.
            return (long)(MapStopwatchTicks(stopwatch) * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));
        }

        public static TimeSpan StopwatchElapsed(Stopwatch stopwatch)
        {
            return new TimeSpan(MapStopwatchTicks(stopwatch));
        }

        private static long MapStopwatchTicks(Stopwatch stopwatch)
        {
            // Keep a NullReferenceException where JKV would have thrown one itself.
            long real = stopwatch.Elapsed.Ticks;
            lock (Sync)
            {
                if (!active && StopwatchClocks.Count == 0)
                {
                    return real;
                }

                stopwatchReads++;
                ChartRenderContinuousClock clock = GetStopwatchClock(stopwatch);
                double mapped = clock.Map(real, active, videoSeconds * TimeSpan.TicksPerSecond);
                return (long)Math.Max(0.0, mapped);
            }
        }

        private static ChartRenderContinuousClock GetStopwatchClock(Stopwatch stopwatch)
        {
            for (int i = 0; i < StopwatchClocks.Count; i++)
            {
                if (ReferenceEquals(StopwatchClocks[i].Stopwatch.Target, stopwatch))
                {
                    return StopwatchClocks[i].Clock;
                }
            }

            // Drop stopwatches JKV has already replaced (it recreates one on layout rebuild).
            StopwatchClocks.RemoveAll(entry => !entry.Stopwatch.IsAlive);
            StopwatchClock created = new StopwatchClock(stopwatch);
            StopwatchClocks.Add(created);
            return created.Clock;
        }
    }
}
