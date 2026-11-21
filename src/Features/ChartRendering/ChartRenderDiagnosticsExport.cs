using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityModManagerNet;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // "Export diagnostics" on the Tools page: one zip a player can send to a developer or paste into
    // an AI chat. It holds the newest render logs, the relevant part of Player.log (and the previous
    // session's log, which is where a crash ends up), facts about the PC and mods, the settings
    // without secrets, and the log analysis guide, so an AI can read it without other context.
    //
    // Privacy: the Windows user folder becomes %USERPROFILE%, the user name is replaced, and the web
    // access token and device keys are removed.
    internal static class ChartRenderDiagnosticsExport
    {
        private const int RenderLogCount = 3;
        private const long PlayerLogLimitBytes = 8L * 1024 * 1024;
        private const int KeepExports = 5;
        private static readonly Regex TokenPattern = new Regex(@"(token|view|k)=[0-9a-fA-F]{24,}", RegexOptions.Compiled);
        private static readonly Regex OffsetPattern = new Regex(@"Player\.log offset at render start: (\d+)", RegexOptions.Compiled);

        public static string GetDirectory(Settings settings)
        {
            return Path.Combine(ChartRenderPaths.GetWorkspaceDirectory(settings), "Diagnostics");
        }

        public static string? Resolve(Settings settings, string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = Path.Combine(GetDirectory(settings), name);
            return File.Exists(path) ? path : null;
        }

        public static string PlayerLogPath()
        {
            try
            {
                string path = Application.consoleLogPath;
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }
            catch
            {
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "7th Beat Games", "A Dance of Fire and Ice", "Player.log");
        }

        // Called at render start (main thread) so the export can cut Player.log at this point.
        public static long CurrentPlayerLogLength()
        {
            try
            {
                FileInfo info = new FileInfo(PlayerLogPath());
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        // Unity APIs: must run on the main thread.
        public static string CollectSystemInfo()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("# System");
            b.AppendLine("Exported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            b.AppendLine("Mod: " + (Main.Mod?.Info?.Version ?? "?"));
            b.AppendLine("Game: " + Application.version + " (r" + ChartModStatus.GameReleaseNumber() + ")");
            b.AppendLine("Unity: " + Application.unityVersion);
            b.AppendLine("OS: " + SystemInfo.operatingSystem);
            b.AppendLine("CPU: " + SystemInfo.processorType + " x" + SystemInfo.processorCount);
            b.AppendLine("RAM: " + SystemInfo.systemMemorySize + " MB");
            b.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVendor + ")");
            b.AppendLine("GPU driver / API: " + SystemInfo.graphicsDeviceVersion);
            b.AppendLine("VRAM: " + SystemInfo.graphicsMemorySize + " MB");
            b.AppendLine("Screen: " + Screen.width + "x" + Screen.height + " @ " + Screen.currentResolution.refreshRateRatio.value.ToString("0.##") + " Hz, fullscreen=" + Screen.fullScreen);
            b.AppendLine("Audio: " + AudioSettings.outputSampleRate + " Hz, DSP buffer " + AudioSettings.GetConfiguration().dspBufferSize);
            b.AppendLine();
            b.AppendLine("# FFmpeg");
            b.AppendLine("Path: " + ChartRenderPaths.GetFfmpegPath());
            b.AppendLine("Exists: " + File.Exists(ChartRenderPaths.GetFfmpegPath()));
            b.AppendLine();
            b.AppendLine("# Encoder detection (encoder = works on this PC)");
            Dictionary<string, object> encoders = ChartEncoderCapabilities.BuildState();
            foreach (KeyValuePair<string, object> pair in encoders)
            {
                if (pair.Value is Dictionary<string, object> results)
                {
                    foreach (KeyValuePair<string, object> result in results)
                    {
                        b.AppendLine(result.Key + " = " + result.Value);
                    }
                }
                else
                {
                    b.AppendLine(pair.Key + ": " + pair.Value);
                }
            }

            b.AppendLine();
            b.AppendLine("# Mods (UMM)");
            try
            {
                foreach (UnityModManager.ModEntry entry in UnityModManager.modEntries)
                {
                    b.AppendLine(entry.Info.Id + " " + entry.Info.Version + (entry.Enabled ? "" : " (disabled)") + (entry.ErrorOnLoading ? " (load error)" : ""));
                }
            }
            catch (Exception exception)
            {
                b.AppendLine("Could not list mods: " + exception.Message);
            }

            b.AppendLine();
            b.AppendLine("# Settings (secrets removed)");
            b.AppendLine(DescribeSettings(Main.Settings));
            return b.ToString();
        }

        private static string DescribeSettings(Settings settings)
        {
            StringBuilder b = new StringBuilder();
            foreach (System.Reflection.FieldInfo field in typeof(Settings).GetFields())
            {
                if (field.IsStatic)
                {
                    continue;
                }

                string name = field.Name;
                if (name == "WebUiDeviceKey" || name == "WebUiViewKey")
                {
                    b.AppendLine(name + " = <hidden>");
                    continue;
                }

                if (name == "EncoderCapabilitiesCache")
                {
                    continue;
                }

                object? value = field.GetValue(settings);
                b.AppendLine(name + " = " + Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            return b.ToString();
        }

        public static string Export(Settings settings, string systemInfo, string accessToken)
        {
            string directory = GetDirectory(settings);
            Directory.CreateDirectory(directory);
            string zipPath = Path.Combine(directory, "AETC-诊断包-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");

            List<string> renderLogs = NewestRenderLogs(settings);
            long playerLogStart = EarliestOffset(renderLogs);

            using (FileStream file = new FileStream(zipPath, FileMode.CreateNew))
            using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                AddText(zip, "00-先读我-日志分析指南.md", ReadGuide(), settings, accessToken);
                AddText(zip, "01-系统与设置.txt", systemInfo, settings, accessToken);
                for (int i = 0; i < renderLogs.Count; i++)
                {
                    AddText(zip, "02-渲染日志-" + (i + 1) + "-" + Path.GetFileName(renderLogs[i]), SafeRead(renderLogs[i], 0, long.MaxValue), settings, accessToken);
                }

                string playerLog = PlayerLogPath();
                AddText(zip, "03-游戏日志-Player.log", ReadPlayerLog(playerLog, playerLogStart), settings, accessToken);
                string previous = Path.Combine(Path.GetDirectoryName(playerLog) ?? string.Empty, "Player-prev.log");
                if (File.Exists(previous))
                {
                    AddText(zip, "04-上一次游戏的日志-Player-prev.log（崩溃时看这个）", ReadPlayerLog(previous, 0), settings, accessToken);
                }
            }

            PruneOldExports(directory);
            return zipPath;
        }

        private static List<string> NewestRenderLogs(Settings settings)
        {
            List<string> logs = new List<string>();
            string folder = Path.Combine(ChartRenderPaths.GetWorkspaceDirectory(settings), "Logs");
            if (Directory.Exists(folder))
            {
                logs.AddRange(Directory.GetFiles(folder, "render_*.log"));
            }

            logs.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            if (logs.Count > RenderLogCount)
            {
                logs.RemoveRange(RenderLogCount, logs.Count - RenderLogCount);
            }

            return logs;
        }

        // The earliest "Player.log offset at render start" among the included render logs, so the
        // game log starts where those renders began. -1 when unknown.
        private static long EarliestOffset(List<string> renderLogs)
        {
            long earliest = -1;
            foreach (string log in renderLogs)
            {
                Match match = OffsetPattern.Match(SafeRead(log, 0, 64 * 1024));
                if (match.Success && long.TryParse(match.Groups[1].Value, out long offset)
                    && (earliest < 0 || offset < earliest))
                {
                    earliest = offset;
                }
            }

            return earliest;
        }

        // Whole file when small; otherwise from the earliest included render (if that is within the
        // limit) or the last 8 MB. The cut is marked so a reader knows text was left out.
        private static string ReadPlayerLog(string path, long renderStart)
        {
            if (!File.Exists(path))
            {
                return "(not found: " + path + ")";
            }

            long length = new FileInfo(path).Length;
            long start = 0;
            if (length > PlayerLogLimitBytes)
            {
                start = renderStart >= 0 && length - renderStart <= PlayerLogLimitBytes * 4 ? renderStart : length - PlayerLogLimitBytes;
            }

            string text = SafeRead(path, start, long.MaxValue);
            return start > 0 ? "[... " + start + " bytes before the first included render omitted ...]\n" + text : text;
        }

        private static string SafeRead(string path, long start, long maxBytes)
        {
            try
            {
                // The game keeps Player.log open for writing; open it shared.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (start > 0 && start < stream.Length)
                    {
                        stream.Seek(start, SeekOrigin.Begin);
                    }

                    long count = Math.Min(maxBytes, stream.Length - stream.Position);
                    byte[] data = new byte[Math.Max(0, count)];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    return Encoding.UTF8.GetString(data, 0, read);
                }
            }
            catch (Exception exception)
            {
                return "(could not read " + path + ": " + exception.Message + ")";
            }
        }

        private static string ReadGuide()
        {
            string path = Main.Mod == null ? string.Empty : Path.Combine(Main.Mod.Path, "Resources", "LogGuide.md");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "(LogGuide.md was not found in the mod's Resources folder.)";
        }

        private static void AddText(ZipArchive zip, string name, string text, Settings settings, string accessToken)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(Sanitize(text, settings, accessToken));
            }
        }

        public static string Sanitize(string text, Settings settings, string accessToken)
        {
            text = WebUi.WebUiAccess.Redact(text, accessToken, settings);
            text = TokenPattern.Replace(text, "$1=<hidden>");
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                text = Replace(text, profile, "%USERPROFILE%");
                text = Replace(text, profile.Replace('\\', '/'), "%USERPROFILE%");
            }

            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
            {
                text = Replace(text, "\\Users\\" + user, "\\Users\\<user>");
                text = Replace(text, "/Users/" + user, "/Users/<user>");
            }

            return text;
        }

        private static string Replace(string text, string find, string replacement)
        {
            return Regex.Replace(text, Regex.Escape(find), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        private static void PruneOldExports(string directory)
        {
            try
            {
                List<string> files = new List<string>(Directory.GetFiles(directory, "AETC-*.zip"));
                files.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (int i = KeepExports; i < files.Count; i++)
                {
                    File.Delete(files[i]);
                }
            }
            catch
            {
            }
        }
    }
}
