using System;
using ADOFAI.EditorTweaks.Api.Rendering;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    internal static class ChartRenderOptionValues
    {
        public const string EncoderAutoBalanced = "auto-balanced";
        public const string EncoderFastest = "fastest";
        public const string EncoderBalanced = "balanced";
        public const string EncoderQuality = "quality";
        public const string EncoderCpuCompatibility = "cpu-compatibility";
        public const string EncoderCustom = "custom";

        public const string CaptureRgba = "rgba";
        public const string CaptureBgra = "bgra";

        public const string CaptureSourceCamera = "camera";
        public const string CaptureSourceGameView = "game-view";

        public const string VideoFormatMp4 = "mp4";
        public const string VideoFormatMkv = "mkv";
        public const string VideoFormatMov = "mov";

        public const string VideoCodecH264 = "h264";
        public const string VideoCodecHevc = "hevc";
        public const string VideoCodecAv1 = "av1";
        public const string VideoCodecVvc = "vvc";
        public const string VideoCodecProRes = "prores";

        // "auto" = follow the encoder mode (the behavior before these presets existed).
        public const string PresetFollowEncoderMode = "auto";

        public const string HardwareEncoderAuto = "auto";
        public const string HardwareEncoderNvidia = "nvidia";
        public const string HardwareEncoderIntel = "intel";
        public const string HardwareEncoderAmd = "amd";

        public static readonly string[] HardwareEncoders =
        {
            HardwareEncoderAuto,
            HardwareEncoderNvidia,
            HardwareEncoderIntel,
            HardwareEncoderAmd
        };

        public static readonly string[] HardwarePresets = { PresetFollowEncoderMode, "p1", "p2", "p3", "p4", "p5", "p6", "p7" };

        // Same order as ChartRenderCpuPreset, fastest first.
        public static readonly string[] CpuPresets =
        {
            PresetFollowEncoderMode, "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"
        };

        public static string NormalizeHardwareEncoder(string? value)
        {
            string lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
            return Array.IndexOf(HardwareEncoders, lowered) >= 0 ? lowered : HardwareEncoderAuto;
        }

        public static ChartRenderHardwareEncoder ParseHardwareEncoder(string? value)
        {
            return (ChartRenderHardwareEncoder)Math.Max(0, Array.IndexOf(HardwareEncoders, NormalizeHardwareEncoder(value)));
        }

        public static string FormatHardwareEncoder(ChartRenderHardwareEncoder encoder)
        {
            int index = (int)encoder;
            return index >= 0 && index < HardwareEncoders.Length ? HardwareEncoders[index] : HardwareEncoderAuto;
        }

        public static string NormalizeHardwarePreset(string? value)
        {
            string lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
            return Array.IndexOf(HardwarePresets, lowered) > 0 ? lowered : PresetFollowEncoderMode;
        }

        public static string NormalizeCpuPreset(string? value)
        {
            string lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
            return Array.IndexOf(CpuPresets, lowered) > 0 ? lowered : PresetFollowEncoderMode;
        }

        public static ChartRenderHardwarePreset ParseHardwarePreset(string? value)
        {
            return (ChartRenderHardwarePreset)Math.Max(0, Array.IndexOf(HardwarePresets, NormalizeHardwarePreset(value)));
        }

        public static ChartRenderCpuPreset ParseCpuPreset(string? value)
        {
            return (ChartRenderCpuPreset)Math.Max(0, Array.IndexOf(CpuPresets, NormalizeCpuPreset(value)));
        }

        public static string FormatHardwarePreset(ChartRenderHardwarePreset preset)
        {
            int index = (int)preset;
            return index > 0 && index < HardwarePresets.Length ? HardwarePresets[index] : PresetFollowEncoderMode;
        }

        public static string FormatCpuPreset(ChartRenderCpuPreset preset)
        {
            int index = (int)preset;
            return index > 0 && index < CpuPresets.Length ? CpuPresets[index] : PresetFollowEncoderMode;
        }

        public const string AudioFormatAac = "aac";
        public const string AudioFormatFlac = "flac";
        public const string AudioFormatAlac = "alac";

        public const string PreviewFull = "full";
        public const string PreviewDim = "dim";
        public const string PreviewMinimal = "minimal";

        public static readonly string[] EncoderModes =
        {
            EncoderAutoBalanced,
            EncoderFastest,
            EncoderBalanced,
            EncoderQuality,
            EncoderCpuCompatibility,
            EncoderCustom
        };

        public static readonly string[] CaptureFormats =
        {
            CaptureRgba,
            CaptureBgra
        };

        public static readonly string[] CaptureSources =
        {
            CaptureSourceCamera,
            CaptureSourceGameView
        };

        public static readonly string[] AudioFormats =
        {
            AudioFormatAac,
            AudioFormatFlac,
            AudioFormatAlac
        };

        public static readonly string[] VideoFormats =
        {
            VideoFormatMp4,
            VideoFormatMkv,
            VideoFormatMov
        };

        public static readonly string[] VideoCodecs =
        {
            VideoCodecH264,
            VideoCodecHevc,
            VideoCodecAv1,
            VideoCodecVvc,
            VideoCodecProRes
        };

        public static readonly string[] PreviewModes =
        {
            PreviewFull,
            PreviewDim,
            PreviewMinimal
        };

        public static string NormalizeEncoderMode(string? value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case EncoderFastest:
                    return EncoderFastest;
                case EncoderBalanced:
                    return EncoderBalanced;
                case EncoderQuality:
                    return EncoderQuality;
                case EncoderCpuCompatibility:
                    return EncoderCpuCompatibility;
                case EncoderCustom:
                    return EncoderCustom;
                default:
                    return EncoderAutoBalanced;
            }
        }

        public static string NormalizeCaptureFormat(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant() == CaptureBgra
                ? CaptureBgra
                : CaptureRgba;
        }

        public static string NormalizeCaptureSource(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant() == CaptureSourceGameView
                ? CaptureSourceGameView
                : CaptureSourceCamera;
        }

        public static string NormalizePreviewMode(string? value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case PreviewDim:
                    return PreviewDim;
                case PreviewMinimal:
                    return PreviewMinimal;
                default:
                    return PreviewFull;
            }
        }

        public static string NormalizeAudioFormat(string? value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case AudioFormatFlac:
                    return AudioFormatFlac;
                case AudioFormatAlac:
                    return AudioFormatAlac;
                default:
                    return AudioFormatAac;
            }
        }

        public static string NormalizeVideoFormat(string? value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case VideoFormatMkv:
                    return VideoFormatMkv;
                case VideoFormatMov:
                    return VideoFormatMov;
                default:
                    return VideoFormatMp4;
            }
        }

        public static string GetVideoFormatExtension(string format)
        {
            return "." + NormalizeVideoFormat(format);
        }

        // ProRes is only written to .mov; every other codec follows the chosen container.
        public static string GetOutputExtension(string format, string codec)
        {
            return NormalizeVideoCodec(codec) == VideoCodecProRes
                ? "." + VideoFormatMov
                : GetVideoFormatExtension(format);
        }

        public static string NormalizeVideoCodec(string? value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case VideoCodecHevc:
                case "h265":
                case "x265":
                    return VideoCodecHevc;
                case VideoCodecAv1:
                    return VideoCodecAv1;
                case VideoCodecVvc:
                case "h266":
                case "h.266":
                    return VideoCodecVvc;
                case VideoCodecProRes:
                case "prores422":
                case "prores_ks":
                    return VideoCodecProRes;
                default:
                    return VideoCodecH264;
            }
        }

        public static ChartRenderVideoCodec ParseVideoCodec(string? value)
        {
            switch (NormalizeVideoCodec(value))
            {
                case VideoCodecHevc:
                    return ChartRenderVideoCodec.Hevc;
                case VideoCodecAv1:
                    return ChartRenderVideoCodec.Av1;
                case VideoCodecVvc:
                    return ChartRenderVideoCodec.Vvc;
                case VideoCodecProRes:
                    return ChartRenderVideoCodec.ProRes;
                default:
                    return ChartRenderVideoCodec.H264;
            }
        }

        public static string FormatVideoCodec(ChartRenderVideoCodec codec)
        {
            switch (codec)
            {
                case ChartRenderVideoCodec.Hevc:
                    return VideoCodecHevc;
                case ChartRenderVideoCodec.Av1:
                    return VideoCodecAv1;
                case ChartRenderVideoCodec.Vvc:
                    return VideoCodecVvc;
                case ChartRenderVideoCodec.ProRes:
                    return VideoCodecProRes;
                default:
                    return VideoCodecH264;
            }
        }
    }
}
