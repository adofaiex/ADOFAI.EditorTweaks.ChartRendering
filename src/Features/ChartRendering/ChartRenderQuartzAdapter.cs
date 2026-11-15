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
    // No Harmony patch is involved. If another replay driver is using KvExternalDriver, we leave
    // Quartz alone.
    internal static class ChartRenderQuartzAdapter
    {
        private const string ClockTypeName = "Quartz.Features.KeyViewer.KvClock";
        private const string DriverTypeName = "Quartz.Features.KeyViewer.KvExternalDriver";

        private static readonly object Sync = new object();
        private static readonly Func<double> RenderClock = ReadVideoSeconds;

        private static MethodInfo? setExternal;
        private static MethodInfo? clearExternal;
        private static System.Reflection.PropertyInfo? driverActive;
        private static bool resolved;
        private static bool discovery;
        private static bool owned;
        private static double frameSeconds = 1.0 / 60.0;
        private static double videoSeconds;

        public static void Begin(int fps)
        {
            lock (Sync)
            {
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
                if (!owned && frameIndex > 0 && frameIndex % 300 == 0)
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
                if (!owned)
                {
                    return;
                }

                owned = false;
                if (IsOtherDriverActive())
                {
                    // Someone else took the clock over mid-render; clearing it would break theirs.
                    ChartRenderDiagnostics.Log("Quartz render clock ended without restoring: another driver is active.");
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
        }

        public static void Shutdown()
        {
            End();
            lock (Sync)
            {
                resolved = false;
                setExternal = null;
                clearExternal = null;
                driverActive = null;
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
                ChartRenderDiagnostics.Log("Quartz render clock enabled (clock only, input unchanged).");
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log("Quartz compatibility failed; rendering continues without it: " + exception.Message);
            }
        }

        private static bool Resolve(bool logMissing)
        {
            if (resolved)
            {
                return true;
            }

            Type? clock = AccessTools.TypeByName(ClockTypeName);
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

            Type? driver = AccessTools.TypeByName(DriverTypeName);
            setExternal = set;
            clearExternal = clear;
            driverActive = driver == null ? null : AccessTools.Property(driver, "Active");
            resolved = true;
            ChartRenderDiagnostics.Log("Quartz compatibility found KvClock in " + clock.Assembly.GetName().Name
                + " " + clock.Assembly.GetName().Version + ".");
            return true;
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
