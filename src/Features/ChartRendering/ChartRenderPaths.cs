using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    internal static class ChartRenderPaths
    {
        private const int MaxBaseFileNameLength = 96;

        // Order: the user's custom path, the FFmpeg bundled with the mod, then <game>\ffmpeg\ffmpeg.exe.
        // When none exists the bundled path is returned, so the "missing FFmpeg" error names it.
        public static string GetFfmpegPath()
        {
            string custom = ExpandPath(Main.Settings?.FfmpegCustomPath ?? string.Empty);
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
            {
                return custom;
            }

            string bundled = Main.Mod == null ? string.Empty : Path.Combine(Main.Mod.Path, "ThirdParty", "FFmpeg", "ffmpeg.exe");
            if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled))
            {
                return bundled;
            }

            try
            {
                string gameFfmpeg = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "ffmpeg", "ffmpeg.exe"));
                if (File.Exists(gameFfmpeg))
                {
                    return gameFfmpeg;
                }
            }
            catch
            {
            }

            return bundled;
        }

        public static string GetWorkspaceDirectory(Settings settings)
        {
            return ExpandPath(settings.ChartRenderWorkspaceDirectory);
        }

        public static string GetExportDirectory(Settings settings)
        {
            return ExpandPath(settings.ChartRenderExportDirectory);
        }

        public static string MakeSafeFileName(string raw)
        {
            raw = StripRichTextTags(raw);
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = "ADOFAI_Render";
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(c, '_');
            }

            raw = Regex.Replace(raw, @"\s+", " ");
            raw = Regex.Replace(raw, @"_+", "_");
            raw = raw.Trim(' ', '.', '_');
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = "ADOFAI_Render";
            }

            return raw.Length <= MaxBaseFileNameLength
                ? raw
                : raw.Substring(0, MaxBaseFileNameLength).Trim(' ', '.', '_');
        }

        private static string StripRichTextTags(string raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? string.Empty
                : Regex.Replace(raw, @"<[^>]*>", string.Empty);
        }

        private static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            try
            {
                return Path.GetFullPath(expanded);
            }
            catch
            {
                // Invalid characters must behave like an empty custom path, not crash the render
                // before the bundled FFmpeg fallback is tried.
                return string.Empty;
            }
        }
    }
}
