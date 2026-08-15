using System;
using System.Collections.Generic;
using ADOFAI.EditorTweaks.Api.Rendering;
using ADOFAI.EditorTweaks.ChartRendering.Patching;
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
                patches.Add(new Dictionary<string, object>
                {
                    ["id"] = status.Feature.ToString(),
                    ["name"] = status.Name,
                    ["description"] = status.Feature == PatchFeature.RenderInputGuard ? "渲染期间屏蔽输入干扰" : "谱面画面、音频和视频背景渲染",
                    ["state"] = GetPatchState(status.State),
                    ["patchCount"] = status.PatchCount,
                    ["reason"] = status.Reason ?? string.Empty
                });
            }

            return new Dictionary<string, object>
            {
                ["server"] = new Dictionary<string, object>
                {
                    ["connected"] = true,
                    ["version"] = Main.Mod?.Info?.Version ?? "0.0.0"
                },
                ["compatibility"] = new Dictionary<string, object>
                {
                    ["gameVersion"] = Application.version ?? string.Empty,
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
                ["render"] = BuildRenderState(renderTask)
            };
        }

        private static Dictionary<string, object> BuildSettings(Settings settings)
        {
            return new Dictionary<string, object>
            {
                ["WebUiOpenHotkey"] = settings.WebUiOpenHotkey,
                ["ChartRenderWorkspaceDirectory"] = settings.ChartRenderWorkspaceDirectory ?? string.Empty,
                ["ChartRenderExportDirectory"] = settings.ChartRenderExportDirectory ?? string.Empty,
                ["ChartRenderWidth"] = settings.ChartRenderWidth,
                ["ChartRenderHeight"] = settings.ChartRenderHeight,
                ["ChartRenderFps"] = settings.ChartRenderFps,
                ["ChartRenderCrf"] = settings.ChartRenderCrf,
                ["ChartRenderBitrateMbps"] = settings.ChartRenderBitrateMbps,
                ["ChartRenderPreset"] = settings.ChartRenderPreset ?? string.Empty,
                ["ChartRenderEncoderMode"] = settings.ChartRenderEncoderMode ?? string.Empty,
                ["ChartRenderCaptureFormat"] = settings.ChartRenderCaptureFormat ?? string.Empty,
                ["ChartRenderCaptureSource"] = settings.ChartRenderCaptureSource ?? string.Empty,
                ["ChartRenderPreviewMode"] = settings.ChartRenderPreviewMode ?? string.Empty,
                ["ChartRenderAudioFormat"] = settings.ChartRenderAudioFormat ?? string.Empty,
                ["ChartRenderVideoFormat"] = settings.ChartRenderVideoFormat ?? string.Empty,
                ["ChartRenderCompletionTailSeconds"] = settings.ChartRenderCompletionTailSeconds,
                ["ChartRenderAudioSyncOffsetMs"] = settings.ChartRenderAudioSyncOffsetMs,
                ["ChartRenderShowHitJudgments"] = settings.ChartRenderShowHitJudgments,
                ["ChartRenderUseSelectedRange"] = settings.ChartRenderUseSelectedRange,
                ["ChartRenderCustomMuxArgs"] = settings.ChartRenderCustomMuxArgs ?? string.Empty
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
                ["captureSource"] = task?.CaptureSource.ToString() ?? string.Empty,
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
