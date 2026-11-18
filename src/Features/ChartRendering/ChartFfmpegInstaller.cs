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
    // Sources are tried in order: the AETC-specific slim build first (13 MB), then the full
    // gyan.dev build as a fallback (88 MB) in case the slim build is unavailable or a machine
    // hits a codec problem with it. Each entry pins its own size and SHA256, so a mirror cannot
    // swap the binary.
    internal static class ChartFfmpegInstaller
    {
        public const string Version = "AETC N-129000";

        private const string EntryNameHint = "ffmpeg.exe";

        private const long SlimSize = 13_279_311L;
        private const long SlimInstalledSize = 46_367_232L;
        private const string SlimSha256 = "90e8f8d2dbaa47fffc2b7118bc1345fec7b36a7046a22269f4a3928083b24558";
        private const long FullSize = 88_041_022L;
        private const long FullInstalledSize = 227_823_104L;
        private const string FullSha256 = "f33a7aa4d379d301dbe650d12b658b15d049974a32948c462bd24726a350b846";

        private sealed class Source
        {
            public Source(string name, string url, long size, long installedSize, string sha256)
            {
                Name = name;
                Url = url;
                Size = size;
                InstalledSize = installedSize;
                Sha256 = sha256;
            }

            public string Name { get; }
            public string Url { get; }
            public long Size { get; }
            public long InstalledSize { get; }
            public string Sha256 { get; }
        }

        private static readonly Source[] Sources =
        {
            new Source("ADOFAITools 精简版",
                "https://assets.adofaitools.top/files/92c16423-c815-44a5-8232-7580939a47cf.zip",
                SlimSize, SlimInstalledSize, SlimSha256),
            new Source("GitHub 精简版",
                "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases/download/ffmpeg-aetc/FFmpeg-AETC-N-129000-win64.zip",
                SlimSize, SlimInstalledSize, SlimSha256),
            new Source("ADOFAITools 完整版",
                "https://assets.adofaitools.top/files/bf68b99d-e905-4fc8-8dd4-0b050e0e0f41.zip",
                FullSize, FullInstalledSize, FullSha256),
            new Source("GitHub 完整版",
                "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases/download/ffmpeg-9.0.2/FFmpeg-9.0.2-full_build-win64.zip",
                FullSize, FullInstalledSize, FullSha256),
        };

        private static readonly object Sync = new object();
        private static Thread? worker;
        private static bool running;
        private static string state = "missing";
        private static string message = string.Empty;
        private static string sourceName = string.Empty;
        private static string installedVariant = string.Empty;
        private static long downloadedBytes;
        private static long totalBytes = SlimSize;
        private static long installedBytes = SlimInstalledSize;
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
                totalBytes = SlimSize;
                installedBytes = SlimInstalledSize;
                sourceName = string.Empty;
                installedVariant = string.Empty;
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
                    ["version"] = ready && installedVariant.Length > 0 ? installedVariant : Version,
                    ["variant"] = installedVariant,
                    ["progress"] = ready ? 1f : Progress,
                    ["downloadedBytes"] = downloadedBytes,
                    ["totalBytes"] = totalBytes,
                    ["installedBytes"] = installedBytes,
                    ["source"] = sourceName,
                    ["message"] = message,
                    ["path"] = TargetPath,
                    ["sha256"] = string.Empty
                };
            }
        }

        private static void DownloadLoop(string reason)
        {
            string workDirectory = WorkDirectory();
            string archivePath = Path.Combine(workDirectory, "ffmpeg-download.zip");

            try
            {
                Directory.CreateDirectory(workDirectory);
                for (sourceIndex = 0; sourceIndex < Sources.Length; sourceIndex++)
                {
                    Source source = Sources[sourceIndex];
                    if (string.IsNullOrEmpty(source.Url))
                    {
                        continue;
                    }

                    lock (Sync)
                    {
                        sourceName = source.Name;
                        message = string.Empty;
                        downloadedBytes = 0;
                        totalBytes = source.Size;
                        installedBytes = source.InstalledSize;
                        state = "downloading";
                    }

                    Main.Log("[FFmpeg] Downloading " + source.Name + " (" + source.Url + ") because " + reason + ".");
                    try
                    {
                        DownloadArchive(source.Url, archivePath);
                        lock (Sync)
                        {
                            state = "verifying";
                        }

                        string digest = ComputeSha256(archivePath);
                        if (!string.Equals(digest, source.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("SHA256 mismatch: expected " + source.Sha256 + ", got " + digest + ".");
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
                            installedVariant = source.Name;
                            downloadedBytes = source.Size;
                            totalBytes = source.Size;
                        }

                        Main.Log("[FFmpeg] Installed " + source.Name + " to " + TargetPath + ".");
                        ChartEncoderCapabilities.InvalidateSessionProbes(ChartRenderPaths.GetFfmpegPath());
                        TryDelete(archivePath);
                        return;
                    }
                    catch (Exception exception)
                    {
                        Main.Log("[FFmpeg] " + source.Name + " failed: " + exception.Message);
                        lock (Sync)
                        {
                            message = source.Name + " 下载失败：" + exception.Message;
                        }

                        TryDelete(archivePath);
                    }
                }

                lock (Sync)
                {
                    state = "failed";
                    running = false;
                    message = "FFmpeg 自动下载失败，请检查网络后重试；也可以改用完整版 FFmpeg（下载带 -Full 后缀的安装包），或手动把 ffmpeg.exe 放到游戏目录的 ffmpeg 文件夹。";
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
                    totalBytes = length > 0 ? length : totalBytes;
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
