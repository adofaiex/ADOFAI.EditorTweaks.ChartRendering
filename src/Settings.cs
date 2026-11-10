using System;
using System.IO;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;
using ADOFAI.EditorTweaks.ChartRendering.Features.WebUi;
using UnityModManagerNet;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering
{
    public class Settings : UnityModManager.ModSettings
    {
        private const int MinSize = 16;
        private const int MaxWidth = 7680;
        private const int MaxHeight = 4320;
        private const int MaxFps = 240;

        public string WebUiOpenHotkey = WebUiHotkey.Default;
        // Optional ffmpeg.exe to use instead of the bundled one. Empty = bundled.
        public string FfmpegCustomPath = string.Empty;
        public string ChartRenderWorkspaceDirectory = string.Empty;
        public string ChartRenderExportDirectory = string.Empty;
        public int ChartRenderWidth = 1920;
        public int ChartRenderHeight = 1080;
        public int ChartRenderFps = 60;
        public int ChartRenderCrf = 18;
        public int ChartRenderBitrateMbps = ChartRenderBitratePresets.AutoBitrateMbps;
        // Target the configured bitrate instead of constant quality. Default false keeps CRF/VBR.
        public bool ChartRenderConstantBitrate;
        public string ChartRenderPreset = "veryfast";
        public string ChartRenderEncoderMode = ChartRenderOptionValues.EncoderAutoBalanced;
        public string ChartRenderHardwarePreset = ChartRenderOptionValues.PresetFollowEncoderMode;
        public string ChartRenderCpuPreset = ChartRenderOptionValues.PresetFollowEncoderMode;
        // Settings page shows the detailed encoder settings (encoder + preset) instead of the simple
        // encoder mode. The two presets above are only used while this is on.
        public bool ChartRenderDetailedEncoderSettings;
        // Encoder detection results (ChartEncoderCapabilities), not a preference: kept by "reset to
        // defaults", and re-detected automatically when the GPU or FFmpeg changes.
        public string EncoderCapabilitiesCache = string.Empty;
        public string ChartRenderCaptureFormat = ChartRenderOptionValues.CaptureRgba;
        public string ChartRenderCaptureSource = ChartRenderOptionValues.CaptureSourceCamera;
        public string ChartRenderPreviewMode = ChartRenderOptionValues.PreviewFull;
        public string ChartRenderAudioFormat = ChartRenderOptionValues.AudioFormatAac;
        public string ChartRenderVideoFormat = ChartRenderOptionValues.VideoFormatMp4;
        public string ChartRenderVideoCodec = ChartRenderOptionValues.VideoCodecH264;
        public float ChartRenderCompletionTailSeconds = 5f;
        public float ChartRenderAudioSyncOffsetMs;
        public bool ChartRenderShowHitJudgments = true;
        public bool ChartRenderUseSelectedRange;
        public bool ChartRenderAdvancedSettingsExpanded;
        public bool ChartRenderProfessionalSettingsExpanded;
        public string ChartRenderCustomMuxArgs = string.Empty;

        public void OnGUI(UnityModManager.ModEntry modEntry) { WebUiSettingsView.Draw(this); }
        public void OnSaveGUI(UnityModManager.ModEntry modEntry) { Save(modEntry); }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Normalize();
            Save(this, modEntry);
        }

        public void EnsureDefaults(UnityModManager.ModEntry modEntry)
        {
            WebUiOpenHotkey = WebUiHotkey.NormalizeOrDefault(WebUiOpenHotkey);
            if (string.IsNullOrWhiteSpace(ChartRenderWorkspaceDirectory))
                ChartRenderWorkspaceDirectory = Path.Combine(modEntry.Path, "Workspace");
            if (string.IsNullOrWhiteSpace(ChartRenderExportDirectory))
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                ChartRenderExportDirectory = string.IsNullOrWhiteSpace(videos)
                    ? Path.Combine(ChartRenderWorkspaceDirectory, "Exports")
                    : Path.Combine(videos, "ADOFAI Renders");
            }
            Normalize();
        }

        public void Normalize()
        {
            WebUiOpenHotkey = WebUiHotkey.NormalizeOrDefault(WebUiOpenHotkey);
            ChartRenderWidth = MakeEven(Mathf.Clamp(ChartRenderWidth, MinSize, MaxWidth));
            ChartRenderHeight = MakeEven(Mathf.Clamp(ChartRenderHeight, MinSize, MaxHeight));
            ChartRenderFps = Mathf.Clamp(ChartRenderFps, 1, MaxFps);
            ChartRenderCrf = Mathf.Clamp(ChartRenderCrf, 0, 51);
            ChartRenderBitrateMbps = Mathf.Clamp(ChartRenderBitrateMbps, ChartRenderBitratePresets.AutoBitrateMbps, ChartRenderBitratePresets.MaxBitrateMbps);
            ChartRenderPreset = string.IsNullOrWhiteSpace(ChartRenderPreset) ? "veryfast" : ChartRenderPreset.Trim();
            ChartRenderEncoderMode = ChartRenderOptionValues.NormalizeEncoderMode(ChartRenderEncoderMode);
            ChartRenderHardwarePreset = ChartRenderOptionValues.NormalizeHardwarePreset(ChartRenderHardwarePreset);
            ChartRenderCpuPreset = ChartRenderOptionValues.NormalizeCpuPreset(ChartRenderCpuPreset);
            // The custom encoder mode only exists in the detailed encoder view; settings saved before
            // that view existed keep their custom mode by opening it.
            if (ChartRenderEncoderMode == ChartRenderOptionValues.EncoderCustom)
            {
                ChartRenderDetailedEncoderSettings = true;
            }
            ChartRenderCaptureFormat = ChartRenderOptionValues.NormalizeCaptureFormat(ChartRenderCaptureFormat);
            ChartRenderCaptureSource = ChartRenderOptionValues.NormalizeCaptureSource(ChartRenderCaptureSource);
            ChartRenderPreviewMode = ChartRenderOptionValues.NormalizePreviewMode(ChartRenderPreviewMode);
            ChartRenderAudioFormat = ChartRenderOptionValues.NormalizeAudioFormat(ChartRenderAudioFormat);
            ChartRenderVideoFormat = ChartRenderOptionValues.NormalizeVideoFormat(ChartRenderVideoFormat);
            ChartRenderVideoCodec = ChartRenderOptionValues.NormalizeVideoCodec(ChartRenderVideoCodec);
            ChartRenderCompletionTailSeconds = Mathf.Max(0f, ChartRenderCompletionTailSeconds);
            ChartRenderAudioSyncOffsetMs = Mathf.Clamp(ChartRenderAudioSyncOffsetMs, -5000f, 5000f);
            ChartRenderWorkspaceDirectory = ChartRenderWorkspaceDirectory ?? string.Empty;
            ChartRenderExportDirectory = ChartRenderExportDirectory ?? string.Empty;
            ChartRenderCustomMuxArgs = ChartRenderCustomMuxArgs ?? string.Empty;
            FfmpegCustomPath = (FfmpegCustomPath ?? string.Empty).Trim().Trim('"');
            EncoderCapabilitiesCache = EncoderCapabilitiesCache ?? string.Empty;
        }

        public void ResetAllDefaults(UnityModManager.ModEntry modEntry)
        {
            WebUiOpenHotkey = WebUiHotkey.Default;
            FfmpegCustomPath = string.Empty;
            ChartRenderWorkspaceDirectory = Path.Combine(modEntry.Path, "Workspace");
            ChartRenderExportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ADOFAI Renders");
            ChartRenderWidth = 1920;
            ChartRenderHeight = 1080;
            ChartRenderFps = 60;
            ChartRenderCrf = 18;
            ChartRenderBitrateMbps = ChartRenderBitratePresets.AutoBitrateMbps;
            ChartRenderConstantBitrate = false;
            ChartRenderPreset = "veryfast";
            ChartRenderEncoderMode = ChartRenderOptionValues.EncoderAutoBalanced;
            ChartRenderHardwarePreset = ChartRenderOptionValues.PresetFollowEncoderMode;
            ChartRenderCpuPreset = ChartRenderOptionValues.PresetFollowEncoderMode;
            ChartRenderDetailedEncoderSettings = false;
            ChartRenderCaptureFormat = ChartRenderOptionValues.CaptureRgba;
            ChartRenderCaptureSource = ChartRenderOptionValues.CaptureSourceCamera;
            ChartRenderPreviewMode = ChartRenderOptionValues.PreviewFull;
            ChartRenderAudioFormat = ChartRenderOptionValues.AudioFormatAac;
            ChartRenderVideoFormat = ChartRenderOptionValues.VideoFormatMp4;
            ChartRenderVideoCodec = ChartRenderOptionValues.VideoCodecH264;
            ChartRenderCompletionTailSeconds = 5f;
            ChartRenderAudioSyncOffsetMs = 0f;
            ChartRenderShowHitJudgments = true;
            ChartRenderUseSelectedRange = false;
            ChartRenderAdvancedSettingsExpanded = false;
            ChartRenderProfessionalSettingsExpanded = false;
            ChartRenderCustomMuxArgs = string.Empty;
            Normalize();
        }

        public static string Text(string key) { return Localization.Text(key); }
        public static Settings Load(UnityModManager.ModEntry modEntry) { return Load<Settings>(modEntry); }
        private static int MakeEven(int value) { return value % 2 == 0 ? value : value + 1; }
    }
}
