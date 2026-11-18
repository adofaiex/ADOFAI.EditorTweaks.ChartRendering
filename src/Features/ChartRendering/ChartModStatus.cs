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

        // Main thread, once per frame from ModEntry.OnUpdate.
        public static void Update(UnityModManager.ModEntry entry)
        {
            string suffix;
            if (ChartFfmpegInstaller.IsBusy)
            {
                int percent = (int)Math.Round(ChartFfmpegInstaller.Progress * 100f);
                suffix = "[FFmpeg 下载中 " + percent + "%]";
            }
            else if (!ChartFfmpegInstaller.IsReady)
            {
                suffix = ChartFfmpegInstaller.Status == "failed" ? "[FFmpeg 下载失败]" : "[缺少 FFmpeg]";
            }
            else
            {
                suffix = string.Empty;
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
