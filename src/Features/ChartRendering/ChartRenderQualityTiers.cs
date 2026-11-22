using System;
using System.Collections.Generic;
using System.Globalization;
using ADOFAI.EditorTweaks.Api.Rendering;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Quality tiers for the simple encoder view: a quality target with a bitrate cap (what OBS
    // calls CQVBR). The quality value keeps easy scenes small; the cap stops particle-heavy scenes
    // from blowing the file up. Without a cap, 1.1.0/1.1.1 produced 280–420 Mbps on a noisy 1080p60
    // test clip (CRF 18 / constant QP); with a 20 Mbps cap the same clip came out at about 21 Mbps.
    //
    // Caps are based on YouTube's recommended upload bitrates (SDR, H.264) and stay above Bilibili's
    // minimums (1080p > 6 Mbps, 4K > 20 Mbps, 8K > 40 Mbps):
    //   small = 1x, standard = 2x (platforms re-encode, so uploads keep headroom), high = 4x,
    //   lossless = no cap. HEVC/AV1/VVC need less bitrate for the same quality (x0.7/0.6/0.5).
    // The quality values per encoder family are a first draft and still need checking on real charts.
    internal static class ChartRenderQualityTiers
    {
        public const string Custom = "custom";
        public const string Small = "small";
        public const string Standard = "standard";
        public const string High = "high";
        public const string Lossless = "lossless";

        public static readonly string[] Tiers = { Small, Standard, High, Lossless };

        // YouTube SDR recommendations in Mbps: { max pixels, <= 30 fps, 48–60 fps }.
        private static readonly double[][] PlatformTable =
        {
            new double[] { 640 * 360, 1, 1.5 },
            new double[] { 854 * 480, 2.5, 4 },
            new double[] { 1280 * 720, 5, 7.5 },
            new double[] { 1920 * 1080, 8, 12 },
            new double[] { 2560 * 1440, 16, 24 },
            new double[] { 3840 * 2160, 40, 60 },
            new double[] { double.MaxValue, 120, 180 },
        };

        // Quality value per encoder family for small / standard / high / lossless.
        private static readonly Dictionary<string, int[]> QualityTable = new Dictionary<string, int[]>
        {
            ["nvenc"] = new[] { 28, 25, 21, 16 },      // -cq (H.264/HEVC)
            ["nvenc_av1"] = new[] { 40, 34, 28, 20 },  // -cq 0-63
            ["amf"] = new[] { 28, 25, 21, 16 },        // QVBR quality level
            ["qsv"] = new[] { 28, 25, 21, 16 },        // global_quality
            ["x264"] = new[] { 26, 23, 20, 16 },       // CRF
            ["x265"] = new[] { 28, 25, 22, 18 },       // CRF
            ["svtav1"] = new[] { 40, 34, 28, 20 },     // CRF 1-63
            ["aom"] = new[] { 40, 34, 28, 20 },        // CRF 0-63
            ["vvenc"] = new[] { 34, 30, 27, 23 },      // QP
        };

        // Encoder speed a tier uses while the speed setting is left on "follow": small files favour
        // render speed, archive / editing tiers favour compression. Measured with the bundled FFmpeg
        // on a detailed 1080p60 clip under a tight cap (RTX, NVENC H.264): p1, p4, p6 and p7 encode
        // at about the same speed (98 / 101 / 97 / 91 fps), but p1 overshoots the cap by ~20% at
        // lower quality, so small stops at p4. x264 veryfast -> faster costs about 17% speed and
        // fast about 45%, so the CPU side only moves one step.
        public static int HardwarePresetRank(string tier)
        {
            switch (Normalize(tier))
            {
                case High:
                case Lossless:
                    return 6;
                case Standard:
                    return 5;
                default:
                    return 4;
            }
        }

        public static string CpuPreset(string tier)
        {
            switch (Normalize(tier))
            {
                case High:
                case Lossless:
                    return "faster";
                default:
                    return "veryfast";
            }
        }

        public static string Normalize(string? value)
        {
            string v = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (v)
            {
                case Custom:
                case Small:
                case Standard:
                case High:
                case Lossless:
                    return v;
                default:
                    return Standard;
            }
        }

        public static ChartRenderQualityTier Parse(string? value)
        {
            switch (Normalize(value))
            {
                case Small: return ChartRenderQualityTier.Small;
                case High: return ChartRenderQualityTier.High;
                case Lossless: return ChartRenderQualityTier.NearLossless;
                case Custom: return ChartRenderQualityTier.Custom;
                default: return ChartRenderQualityTier.Standard;
            }
        }

        public static string Format(ChartRenderQualityTier value)
        {
            switch (value)
            {
                case ChartRenderQualityTier.Small: return Small;
                case ChartRenderQualityTier.Standard: return Standard;
                case ChartRenderQualityTier.High: return High;
                case ChartRenderQualityTier.NearLossless: return Lossless;
                default: return Custom;
            }
        }

        private static int Index(string tier)
        {
            switch (tier)
            {
                case Small: return 0;
                case High: return 2;
                case Lossless: return 3;
                default: return 1;
            }
        }

        public static double PlatformBitrateMbps(int width, int height, int fps)
        {
            double pixels = (double)Math.Max(1, width) * Math.Max(1, height);
            double[] row = PlatformTable[PlatformTable.Length - 1];
            foreach (double[] candidate in PlatformTable)
            {
                if (pixels <= candidate[0])
                {
                    row = candidate;
                    break;
                }
            }

            double rate = fps <= 30 ? row[1] : row[2];
            if (fps > 60)
            {
                // YouTube's table stops at 60 fps; scale higher frame rates by the square root.
                rate *= Math.Sqrt(fps / 60.0);
            }

            return rate;
        }

        private static double CodecFactor(string codec)
        {
            switch (codec)
            {
                case ChartRenderOptionValues.VideoCodecHevc: return 0.7;
                case ChartRenderOptionValues.VideoCodecAv1: return 0.6;
                case ChartRenderOptionValues.VideoCodecVvc: return 0.5;
                default: return 1.0;
            }
        }

        // Bitrate cap in Mbps, or 0 for no cap (lossless / custom).
        public static int CapMbps(string tier, string codec, int width, int height, int fps)
        {
            tier = Normalize(tier);
            if (tier == Custom || tier == Lossless)
            {
                return 0;
            }

            double multiplier = tier == Small ? 1.0 : tier == High ? 4.0 : 2.0;
            double cap = PlatformBitrateMbps(width, height, fps) * multiplier * CodecFactor(ChartRenderOptionValues.NormalizeVideoCodec(codec));
            return Math.Max(2, Math.Min(ChartRenderBitratePresets.MaxBitrateMbps, (int)Math.Round(cap)));
        }

        public static int Quality(string tier, string family)
        {
            return QualityTable.TryGetValue(family, out int[] values) ? values[Index(Normalize(tier))] : 23;
        }

        // Sent to the settings page so it shows exactly the values the encoder will use.
        public static Dictionary<string, object> BuildState(Settings settings)
        {
            Dictionary<string, object> quality = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int[]> family in QualityTable)
            {
                quality[family.Key] = new List<object> { family.Value[0], family.Value[1], family.Value[2], family.Value[3] };
            }

            Dictionary<string, object> caps = new Dictionary<string, object>();
            foreach (string tier in Tiers)
            {
                caps[tier] = CapMbps(tier, settings.ChartRenderVideoCodec, settings.ChartRenderWidth, settings.ChartRenderHeight, settings.ChartRenderFps);
            }

            return new Dictionary<string, object>
            {
                ["quality"] = quality,
                ["caps"] = caps,
                ["platformMbps"] = Math.Round(PlatformBitrateMbps(settings.ChartRenderWidth, settings.ChartRenderHeight, settings.ChartRenderFps), 1),
                ["learned"] = ParseStats(settings.ChartRenderSizeStats),
            };
        }

        // --- Learned size ratios -------------------------------------------------------------
        // After a capped render finishes, "average video bitrate / cap" is remembered per codec|tier,
        // so the next size estimate uses what this user's charts actually produced.

        public static Dictionary<string, object> ParseStats(string? stats)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (string entry in (stats ?? string.Empty).Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                if (double.TryParse(entry.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio)
                    && ratio > 0 && ratio < 5)
                {
                    result[entry.Substring(0, eq)] = Math.Round(ratio, 3);
                }
            }

            return result;
        }

        public static string RecordRatio(string? stats, string codec, string tier, double ratio)
        {
            Dictionary<string, object> parsed = ParseStats(stats);
            string key = ChartRenderOptionValues.NormalizeVideoCodec(codec) + "|" + Normalize(tier);
            // Blend with the previous value so one unusual chart does not swing the estimate.
            if (parsed.TryGetValue(key, out object previous) && previous is double old)
            {
                ratio = old * 0.5 + ratio * 0.5;
            }

            parsed[key] = Math.Round(ratio, 3);
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in parsed)
            {
                parts.Add(pair.Key + "=" + Convert.ToDouble(pair.Value).ToString("0.###", CultureInfo.InvariantCulture));
            }

            return string.Join(";", parts.ToArray());
        }
    }
}
