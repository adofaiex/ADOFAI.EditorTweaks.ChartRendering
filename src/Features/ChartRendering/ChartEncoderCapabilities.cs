using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using GDMiniJSON;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Which video encoders work on this computer, so the settings page can show what a render will
    // really use (for example that AV1 falls back to the CPU on an RTX 30 card).
    //
    // - Results are saved in Settings.EncoderCapabilitiesCache with a fingerprint of the GPU and the
    //   FFmpeg executable. When the mod starts and the fingerprint differs (new GPU, custom FFmpeg) or
    //   nothing is saved yet, all encoders are probed again on a background thread. The settings page
    //   can also start a detection by hand (for example after a driver update).
    // - Renders still probe each encoder live once per game session (IsAvailable), because a driver
    //   change does not alter the fingerprint. A live result that differs from the saved one replaces it.
    // - A probe encodes one small frame with the encoder; it fails both when FFmpeg lacks the encoder
    //   and when the hardware or driver cannot run it.
    // - Probing runs off the main thread; saving to Settings.xml happens on the main thread
    //   (FlushIfDirty, called every frame by the Web UI host).
    internal static class ChartEncoderCapabilities
    {
        // Every encoder the renderer can pick (FfmpegEncoder), in the order the settings page lists them.
        public static readonly string[] KnownEncoders =
        {
            "h264_nvenc", "hevc_nvenc", "av1_nvenc",
            "h264_amf", "hevc_amf", "av1_amf",
            "h264_qsv", "hevc_qsv", "av1_qsv",
            "libx264", "libx265", "libsvtav1", "libaom-av1", "libvvenc", "prores_aw"
        };

        private const int DefaultProbeTimeoutMilliseconds = 10000;
        // libaom/libsvtav1/libvvenc can take much longer to initialise on the CPU, especially the
        // first time a library is loaded.
        private const int SlowCpuProbeTimeoutMilliseconds = 30000;
        // Bump when KnownEncoders changes so caches saved by an older build are probed again.
        private const int CapabilitySchemaVersion = 2;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, bool> savedResults = new Dictionary<string, bool>(StringComparer.Ordinal);
        // Live probes made in this game session, keyed by "<ffmpeg path>|<encoder>".
        private static readonly Dictionary<string, bool> sessionProbes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static string savedFingerprint = string.Empty;
        private static string currentFingerprint = string.Empty;
        private static string currentFfmpegPath = string.Empty;
        private static string detectedAt = string.Empty;
        private static string gpuName = string.Empty;
        private static bool loaded;
        private static bool detecting;
        private static bool dirty;
        private static int changeCount;

        // Increases whenever the results or the detection state change, so the Web UI knows to publish.
        public static int ChangeCount
        {
            get
            {
                lock (Sync)
                {
                    return changeCount;
                }
            }
        }

        // Main thread. Loads the saved results and starts a detection when they do not match this computer.
        public static void EnsureCurrent()
        {
            string ffmpegPath = ChartRenderPaths.GetFfmpegPath();
            string fingerprint = BuildFingerprint(ffmpegPath);
            bool start;
            lock (Sync)
            {
                if (!loaded)
                {
                    Load(Main.Settings.EncoderCapabilitiesCache);
                    loaded = true;
                }

                currentFfmpegPath = ffmpegPath;
                currentFingerprint = fingerprint;
                gpuName = SystemInfo.graphicsDeviceName ?? string.Empty;
                start = !detecting && (savedResults.Count == 0 || !string.Equals(savedFingerprint, fingerprint, StringComparison.Ordinal));
            }

            if (start)
            {
                StartDetection();
            }
        }

        // Main thread. Probes every known encoder again on a background thread. Returns false when a
        // detection is already running.
        public static bool StartDetection()
        {
            string ffmpegPath = ChartRenderPaths.GetFfmpegPath();
            string fingerprint = BuildFingerprint(ffmpegPath);
            lock (Sync)
            {
                if (!loaded)
                {
                    Load(Main.Settings.EncoderCapabilitiesCache);
                    loaded = true;
                }

                if (detecting)
                {
                    return false;
                }

                detecting = true;
                currentFfmpegPath = ffmpegPath;
                currentFingerprint = fingerprint;
                gpuName = SystemInfo.graphicsDeviceName ?? string.Empty;
                changeCount++;
            }

            Thread thread = new Thread(() => RunDetection(ffmpegPath, fingerprint))
            {
                IsBackground = true,
                Name = "ADOFAI.EditorTweaks.ChartRendering.EncoderDetection"
            };
            thread.Start();
            return true;
        }

        // Any thread. Whether the encoder works with this FFmpeg, probed live once per game session.
        public static bool IsAvailable(string ffmpegPath, string encoder)
        {
            string key = ffmpegPath + "|" + encoder;
            lock (Sync)
            {
                if (sessionProbes.TryGetValue(key, out bool known))
                {
                    return known;
                }
            }

            bool? available = Probe(ffmpegPath, encoder);
            if (!available.HasValue)
            {
                // A timeout is not "this encoder is broken". Do not cache it or save it, so a later
                // render (or the next launch) can probe again.
                Main.Log("Encoder probe timed out for " + encoder + "; not saving the result.");
                return false;
            }

            Record(ffmpegPath, encoder, available.Value);
            return available.Value;
        }

        // Main thread. Writes changed results into the settings file.
        public static bool FlushIfDirty()
        {
            string json;
            lock (Sync)
            {
                if (!dirty)
                {
                    return false;
                }

                dirty = false;
                json = Serialize();
            }

            try
            {
                Main.Settings.EncoderCapabilitiesCache = json;
                Main.Settings.Save(Main.Mod!);
            }
            catch (Exception ex)
            {
                Main.Log("Failed to save encoder detection results: " + ex.Message);
            }

            return true;
        }

        // Snapshot for the Web UI: status ("unknown", "detecting", "ready"), when, GPU, and per-encoder results.
        public static Dictionary<string, object> BuildState()
        {
            lock (Sync)
            {
                Dictionary<string, object> results = new Dictionary<string, object>();
                foreach (string encoder in KnownEncoders)
                {
                    if (savedResults.TryGetValue(encoder, out bool available))
                    {
                        results[encoder] = available;
                    }
                }

                bool matches = savedResults.Count > 0 && string.Equals(savedFingerprint, currentFingerprint, StringComparison.Ordinal);
                return new Dictionary<string, object>
                {
                    ["status"] = detecting ? "detecting" : matches ? "ready" : "unknown",
                    ["detectedAt"] = detectedAt,
                    ["gpu"] = gpuName,
                    ["results"] = results
                };
            }
        }

        private static void RunDetection(string ffmpegPath, string fingerprint)
        {
            Main.Log("Encoder detection started with " + ffmpegPath + ".");
            Dictionary<string, bool> results = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string encoder in KnownEncoders)
            {
                bool? available = Probe(ffmpegPath, encoder);
                if (!available.HasValue)
                {
                    // Keep the previous saved result, if any, instead of turning a cold-start
                    // timeout into a permanent "unavailable" entry.
                    lock (Sync)
                    {
                        if (savedResults.TryGetValue(encoder, out bool previous))
                        {
                            results[encoder] = previous;
                        }
                    }

                    Main.Log("Encoder probe timed out for " + encoder + "; keeping the previous result.");
                    continue;
                }

                results[encoder] = available.Value;
                lock (Sync)
                {
                    sessionProbes[ffmpegPath + "|" + encoder] = available.Value;
                }
            }

            lock (Sync)
            {
                savedResults.Clear();
                foreach (KeyValuePair<string, bool> pair in results)
                {
                    savedResults[pair.Key] = pair.Value;
                }

                savedFingerprint = fingerprint;
                detectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                detecting = false;
                dirty = true;
                changeCount++;
            }

            List<string> working = new List<string>();
            foreach (KeyValuePair<string, bool> pair in results)
            {
                if (pair.Value)
                {
                    working.Add(pair.Key);
                }
            }

            Main.Log("Encoder detection finished. Available: " + (working.Count == 0 ? "none" : string.Join(", ", working.ToArray())) + ".");
        }

        private static void Record(string ffmpegPath, string encoder, bool available)
        {
            lock (Sync)
            {
                sessionProbes[ffmpegPath + "|" + encoder] = available;
                // Only results for the FFmpeg the saved detection describes are worth keeping.
                if (savedResults.Count > 0
                    && string.Equals(ffmpegPath, currentFfmpegPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(savedFingerprint, currentFingerprint, StringComparison.Ordinal)
                    && (!savedResults.TryGetValue(encoder, out bool saved) || saved != available))
                {
                    savedResults[encoder] = available;
                    dirty = true;
                    changeCount++;
                }
            }
        }

        // true = available, false = unavailable, null = timed out (do not save it).
        private static bool? Probe(string ffmpegPath, string encoder)
        {
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                return false;
            }

            try
            {
                // AMF enforces minimum widths: HEVC needs at least 384 and AV1 at least 320.
                // Probing at 256x256 made hevc_amf/av1_amf fail at Init() even though real
                // renders work, so use a normal 640x360 frame for every encoder.
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -loglevel quiet -y -f lavfi -i color=size=640x360:rate=1:duration=0.1 -c:v " + encoder
                        + " -pix_fmt " + GetProbePixelFormat(encoder) + " -frames:v 1 -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process? probe = Process.Start(info))
                {
                    if (probe == null)
                    {
                        return false;
                    }

                    if (!probe.WaitForExit(GetProbeTimeoutMilliseconds(encoder)))
                    {
                        try
                        {
                            probe.Kill();
                        }
                        catch
                        {
                        }

                        return null;
                    }

                    return probe.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int GetProbeTimeoutMilliseconds(string encoder)
        {
            return encoder == "libaom-av1" || encoder == "libsvtav1" || encoder == "libvvenc"
                ? SlowCpuProbeTimeoutMilliseconds
                : DefaultProbeTimeoutMilliseconds;
        }

        private static string GetProbePixelFormat(string encoder)
        {
            if (encoder.EndsWith("_qsv", StringComparison.Ordinal) || encoder.EndsWith("_amf", StringComparison.Ordinal))
            {
                return "nv12";
            }

            if (encoder == "libvvenc")
            {
                return "yuv420p10le";
            }

            if (encoder == "prores_aw")
            {
                return "yuv422p10le";
            }

            return "yuv420p";
        }

        // Main thread (SystemInfo). Changes when the GPU or the FFmpeg executable changes.
        private static string BuildFingerprint(string ffmpegPath)
        {
            string ffmpegStamp = "missing";
            try
            {
                FileInfo file = new FileInfo(ffmpegPath);
                if (file.Exists)
                {
                    ffmpegStamp = file.Length.ToString(CultureInfo.InvariantCulture) + "@"
                        + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
            }

            return "v" + CapabilitySchemaVersion + "|"
                + SystemInfo.graphicsDeviceVendor + "|" + SystemInfo.graphicsDeviceName + "|"
                + ffmpegPath.ToLowerInvariant() + "|" + ffmpegStamp;
        }

        private static void Load(string? json)
        {
            savedResults.Clear();
            savedFingerprint = string.Empty;
            detectedAt = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                if (!(Json.Deserialize(json) is Dictionary<string, object> root))
                {
                    return;
                }

                savedFingerprint = root.TryGetValue("fingerprint", out object fp) ? fp as string ?? string.Empty : string.Empty;
                detectedAt = root.TryGetValue("detectedAt", out object at) ? at as string ?? string.Empty : string.Empty;
                if (root.TryGetValue("results", out object raw) && raw is Dictionary<string, object> results)
                {
                    foreach (KeyValuePair<string, object> pair in results)
                    {
                        if (pair.Value is bool available)
                        {
                            savedResults[pair.Key] = available;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Log("Ignoring unreadable encoder detection results: " + ex.Message);
                savedResults.Clear();
                savedFingerprint = string.Empty;
            }
        }

        private static string Serialize()
        {
            Dictionary<string, object> results = new Dictionary<string, object>();
            foreach (KeyValuePair<string, bool> pair in savedResults)
            {
                results[pair.Key] = pair.Value;
            }

            return Json.Serialize(new Dictionary<string, object>
            {
                ["fingerprint"] = savedFingerprint,
                ["detectedAt"] = detectedAt,
                ["results"] = results
            });
        }
    }
}
