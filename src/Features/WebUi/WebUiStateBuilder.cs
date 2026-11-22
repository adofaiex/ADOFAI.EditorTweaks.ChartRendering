using System;
using System.Collections.Generic;
using System.IO;
using ADOFAI.EditorTweaks.Api.Rendering;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;
using ADOFAI.EditorTweaks.ChartRendering.Patching;
using ChartRenderResult = ADOFAI.EditorTweaks.Api.Rendering.ChartRenderResult;
using GDMiniJSON;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    internal static class WebUiStateBuilder
    {
        public static string BuildJson(ChartRenderTask? renderTask) { return Json.Serialize(Build(renderTask)); }

        public static Dictionary<string, object> Build(ChartRenderTask? renderTask)
        {
            IReadOnlyList<PatchGroupStatus> statuses = PatchManager.Statuses;
            List<object> patches = new List<object>();
            int activeCount = 0;
            foreach (PatchGroupStatus status in statuses)
            {
                if (status.State == PatchGroupState.Active) activeCount++;
                string reason = status.Reason;
                if (status.State == PatchGroupState.Failed && !string.IsNullOrEmpty(status.FailedPatchName))
                {
                    reason = string.IsNullOrEmpty(reason)
                        ? status.FailedPatchName
                        : status.FailedPatchName + ": " + reason;
                }

                patches.Add(new Dictionary<string, object>
                {
                    ["id"] = status.Feature.ToString(),
                    ["name"] = status.Name,
                    ["description"] = status.Feature == PatchFeature.RenderInputGuard
                        ? "渲染期间屏蔽输入干扰"
                        : status.Feature == PatchFeature.RenderVisualTime
                            ? "修正视频中星球转动、彩虹变色等动画过快"
                            : "谱面画面、音频和视频背景渲染",
                    ["state"] = GetPatchState(status.State),
                    ["patchCount"] = status.PatchCount,
                    ["failedPatchName"] = status.FailedPatchName,
                    ["reason"] = reason
                });
            }

            return new Dictionary<string, object>
            {
                ["server"] = new Dictionary<string, object>
                {
                    ["connected"] = true,
                    ["version"] = Main.Mod?.Info?.Version ?? "0.0.0",
                    ["lanEnabled"] = WebUiHost.LanEnabled,
                    ["lanUrl"] = WebUiHost.LanUrl,
                    ["lanUrls"] = new List<string>(WebUiHost.LanUrls),
                    ["lanPort"] = WebUiHost.LanPort,
                    ["lanViewUrls"] = WebUiHost.LanViewUrls
                },
                ["compatibility"] = new Dictionary<string, object>
                {
                    ["gameVersion"] = Application.version ?? string.Empty,
                    ["gameRelease"] = ChartModStatus.GameReleaseNumber(),
                    ["editorVersion"] = Application.version ?? string.Empty,
                    ["modVersion"] = Main.Mod?.Info?.Version ?? "0.0.0"
                },
                ["patches"] = patches,
                ["patchSummary"] = new Dictionary<string, object>
                {
                    ["active"] = activeCount,
                    ["total"] = statuses.Count,
                    ["registrationError"] = statuses.Count == 0
                },
                ["settings"] = BuildSettings(Main.Settings),
                ["encoders"] = Features.ChartRendering.ChartEncoderCapabilities.BuildState(),
                ["ffmpeg"] = Features.ChartRendering.ChartFfmpegInstaller.BuildState(),
                ["update"] = Features.ChartRendering.ChartUpdateChecker.BuildState(),
                ["render"] = BuildRenderState(renderTask),
                ["quality"] = ChartRenderQualityTiers.BuildState(Main.Settings),
                ["chart"] = Cached(ref chartCache, ref chartCacheTime, 2f, BuildChartState),
                ["disk"] = Cached(ref diskCache, ref diskCacheTime, 5f, BuildDiskState),
                ["workspace"] = Cached(ref workspaceCache, ref workspaceCacheTime, 5f, () => ChartRenderWorkspace.BuildState(Main.Settings)),
                ["lastResult"] = new Dictionary<string, object>
                {
                    ["path"] = ChartRenderSession.LastOutputPath,
                    ["bytes"] = ChartRenderSession.LastOutputBytes,
                    ["averageMbps"] = Math.Round(ChartRenderSession.LastAverageMbps, 2),
                    ["capRatio"] = Math.Round(ChartRenderSession.LastCapRatio, 3)
                }
            };
        }

        // State is rebuilt up to ten times a second; these values change slowly and touch the disk.
        private static Dictionary<string, object>? chartCache;
        private static Dictionary<string, object>? diskCache;
        private static Dictionary<string, object>? workspaceCache;
        private static float chartCacheTime = -100f;
        private static float diskCacheTime = -100f;
        private static float workspaceCacheTime = -100f;

        public static void InvalidateCaches()
        {
            chartCacheTime = diskCacheTime = workspaceCacheTime = -100f;
        }

        private static Dictionary<string, object> Cached(ref Dictionary<string, object>? cache, ref float time, float seconds, Func<Dictionary<string, object>> build)
        {
            float now = Time.realtimeSinceStartup;
            if (cache == null || now - time >= seconds)
            {
                try
                {
                    cache = build();
                }
                catch
                {
                    cache = cache ?? new Dictionary<string, object>();
                }

                time = now;
            }

            return cache;
        }

        // Length of the loaded chart and of the editor selection, for the size estimate.
        private static Dictionary<string, object> BuildChartState()
        {
            double whole = -1;
            double selection = -1;
            List<scrFloor>? floors = ADOBase.lm == null ? null : ADOBase.lm.listFloors;
            if (floors != null && floors.Count > 1)
            {
                whole = floors[floors.Count - 1].entryTimePitchAdj;
                if (ChartRenderRange.TryGetEditorSelectedRange(out int start, out int end, out _)
                    && start >= 0 && end > start && end < floors.Count)
                {
                    selection = floors[end].entryTimePitchAdj - floors[start].entryTimePitchAdj;
                }
            }

            return new Dictionary<string, object>
            {
                ["loaded"] = whole > 0,
                ["seconds"] = Math.Round(whole, 2),
                ["selectionSeconds"] = Math.Round(selection, 2)
            };
        }

        private static Dictionary<string, object> BuildDiskState()
        {
            Settings settings = Main.Settings;
            return new Dictionary<string, object>
            {
                ["export"] = DriveState(ChartRenderPaths.GetExportDirectory(settings)),
                ["workspace"] = DriveState(ChartRenderPaths.GetWorkspaceDirectory(settings))
            };
        }

        private static Dictionary<string, object> DriveState(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                DriveInfo drive = new DriveInfo(root);
                return new Dictionary<string, object>
                {
                    ["drive"] = root.TrimEnd(Path.DirectorySeparatorChar),
                    ["freeBytes"] = drive.IsReady ? drive.AvailableFreeSpace : -1L
                };
            }
            catch
            {
                return new Dictionary<string, object> { ["drive"] = string.Empty, ["freeBytes"] = -1L };
            }
        }

        // The read-only link gets progress and a settings summary, but no addresses, tokens, keys or
        // local paths.
        public static Dictionary<string, object> ForViewer(Dictionary<string, object> state)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(state);
            copy.Remove("workspace");
            copy.Remove("disk");
            copy.Remove("ffmpeg");
            copy.Remove("update");
            if (state.TryGetValue("server", out object server) && server is Dictionary<string, object> serverState)
            {
                Dictionary<string, object> s2 = new Dictionary<string, object>(serverState);
                s2.Remove("lanUrl");
                s2.Remove("lanUrls");
                s2.Remove("lanViewUrls");
                copy["server"] = s2;
            }

            if (state.TryGetValue("settings", out object settings) && settings is Dictionary<string, object> settingsState)
            {
                Dictionary<string, object> s2 = new Dictionary<string, object>(settingsState);
                s2.Remove("ChartRenderWorkspaceDirectory");
                s2.Remove("ChartRenderExportDirectory");
                s2.Remove("FfmpegCustomPath");
                s2.Remove("ChartRenderCustomMuxArgs");
                copy["settings"] = s2;
            }

            if (state.TryGetValue("render", out object render) && render is Dictionary<string, object> renderState)
            {
                Dictionary<string, object> r2 = new Dictionary<string, object>(renderState);
                r2["outputPath"] = Path.GetFileName(renderState.TryGetValue("outputPath", out object p) ? p as string ?? string.Empty : string.Empty);
                // Renderer and FFmpeg messages can contain local paths, including before the task
                // reaches its terminal state. Use only fixed text for the viewer.
                string summary = (renderState["state"] as string) switch
                {
                    nameof(ChartRenderTaskState.Completed) => "视频已成功导出。",
                    nameof(ChartRenderTaskState.Failed) => "渲染任务失败。",
                    nameof(ChartRenderTaskState.Canceled) => "渲染已取消。",
                    _ => string.Empty,
                };
                r2["message"] = summary;
                if (renderState.TryGetValue("progress", out object progress)
                    && progress is Dictionary<string, object> progressState)
                {
                    r2["progress"] = new Dictionary<string, object>(progressState)
                    {
                        ["detail"] = string.IsNullOrEmpty(summary)
                            ? renderState["active"] is true ? "正在处理渲染任务。" : "等待开始渲染。"
                            : summary,
                    };
                }

                copy["render"] = r2;
            }

            if (state.TryGetValue("lastResult", out object last) && last is Dictionary<string, object> lastState)
            {
                Dictionary<string, object> l2 = new Dictionary<string, object>(lastState);
                l2["path"] = Path.GetFileName(lastState.TryGetValue("path", out object lp) ? lp as string ?? string.Empty : string.Empty);
                copy["lastResult"] = l2;
            }

            copy["viewer"] = true;
            return copy;
        }

        private static Dictionary<string, object> BuildSettings(Settings settings)
        {
            return new Dictionary<string, object>
            {
                ["WebUiOpenHotkey"] = settings.WebUiOpenHotkey,
                ["WebUiOpenMode"] = settings.WebUiOpenMode ?? string.Empty,
                ["WebUiAllowLan"] = settings.WebUiAllowLan,
                ["ChartRenderWorkspaceDirectory"] = settings.ChartRenderWorkspaceDirectory ?? string.Empty,
                ["ChartRenderExportDirectory"] = settings.ChartRenderExportDirectory ?? string.Empty,
                ["ChartRenderWidth"] = settings.ChartRenderWidth,
                ["ChartRenderHeight"] = settings.ChartRenderHeight,
                ["ChartRenderFps"] = settings.ChartRenderFps,
                ["ChartRenderCrf"] = settings.ChartRenderCrf,
                ["ChartRenderQualityTier"] = settings.ChartRenderQualityTier ?? string.Empty,
                ["ChartRenderDiagnosticMode"] = settings.ChartRenderDiagnosticMode,
                ["WebUiLanPort"] = settings.WebUiLanPort,
                ["ChartRenderBitrateMbps"] = settings.ChartRenderBitrateMbps,
                ["ChartRenderConstantBitrate"] = settings.ChartRenderConstantBitrate,
                ["ChartRenderPreset"] = settings.ChartRenderPreset ?? string.Empty,
                ["ChartRenderEncoderMode"] = settings.ChartRenderEncoderMode ?? string.Empty,
                ["ChartRenderHardwareEncoder"] = settings.ChartRenderHardwareEncoder ?? string.Empty,
                ["ChartRenderHardwarePreset"] = settings.ChartRenderHardwarePreset ?? string.Empty,
                ["ChartRenderCpuPreset"] = settings.ChartRenderCpuPreset ?? string.Empty,
                ["ChartRenderDetailedEncoderSettings"] = settings.ChartRenderDetailedEncoderSettings,
                ["ChartRenderCaptureFormat"] = settings.ChartRenderCaptureFormat ?? string.Empty,
                ["ChartRenderCaptureSource"] = settings.ChartRenderCaptureSource ?? string.Empty,
                ["ChartRenderGameViewBackend"] = settings.ChartRenderGameViewBackend ?? string.Empty,
                ["ChartRenderPreviewMode"] = settings.ChartRenderPreviewMode ?? string.Empty,
                ["ChartRenderAudioFormat"] = settings.ChartRenderAudioFormat ?? string.Empty,
                ["ChartRenderVideoFormat"] = settings.ChartRenderVideoFormat ?? string.Empty,
                ["ChartRenderVideoCodec"] = settings.ChartRenderVideoCodec ?? string.Empty,
                ["ChartRenderCompletionTailSeconds"] = settings.ChartRenderCompletionTailSeconds,
                ["ChartRenderAudioSyncOffsetMs"] = settings.ChartRenderAudioSyncOffsetMs,
                ["ChartRenderShowHitJudgments"] = settings.ChartRenderShowHitJudgments,
                ["ChartRenderCompatKeyViewers"] = settings.ChartRenderCompatKeyViewers,
                ["ChartRenderUseSelectedRange"] = settings.ChartRenderUseSelectedRange,
                ["ChartRenderCustomMuxArgs"] = settings.ChartRenderCustomMuxArgs ?? string.Empty,
                ["FfmpegCustomPath"] = settings.FfmpegCustomPath ?? string.Empty
            };
        }

        private static Dictionary<string, object> BuildRenderState(ChartRenderTask? task)
        {
            ChartRenderProgress progress = task?.Progress ?? ChartRenderProgress.Empty();
            ChartRenderResult? result = task?.Result;
            string resultMessage = result?.Message ?? string.Empty;
            string message = resultMessage;
            float value = progress.Value;
            string stage = progress.Stage;
            string detail = progress.Detail;
            if (task == null)
            {
                stage = "等待渲染";
                detail = "从页面开始渲染后，全部进度会在这里实时更新。";
            }
            else if (task.IsTerminal)
            {
                if (task.State == ChartRenderTaskState.Completed)
                {
                    stage = "渲染完成";
                    detail = "视频已成功导出。";
                    message = "渲染完成，视频已导出。";
                    value = 1f;
                }
                else if (task.State == ChartRenderTaskState.Failed)
                {
                    stage = "渲染失败";
                    detail = string.IsNullOrWhiteSpace(resultMessage) ? "渲染任务失败。" : resultMessage;
                    message = detail;
                }
                else if (task.State == ChartRenderTaskState.Canceled)
                {
                    stage = "渲染已取消";
                    detail = string.IsNullOrWhiteSpace(resultMessage) ? "渲染任务已取消。" : resultMessage;
                    message = "渲染已取消。";
                }
            }

            return new Dictionary<string, object>
            {
                ["active"] = task != null && !task.IsTerminal,
                ["taskId"] = task?.Id.ToString() ?? string.Empty,
                ["state"] = task?.State.ToString() ?? "Idle",
                ["cancelRequested"] = task != null && task.IsCancellationRequested,
                // Frames are done but FFmpeg is still writing the file: closing the game now loses it.
                ["writingOutput"] = task != null && task.State == ChartRenderTaskState.Finalizing,
                // The game refused a close request in the last few seconds (ChartRenderQuitGuard).
                ["quitRefused"] = task != null && !task.IsTerminal
                    && UnityEngine.Time.realtimeSinceStartup - Features.ChartRendering.ChartRenderQuitGuard.LastRefusedAt <= 10f,
                ["captureSource"] = task?.CaptureSource.ToString() ?? string.Empty,
                // Bytes of raw video written so far; with the frame counts this gives a live size
                // projection that is far more accurate than any estimate made before rendering.
                ["tempVideoBytes"] = task != null && !task.IsTerminal ? FileLength(ChartRenderSession.CurrentTempVideoPath) : 0L,
                ["outputPath"] = task?.OutputPath ?? string.Empty,
                ["message"] = message,
                ["progress"] = new Dictionary<string, object>
                {
                    ["value"] = value,
                    ["writtenFrames"] = progress.WrittenFrames,
                    ["totalFrames"] = progress.TotalFrames,
                    ["duplicateFrames"] = progress.DuplicateFrames,
                    ["duplicateRatio"] = progress.DuplicateRatio,
                    ["processingFramesPerSecond"] = progress.ProcessingFramesPerSecond,
                    ["estimatedRemaining"] = progress.EstimatedRemaining.ToString(),
                    ["stage"] = stage,
                    ["detail"] = detail,
                    ["encoderName"] = progress.EncoderName,
                    ["memoryBudget"] = progress.MemoryBudget,
                    ["queueBudget"] = progress.QueueBudget
                }
            };
        }

        private static long FileLength(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? 0L : new FileInfo(path).Length;
            }
            catch
            {
                return 0L;
            }
        }

        private static string GetPatchState(PatchGroupState state)
        {
            switch (state)
            {
                case PatchGroupState.Active: return "active";
                case PatchGroupState.Failed: return "failed";
                case PatchGroupState.Blocked: return "blocked";
                default: return "inactive";
            }
        }
    }
}
