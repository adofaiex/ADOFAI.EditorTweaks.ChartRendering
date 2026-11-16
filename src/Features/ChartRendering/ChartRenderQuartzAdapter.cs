using System;
using System.Reflection;
using HarmonyLib;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Render clock adapter for Quartz (github.com/PrismMods/Quartz), UMM build, v2.1.1 and v3.
    //
    // Quartz's key viewer reads every time value (rain, transitions, counters, graph, JS panel and
    // input timestamps) from one internal clock, KvClock.Now. Quartz already supports swapping
    // that clock: KvClock.SetExternal(Func<double>) and ClearExternal(), which rebase so Now never
    // jumps, are what its public KvExternalDriver uses. We call those two directly instead of
    // KvExternalDriver.Begin, because Begin also replaces real input with driver-injected keys,
    // and AETC has no key codes to inject. Key input keeps reaching Quartz the usual way.
    //
    // Quartz drops key presses while the game window is not focused
    // (KvInputQueue.Push: "if(down && !focused) return;"). Starting a render from the Web UI, or
    // switching windows during a render, unfocuses the game, so while we hold the clock a small
    // prefix on the public KvInputQueue.SetFocused reports the window as focused.
    //
    // If another replay driver is using KvExternalDriver, we leave Quartz alone.
    internal static class ChartRenderQuartzAdapter
    {
        private const string HarmonyId = "ADOFAI.EditorTweaks.ChartRendering.RenderTimeCompat.Quartz";
        private const string KeyViewerNamespace = "Quartz.Features.KeyViewer.";
        private const string ClockTypeName = KeyViewerNamespace + "KvClock";
        private const string DriverTypeName = KeyViewerNamespace + "KvExternalDriver";
        private const string InputQueueTypeName = KeyViewerNamespace + "KvInputQueue";

        private static readonly object Sync = new object();
        private static readonly Func<double> RenderClock = ReadVideoSeconds;

        private static MethodInfo? setExternal;
        private static MethodInfo? clearExternal;
        private static MethodInfo? setFocused;
        private static System.Reflection.PropertyInfo? driverActive;
        private static Harmony? harmony;
        private static bool resolved;
        private static bool discovery;
        private static bool owned;
        private static volatile bool forceFocus;
        private static double frameSeconds = 1.0 / 60.0;
        private static double videoSeconds;

        public static void Begin(int fps)
        {
            lock (Sync)
            {
                // A previous render that never reached End still owns the clock. Resetting the video
                // time under it would move Quartz's clock backwards, so release it first; the next
                // SetExternal rebases from the current time.
                if (owned)
                {
                    ReleaseClock("Quartz clock was still held from an earlier render; re-anchoring.");
                }

                discovery = true;
                frameSeconds = 1.0 / Math.Max(1, fps);
                videoSeconds = 0.0;
                TryTakeClock(logMissing: true);
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

                // Quartz loads its modules asynchronously and may finish after the render started.
                if (!owned && frameIndex > 0 && frameIndex % 60 == 0)
                {
                    TryTakeClock(logMissing: false);
                }
            }
        }

        public static void End()
        {
            lock (Sync)
            {
                discovery = false;
                if (owned)
                {
                    ReleaseClock(null);
                }
            }
        }

        public static void Shutdown()
        {
            End();
            lock (Sync)
            {
                forceFocus = false;
                Harmony? old = harmony;
                harmony = null;
                resolved = false;
                setExternal = null;
                clearExternal = null;
                setFocused = null;
                driverActive = null;
                if (old != null)
                {
                    try
                    {
                        old.UnpatchAll(HarmonyId);
                    }
                    catch (Exception exception)
                    {
                        Main.Log("Could not remove the Quartz compatibility patch: " + exception.Message);
                    }
                }
            }
        }

        private static void ReleaseClock(string? reason)
        {
            owned = false;
            forceFocus = false;
            if (reason != null)
            {
                ChartRenderDiagnostics.Log(reason);
            }

            if (IsOtherDriverActive())
            {
                // Someone else took the clock over mid-render; clearing it would break theirs.
                ChartRenderDiagnostics.Log("Quartz render clock released without restoring: another driver is active.");
                return;
            }

            try
            {
                clearExternal!.Invoke(null, null);
                ChartRenderDiagnostics.Log("Quartz render clock ended. videoSeconds="
                    + videoSeconds.ToString("0.###") + " handoff=continuous.");
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log("Quartz render clock could not be restored: " + exception.Message);
            }
        }

        private static void TryTakeClock(bool logMissing)
        {
            if (owned || !Resolve(logMissing))
            {
                return;
            }

            if (IsOtherDriverActive())
            {
                ChartRenderDiagnostics.Log("Quartz compatibility skipped: another replay driver is using Quartz.");
                return;
            }

            try
            {
                setExternal!.Invoke(null, new object[] { RenderClock });
                owned = true;
                forceFocus = true;
                // Apply now instead of waiting for Quartz's next LateUpdate.
                setFocused?.Invoke(null, new object[] { true });
                ChartRenderDiagnostics.Log("Quartz render clock enabled (clock only, input unchanged"
                    + (harmony != null ? ", focus kept" : ", focus patch unavailable") + ").");
            }
            catch (Exception exception)
            {
                owned = false;
                forceFocus = false;
                ChartRenderDiagnostics.Log("Quartz compatibility failed; rendering continues without it: " + exception.Message);
            }
        }

        private static bool Resolve(bool logMissing)
        {
            if (resolved)
            {
                return true;
            }

            Type? clock = FindQuartzType(ClockTypeName);
            if (clock == null)
            {
                if (logMissing)
                {
                    ChartRenderDiagnostics.Log("Quartz compatibility skipped: Quartz key viewer is not loaded.");
                }

                return false;
            }

            MethodInfo? set = AccessTools.Method(clock, "SetExternal", new[] { typeof(Func<double>) });
            MethodInfo? clear = AccessTools.Method(clock, "ClearExternal", Type.EmptyTypes);
            if (set == null || clear == null || !set.IsStatic || !clear.IsStatic)
            {
                ChartRenderDiagnostics.Log("Quartz compatibility skipped: KvClock.SetExternal/ClearExternal not found in "
                    + clock.Assembly.GetName().Name + " " + clock.Assembly.GetName().Version + ".");
                return false;
            }

            Type? driver = FindQuartzType(DriverTypeName);
            setExternal = set;
            clearExternal = clear;
            driverActive = driver == null ? null : AccessTools.Property(driver, "Active");
            resolved = true;
            ChartRenderDiagnostics.Log("Quartz compatibility found KvClock in " + clock.Assembly.GetName().Name
                + " " + clock.Assembly.GetName().Version + ".");
            PatchFocus();
            return true;
        }

        // Optional: without it the clock still works, Quartz just keeps dropping presses while the
        // window is unfocused.
        private static void PatchFocus()
        {
            Type? queue = FindQuartzType(InputQueueTypeName);
            MethodInfo? method = queue == null ? null : AccessTools.Method(queue, "SetFocused", new[] { typeof(bool) });
            MethodInfo prefix = AccessTools.Method(typeof(ChartRenderQuartzAdapter), nameof(SetFocusedPrefix));
            if (method == null || !method.IsStatic)
            {
                ChartRenderDiagnostics.Log("Quartz focus patch skipped: KvInputQueue.SetFocused(bool) not found.");
                return;
            }

            Harmony candidate = new Harmony(HarmonyId);
            try
            {
                candidate.Patch(method, prefix: new HarmonyMethod(prefix));
                harmony = candidate;
                setFocused = method;
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

                ChartRenderDiagnostics.Log("Quartz focus patch failed: " + exception.Message);
            }
        }

        private static void SetFocusedPrefix(ref bool __0)
        {
            if (forceFocus)
            {
                __0 = true;
            }
        }

        // Quartz 2.1.1 ships the key viewer as module Quartz.Module.KeyViewer; v3 moved it into
        // QuartzUmm. Only Quartz assemblies are searched: AccessTools.TypeByName scans every type of
        // every assembly when the type is missing, which is too slow to repeat during a render.
        private static Type? FindQuartzType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = assembly.GetName().Name;
                if (name == null || !name.StartsWith("Quartz", StringComparison.Ordinal))
                {
                    continue;
                }

                Type? type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool IsOtherDriverActive()
        {
            try
            {
                return driverActive != null && driverActive.GetValue(null, null) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        // Called by Quartz, possibly from its input hook thread.
        private static double ReadVideoSeconds()
        {
            lock (Sync)
            {
                return videoSeconds;
            }
        }
    }
}
