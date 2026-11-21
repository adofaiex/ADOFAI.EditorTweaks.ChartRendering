using System;
using System.Collections.Generic;
using System.IO;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Keeps the workspace folder from filling up with leftovers.
    //
    // Each render writes its raw video and audio into Workspace\CurrentRender_<time>_<id>\. They are
    // as large as the finished video and are only needed until the mux step finishes. Renders
    // remove their own folder when they end; this class also removes folders left behind by a crash,
    // a killed game process or an abandoned FFmpeg worker, and reports how much space is used.
    internal static class ChartRenderWorkspace
    {
        public const string TempDirectoryPrefix = "CurrentRender";

        // At mod load no render can be running in this process, so every leftover folder is stale.
        public static void CleanupAtStartup(Settings settings)
        {
            try
            {
                string workspace = ChartRenderPaths.GetWorkspaceDirectory(settings);
                int removed = DeleteTempDirectories(workspace, keep: null, out long bytes);
                if (removed > 0)
                {
                    Main.Log("Removed " + removed + " leftover render folder(s) from the workspace ("
                        + FormatBytes(bytes) + ").");
                }
            }
            catch (Exception exception)
            {
                Main.Log("Workspace cleanup at startup failed: " + exception.Message);
            }
        }

        // Deletes every CurrentRender* folder except keep. A folder still in use (an abandoned FFmpeg
        // worker holding a file) cannot be deleted; it is skipped and tried again next time.
        public static int DeleteTempDirectories(string workspace, string? keep, out long bytesFreed)
        {
            bytesFreed = 0;
            if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            {
                return 0;
            }

            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(workspace, TempDirectoryPrefix + "*"))
            {
                if (keep != null && string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long size = GetDirectorySize(directory);
                try
                {
                    Directory.Delete(directory, recursive: true);
                    bytesFreed += size;
                    removed++;
                }
                catch
                {
                }
            }

            return removed;
        }

        public static Dictionary<string, object> BuildState(Settings settings)
        {
            string workspace = ChartRenderPaths.GetWorkspaceDirectory(settings);
            long tempBytes = 0;
            int tempFolders = 0;
            long logBytes = 0;
            int logFiles = 0;
            try
            {
                if (Directory.Exists(workspace))
                {
                    foreach (string directory in Directory.EnumerateDirectories(workspace, TempDirectoryPrefix + "*"))
                    {
                        tempFolders++;
                        tempBytes += GetDirectorySize(directory);
                    }

                    string logs = Path.Combine(workspace, "Logs");
                    if (Directory.Exists(logs))
                    {
                        foreach (string file in Directory.EnumerateFiles(logs, "*.log"))
                        {
                            logFiles++;
                            logBytes += SafeLength(file);
                        }
                    }
                }
            }
            catch
            {
            }

            return new Dictionary<string, object>
            {
                ["path"] = workspace,
                ["tempFolders"] = tempFolders,
                ["tempBytes"] = tempBytes,
                ["logFiles"] = logFiles,
                ["logBytes"] = logBytes,
            };
        }

        public static long GetDirectorySize(string directory)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    total += SafeLength(file);
                }
            }
            catch
            {
            }

            return total;
        }

        private static long SafeLength(string file)
        {
            try
            {
                return new FileInfo(file).Length;
            }
            catch
            {
                return 0;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
            }

            return (bytes / (1024.0 * 1024)).ToString("0") + " MB";
        }
    }
}
