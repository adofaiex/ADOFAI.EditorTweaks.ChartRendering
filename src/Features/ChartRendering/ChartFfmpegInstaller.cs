using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Downloads the FFmpeg build AETC renders with. The mod package itself no longer ships
    // ffmpeg.exe, so a fresh install has to fetch it once (about 88 MB) before the first render.
    //
    // Two mirrors are tried in order: ADOFAITools (primary, mainland friendly) and the GitHub
    // release asset (overseas / fallback). Every download is verified against a pinned SHA256
    // before it is unpacked, so a mirror cannot swap the binary.
    internal static class ChartFfmpegInstaller
    {
        public const string Version = "9.0.2";

        private const long ExpectedSize = 88_041_022L;
        private const string ExpectedSha256 = "f33a7aa4d379d301dbe650d12b658b15d049974a32948c462bd24726a350b846";
        private const string ArchiveFileName = "FFmpeg-9.0.2-full_build-win64.zip";
        private const string EntryNameHint = "ffmpeg.exe";

        private static readonly string[] SourceNames = { "ADOFAITools", "GitHub" };
        private static readonly string[] SourceUrls =
        {
            "https://assets.adofaitools.top/files/bf68b99d-e905-4fc8-8dd4-0b050e0e0f41.zip",
            "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases/download/ffmpeg-9.0.2/FFmpeg-9.0.2-full_build-win64.zip",
        };

        private static readonly object Sync = new object();
        private static Thread? worker;
        private static bool running;
        private static string state = "missing";
        private static string message = string.Empty;
        private static string sourceName = string.Empty;
        private static long downloadedBytes;
        private static long totalBytes = ExpectedSize;
        private static int sourceIndex;

        public static string TargetPath
        {
            get
            {
                string bundled = BundledTargetPath();
                return string.IsNullOrEmpty(bundled) ? ChartRenderPaths.GetFfmpegPath() : bundled;
            }
        }

        public static bool IsReady => File.Exists(ChartRenderPaths.GetFfmpegPath());

        public static bool IsBusy
        {
            get
            {
                lock (Sync)
                {
                    return running;
                }
            }
        }

        public static string Status
        {
            get
            {
                lock (Sync)
                {
                    return state;
                }
            }
        }

        public static float Progress
        {
            get
            {
                lock (Sync)
                {
                    return totalBytes <= 0 ? 0f : Mathf.Clamp01((float)downloadedBytes / totalBytes);
                }
            }
        }

        // Called from the settings page and from the render session. Safe to call repeatedly.
        public static bool Start(string reason)
        {
            lock (Sync)
            {
                if (IsReady || running)
                {
                    return false;
                }

                running = true;
                state = "downloading";
                message = string.Empty;
                downloadedBytes = 0;
                totalBytes = ExpectedSize;
                sourceName = string.Empty;
                sourceIndex = 0;
                worker = new Thread(() => DownloadLoop(reason))
                {
                    IsBackground = true,
                    Name = "ADOFAI.EditorTweaks.ChartRendering.FfmpegInstaller"
                };
                worker.Start();
                return true;
            }
        }

        public static string Describe()
        {
            lock (Sync)
            {
                if (IsReady)
                {
                    return "FFmpeg " + Version + " ready.";
                }

                if (!running)
                {
                    return state == "failed" && message.Length > 0 ? message : "FFmpeg is not installed.";
                }

                int percent = (int)Math.Round(Progress * 100f);
                return "下载 FFmpeg " + percent + "%（" + Megabytes(downloadedBytes) + " / "
                    + Megabytes(totalBytes) + " MB，来源 " + (sourceName.Length == 0 ? "准备中" : sourceName)
                    + "） / Downloading FFmpeg " + percent + "%.";
            }
        }

        public static Dictionary<string, object> BuildState()
        {
            lock (Sync)
            {
                bool ready = IsReady;
                return new Dictionary<string, object>
                {
                    ["status"] = ready ? "ready" : state,
                    ["version"] = Version,
                    ["progress"] = ready ? 1f : Progress,
                    ["downloadedBytes"] = downloadedBytes,
                    ["totalBytes"] = totalBytes,
                    ["source"] = sourceName,
                    ["message"] = message,
                    ["path"] = TargetPath,
                    ["sha256"] = ExpectedSha256
                };
            }
        }

        private static void DownloadLoop(string reason)
        {
            string workDirectory = WorkDirectory();
            string archivePath = Path.Combine(workDirectory, ArchiveFileName);

            try
            {
                Directory.CreateDirectory(workDirectory);
                for (sourceIndex = 0; sourceIndex < SourceUrls.Length; sourceIndex++)
                {
                    string url = SourceUrls[sourceIndex];
                    string name = SourceNames[sourceIndex];
                    lock (Sync)
                    {
                        sourceName = name;
                        message = string.Empty;
                        downloadedBytes = 0;
                        state = "downloading";
                    }

                    Main.Log("[FFmpeg] Downloading " + Version + " from " + name + " (" + url + ") because " + reason + ".");
                    try
                    {
                        DownloadArchive(url, archivePath);
                        lock (Sync)
                        {
                            state = "verifying";
                        }

                        string digest = ComputeSha256(archivePath);
                        if (!string.Equals(digest, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("SHA256 mismatch: expected " + ExpectedSha256 + ", got " + digest + ".");
                        }

                        lock (Sync)
                        {
                            state = "installing";
                        }

                        InstallFromArchive(archivePath);
                        lock (Sync)
                        {
                            state = "ready";
                            running = false;
                            message = string.Empty;
                            downloadedBytes = ExpectedSize;
                            totalBytes = ExpectedSize;
                        }

                        Main.Log("[FFmpeg] Installed " + Version + " to " + TargetPath + ".");
                        ChartEncoderCapabilities.InvalidateSessionProbes(ChartRenderPaths.GetFfmpegPath());
                        TryDelete(archivePath);
                        return;
                    }
                    catch (Exception exception)
                    {
                        Main.Log("[FFmpeg] " + name + " failed: " + exception.Message);
                        lock (Sync)
                        {
                            message = name + " 下载失败：" + exception.Message;
                        }

                        TryDelete(archivePath);
                    }
                }

                lock (Sync)
                {
                    state = "failed";
                    running = false;
                    message = "FFmpeg 自动下载失败，请检查网络后重试，或手动安装 FFmpeg（放到游戏目录的 ffmpeg 文件夹）。";
                }
            }
            catch (Exception exception)
            {
                Main.Log("[FFmpeg] Installer crashed: " + exception);
                lock (Sync)
                {
                    state = "failed";
                    running = false;
                    message = "FFmpeg 安装失败：" + exception.Message;
                }
            }
        }

        private static void DownloadArchive(string url, string path)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ADOFAI.EditorTweaks.ChartRendering/" + Version;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;

            string partial = path + ".part";
            TryDelete(partial);

            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long length = response.ContentLength;
                lock (Sync)
                {
                    totalBytes = length > 0 ? length : ExpectedSize;
                }

                byte[] buffer = new byte[64 * 1024];
                long written = 0;
                DateTime lastReport = DateTime.UtcNow;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    written += read;
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds >= 200)
                    {
                        lastReport = now;
                        lock (Sync)
                        {
                            downloadedBytes = written;
                        }
                    }
                }

                lock (Sync)
                {
                    downloadedBytes = written;
                }
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(partial, path);
        }

        private static void InstallFromArchive(string archivePath)
        {
            string target = TargetPath;
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidOperationException("Cannot resolve the FFmpeg install path.");
            }

            string directory = Path.GetDirectoryName(target) ?? string.Empty;
            Directory.CreateDirectory(directory);

            string temporary = target + ".new";
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry? entry = null;
                foreach (ZipArchiveEntry candidate in archive.Entries)
                {
                    if (candidate.Name.EndsWith(EntryNameHint, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = candidate;
                        break;
                    }
                }

                if (entry == null)
                {
                    throw new InvalidDataException("The archive does not contain " + EntryNameHint + ".");
                }

                TryDelete(temporary);
                entry.ExtractToFile(temporary, overwrite: true);
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(temporary, target);
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string BundledTargetPath()
        {
            if (Main.Mod == null || string.IsNullOrEmpty(Main.Mod.Path))
            {
                return string.Empty;
            }

            return Path.Combine(Main.Mod.Path, "ThirdParty", "FFmpeg", EntryNameHint);
        }

        private static string WorkDirectory()
        {
            string root = Main.Mod == null || string.IsNullOrEmpty(Main.Mod.Path)
                ? Application.persistentDataPath
                : Main.Mod.Path;
            return Path.Combine(root, "Workspace", "Downloads");
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
