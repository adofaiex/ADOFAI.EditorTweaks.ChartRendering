using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Checks a small static manifest for a newer release. It only reports; updating is left to
    // UMM / Aurora, so nothing is downloaded or replaced here.
    internal static class ChartUpdateChecker
    {
        public const string ManifestUrl =
            "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases/download/update/update.json";

        public const string AuroraUrl = "https://www.adofaitools.top/tools/adofai-mod-manager";

        [Serializable]
        private sealed class Manifest
        {
            public string version = string.Empty;
            public string url = string.Empty;
            public string notes = string.Empty;
        }

        private static readonly object Sync = new object();
        private static string status = "idle";
        private static string latest = string.Empty;
        private static string downloadUrl = "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases";
        private static string notes = string.Empty;
        private static int changeCount;
        private static bool started;
        private static int needsApply;

        public static int ChangeCount => Volatile.Read(ref changeCount);

        public static bool IsAvailable
        {
            get
            {
                lock (Sync)
                {
                    return status == "available";
                }
            }
        }

        public static string Latest
        {
            get
            {
                lock (Sync)
                {
                    return latest;
                }
            }
        }

        public static string DownloadUrl
        {
            get
            {
                lock (Sync)
                {
                    return downloadUrl;
                }
            }
        }

        // Main thread. Once per session; the check runs on a background thread.
        public static void StartIfDue()
        {
            lock (Sync)
            {
                if (started)
                {
                    return;
                }

                started = true;
                status = "checking";
            }

            MarkChanged();
            Thread thread = new Thread(Check)
            {
                IsBackground = true,
                Name = "ADOFAI.EditorTweaks.ChartRendering.UpdateCheck"
            };
            thread.Start();
        }

        // Main thread. Mirrors the result into UMM's own "available update" marker.
        public static void ApplyToMainThread()
        {
            if (Interlocked.Exchange(ref needsApply, 0) == 0)
            {
                return;
            }

            try
            {
                if (Main.Mod != null && Version.TryParse(latest, out Version version))
                {
                    Main.Mod.NewestVersion = version;
                    Main.Mod.HasUpdate = true;
                }
            }
            catch
            {
            }
        }

        public static Dictionary<string, object> BuildState()
        {
            lock (Sync)
            {
                return new Dictionary<string, object>
                {
                    ["status"] = status,
                    ["latest"] = latest,
                    ["current"] = Main.Mod?.Info?.Version ?? "0.0.0",
                    ["url"] = downloadUrl,
                    ["notes"] = notes,
                    ["auroraUrl"] = AuroraUrl
                };
            }
        }

        private static void Check()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ManifestUrl);
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.UserAgent = "ADOFAI.EditorTweaks.ChartRendering/" + (Main.Mod?.Info?.Version ?? "0");

                string json;
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    json = reader.ReadToEnd();
                }

                Manifest manifest = JsonUtility.FromJson<Manifest>(json);
                if (manifest == null || string.IsNullOrEmpty(manifest.version))
                {
                    throw new InvalidDataException("manifest has no version");
                }

                bool hasRemote = Version.TryParse(manifest.version, out Version remote);
                bool hasCurrent = Version.TryParse(Main.Mod?.Info?.Version ?? "0.0.0", out Version current);
                bool newer = hasRemote && hasCurrent && remote > current;

                lock (Sync)
                {
                    latest = manifest.version;
                    downloadUrl = string.IsNullOrEmpty(manifest.url)
                        ? "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases"
                        : manifest.url;
                    notes = manifest.notes ?? string.Empty;
                    status = newer ? "available" : "current";
                }

                if (newer)
                {
                    Interlocked.Exchange(ref needsApply, 1);
                    Main.Log("[Update] New version available: " + manifest.version);
                }
                else
                {
                    Main.Log("[Update] Up to date (" + manifest.version + ").");
                }
            }
            catch (Exception exception)
            {
                Main.Log("[Update] Check failed: " + exception.Message);
                lock (Sync)
                {
                    status = "failed";
                }
            }

            MarkChanged();
        }

        private static void MarkChanged()
        {
            Interlocked.Increment(ref changeCount);
        }
    }
}
