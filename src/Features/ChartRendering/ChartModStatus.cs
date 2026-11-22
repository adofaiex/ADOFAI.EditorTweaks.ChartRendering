using System;
using UnityModManagerNet;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // UMM renders Info.DisplayName in the mod list every frame and has no free-form status field
    // (its Status column is icons only). Appending a short suffix to DisplayName is the supported
    // way to surface state without touching the list's own columns.
    internal static class ChartModStatus
    {
        private const string BaseName = "ADOFAI Editor Tweaks - Chart Rendering";
        private static string lastText = string.Empty;
        private static int releaseNumber = -2;

        // The game's build number (shown in game as "r150"). GCNS.releaseNumber is a constant, so it
        // is read through reflection from the running game rather than compiled into this mod.
        public static int GameReleaseNumber()
        {
            if (releaseNumber != -2)
            {
                return releaseNumber;
            }

            releaseNumber = -1;
            try
            {
                System.Reflection.FieldInfo? field = typeof(GCNS).GetField("releaseNumber",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                object? value = field == null ? null : (field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null));
                if (value != null)
                {
                    releaseNumber = Convert.ToInt32(value);
                }
            }
            catch
            {
            }

            return releaseNumber;
        }

        // Main thread, once per frame from ModEntry.OnUpdate.
        public static void Update(UnityModManager.ModEntry entry)
        {
            string suffix = string.Empty;
            if (ChartFfmpegInstaller.IsBusy)
            {
                int percent = (int)Math.Round(ChartFfmpegInstaller.Progress * 100f);
                suffix = "[>] FFmpeg 下载中 " + percent + "%";
            }
            else if (!ChartFfmpegInstaller.IsReady)
            {
                int countdown = ChartFfmpegInstaller.CountdownSeconds;
                if (countdown >= 0)
                {
                    suffix = "[>] " + countdown + " 秒后下载 FFmpeg";
                }
                else if (ChartFfmpegInstaller.Status == "failed")
                {
                    suffix = "[!] FFmpeg 下载失败";
                }
            }

            if (ChartUpdateChecker.IsAvailable)
            {
                string updateSuffix = "[^] 有新版本 " + ChartUpdateChecker.Latest;
                suffix = suffix.Length == 0 ? updateSuffix : suffix + "  " + updateSuffix;
            }

            string text = suffix.Length == 0 ? BaseName : BaseName + "  " + suffix;
            if (string.Equals(text, lastText, StringComparison.Ordinal))
            {
                return;
            }

            lastText = text;
            try
            {
                entry.Info.DisplayName = text;
            }
            catch
            {
            }
        }

        public static void Reset(UnityModManager.ModEntry entry)
        {
            lastText = BaseName;
            try
            {
                entry.Info.DisplayName = BaseName;
            }
            catch
            {
            }
        }
    }
}
