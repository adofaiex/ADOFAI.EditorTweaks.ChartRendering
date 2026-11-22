import type { EncoderState, SettingsState, WebUiState } from "./types";

export const TIERS = [
  { id: "small", name: "小文件", use: "发给朋友、手机看" },
  { id: "standard", name: "标准", use: "上传 B 站 / YouTube", recommended: true },
  { id: "high", name: "高质量", use: "自己存档" },
  { id: "lossless", name: "近似无损", use: "导入剪辑软件" },
] as const;

export type TierId = (typeof TIERS)[number]["id"] | "custom";

const TIER_INDEX: Record<string, number> = { small: 0, standard: 1, high: 2, lossless: 3 };
const TIER_MULTIPLIER: Record<string, number> = { small: 1, standard: 2, high: 4 };
const CODEC_FACTOR: Record<string, number> = { h264: 1, hevc: 0.7, av1: 0.6, vvc: 0.5 };

// Same table as ChartRenderQualityTiers.PlatformTable (YouTube SDR recommendations, Mbps).
const PLATFORM: Array<[number, number, number]> = [
  [640 * 360, 1, 1.5], [854 * 480, 2.5, 4], [1280 * 720, 5, 7.5], [1920 * 1080, 8, 12],
  [2560 * 1440, 16, 24], [3840 * 2160, 40, 60], [Infinity, 120, 180],
];

export function platformMbps(width: number, height: number, fps: number): number {
  const row = PLATFORM.find((r) => width * height <= r[0]) ?? PLATFORM[PLATFORM.length - 1];
  let rate = fps <= 30 ? row[1] : row[2];
  if (fps > 60) rate *= Math.sqrt(fps / 60);
  return rate;
}

// Computed locally so the demo page reacts to every change; the mod sends the same numbers.
export function tierCap(tier: string, settings: SettingsState): number {
  const multiplier = TIER_MULTIPLIER[tier];
  if (!multiplier) return 0;
  const codec = settings.ChartRenderVideoCodec.toLowerCase();
  const cap = platformMbps(settings.ChartRenderWidth, settings.ChartRenderHeight, settings.ChartRenderFps) * multiplier * (CODEC_FACTOR[codec] ?? 1);
  return Math.max(2, Math.min(300, Math.round(cap)));
}

// 1.0.3's recommended bitrate (ChartRenderBitratePresets), still used for CBR / custom bitrate 0.
export function recommendedMbps(settings: SettingsState): number {
  const px = settings.ChartRenderWidth * settings.ChartRenderHeight;
  const base = px <= 1280 * 720 ? 8 : px <= 1920 * 1080 ? 20 : px <= 2560 * 1440 ? 35 : px <= 3840 * 2160 ? 60 : 120;
  return Math.max(4, Math.min(300, Math.round(base * Math.sqrt(Math.max(1, settings.ChartRenderFps) / 60))));
}

// Encoder family of an FFmpeg encoder name, for the quality value table.
export function encoderFamily(encoder: string | null): string | null {
  if (!encoder) return null;
  if (encoder === "av1_nvenc") return "nvenc_av1";
  if (encoder.endsWith("_nvenc")) return "nvenc";
  if (encoder.endsWith("_amf")) return "amf";
  if (encoder.endsWith("_qsv")) return "qsv";
  switch (encoder) {
    case "libx264": return "x264";
    case "libx265": return "x265";
    case "libsvtav1": return "svtav1";
    case "libaom-av1": return "aom";
    case "libvvenc": return "vvenc";
    default: return null;
  }
}

export const QUALITY_LABEL: Record<string, string> = {
  nvenc: "CQ", nvenc_av1: "CQ", amf: "QVBR 质量", qsv: "ICQ", x264: "CRF", x265: "CRF", svtav1: "CRF", aom: "CRF", vvenc: "QP",
};

// Encoder speed a tier uses while the speed setting is "auto" (ChartRenderQualityTiers.HardwarePresetRank
// / CpuPreset). Only the balanced / CPU encoder modes follow it.
export function tierSpeed(tier: string): { hardware: string; cpu: string } {
  switch (tier) {
    case "high":
    case "lossless": return { hardware: "p6", cpu: "faster" };
    case "standard": return { hardware: "p5", cpu: "veryfast" };
    default: return { hardware: "p4", cpu: "veryfast" };
  }
}

export function tierQuality(state: WebUiState, family: string | null, tier: string): number | null {
  if (!family) return null;
  const row = state.quality?.quality?.[family];
  const index = TIER_INDEX[tier];
  return row && index !== undefined ? row[index] : null;
}

export function normalizedTier(settings: SettingsState): TierId {
  const tier = (settings.ChartRenderQualityTier || "standard").toLowerCase();
  return (["small", "standard", "high", "lossless", "custom"].includes(tier) ? tier : "standard") as TierId;
}

// Seconds the render will cover: the selection or the whole chart plus the tail, or null if no
// chart is loaded.
export function renderSeconds(state: WebUiState): number | null {
  const chart = state.chart;
  if (!chart?.loaded || chart.seconds <= 0) return null;
  const tail = Math.max(0, state.settings.ChartRenderCompletionTailSeconds);
  if (state.settings.ChartRenderUseSelectedRange && chart.selectionSeconds > 0) return chart.selectionSeconds + tail;
  return chart.seconds + tail;
}

const audioMbps = (settings: SettingsState) => (settings.ChartRenderAudioFormat.toLowerCase() === "aac" ? 0.32 : 0.9);
const proResMbps = (settings: SettingsState) => 220 * (settings.ChartRenderWidth * settings.ChartRenderHeight) / (1920 * 1080) * (settings.ChartRenderFps / 30);

// Assumed share of the cap a render uses before this PC has rendered with that tier. A first guess;
// after each render the mod remembers the real share per codec and tier.
const DEFAULT_RATIO = 0.6;

export interface SizeEstimate {
  // MB; null when unknown or unbounded.
  typical: number | null;
  max: number | null;
  kind: "learned" | "guess" | "unbounded" | "fixed" | "cbr" | "vbr" | "nochart";
}

export function estimateSize(state: WebUiState, tier: TierId = normalizedTier(state.settings)): SizeEstimate {
  const settings = state.settings;
  const seconds = renderSeconds(state);
  if (seconds == null) return { typical: null, max: null, kind: "nochart" };
  const audio = audioMbps(settings);
  const toMb = (mbps: number) => (mbps + audio) * seconds / 8;
  // The legacy custom encoder always uses H.264 and bypasses the quality tier.
  const customEncoder = settings.ChartRenderEncoderMode.toLowerCase() === "custom";
  const codec = customEncoder ? "h264" : settings.ChartRenderVideoCodec.toLowerCase();
  if (codec === "prores") {
    const mb = toMb(proResMbps(settings));
    return { typical: mb, max: mb * 1.05, kind: "fixed" };
  }

  if (settings.ChartRenderConstantBitrate && codec !== "vvc") {
    const target = settings.ChartRenderBitrateMbps > 0 ? settings.ChartRenderBitrateMbps : recommendedMbps(settings);
    return { typical: toMb(target), max: toMb(target) * 1.1, kind: "cbr" };
  }

  if (customEncoder || tier === "custom") {
    if (settings.ChartRenderBitrateMbps > 0) {
      return { typical: toMb(settings.ChartRenderBitrateMbps), max: toMb(Math.ceil(settings.ChartRenderBitrateMbps * 1.5)), kind: "vbr" };
    }
    return { typical: null, max: null, kind: "unbounded" };
  }

  const cap = tierCap(tier, settings);
  if (cap <= 0) return { typical: null, max: null, kind: "unbounded" };
  const learned = state.quality?.learned?.[`${codec}|${tier}`];
  const ratio = learned ?? DEFAULT_RATIO;
  // The encoder limit sits at 90% of the cap, so the cap plus a small margin is a safe maximum.
  return { typical: toMb(cap * ratio), max: toMb(cap) * 1.05, kind: learned != null ? "learned" : "guess" };
}

export function formatSize(mb: number | null): string {
  if (mb == null) return "—";
  return mb >= 1000 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.max(1, Math.round(mb))} MB`;
}

export function formatBytes(bytes: number): string {
  return formatSize(bytes / (1024 * 1024));
}

export function describeEstimate(estimate: SizeEstimate): string {
  switch (estimate.kind) {
    case "nochart": return "加载谱面后显示大小估算";
    case "unbounded": return "不限码率，取决于画面，可能几 GB";
    case "fixed": return `约 ${formatSize(estimate.typical)}（ProRes 固定码率）`;
    case "cbr": return `约 ${formatSize(estimate.typical)}（恒定码率）`;
    case "vbr": return `通常约 ${formatSize(estimate.typical)}，最多约 ${formatSize(estimate.max)}`;
    default: return `通常约 ${formatSize(estimate.typical)}，最多约 ${formatSize(estimate.max)}`;
  }
}

// Warnings that matter before starting: platform limits and free disk space.
export function sizeWarnings(state: WebUiState): string[] {
  const settings = state.settings;
  const estimate = estimateSize(state);
  const warnings: string[] = [];
  const worst = estimate.max ?? (estimate.kind === "fixed" ? estimate.typical : null);
  if (worst != null && worst > 16 * 1024) warnings.push("最大可能超过 B 站网页端 16 GB 上限（粉丝 ≥ 1000 为 64 GB）。可以降一档画质，或者分 P 上传。");
  const px = settings.ChartRenderWidth * settings.ChartRenderHeight;
  if (settings.ChartRenderFps > 120 && px <= 3840 * 2160) warnings.push("B 站 4K 及以下最高支持 120 帧/秒，YouTube 推荐表只到 60 帧/秒；更高帧率上传后会被降帧。");
  if (px > 3840 * 2160 && settings.ChartRenderFps > 30) warnings.push("B 站 8K 最高支持 30 帧/秒。");
  const disk = diskCheck(state);
  if (disk && !disk.enough) warnings.push(disk.text);
  if (estimate.kind === "unbounded") warnings.push("这个设置不限码率，粒子多的谱面可能非常大，开始前请留足硬盘空间。");
  return warnings;
}

// The output and an equally large temp video live at the same time until the mux finishes.
export function diskCheck(state: WebUiState): { enough: boolean; text: string } | null {
  const estimate = estimateSize(state);
  const worst = estimate.max ?? (estimate.kind === "fixed" ? estimate.typical : null);
  const exportDrive = state.disk?.export;
  const workspaceDrive = state.disk?.workspace;
  if (!exportDrive || exportDrive.freeBytes < 0) return null;
  const free = exportDrive.freeBytes / (1024 * 1024);
  if (worst == null) return { enough: true, text: `${exportDrive.drive} 剩余 ${formatSize(free)}（不限码率，无法判断是否够用）` };
  const sameDrive = workspaceDrive && workspaceDrive.drive.toLowerCase() === exportDrive.drive.toLowerCase();
  const need = sameDrive ? worst * 2 : worst;
  const enough = need <= free && (sameDrive || !workspaceDrive || workspaceDrive.freeBytes < 0 || worst <= workspaceDrive.freeBytes / (1024 * 1024));
  const text = `${exportDrive.drive} 剩余 ${formatSize(free)}，最坏情况需要约 ${formatSize(need)}${sameDrive ? "（成片 + 同样大的临时文件）" : ""}`;
  return { enough, text: enough ? text : `硬盘空间可能不够：${text}。` };
}

// Live projection while rendering: bytes of raw video so far, scaled to all frames, plus audio.
export function projectedSize(state: WebUiState): number | null {
  const render = state.render;
  const written = render.progress.writtenFrames;
  if (!render.active || written <= 0 || render.tempVideoBytes <= 0) return null;
  const total = Math.max(written, render.progress.totalFrames);
  const seconds = total / Math.max(1, state.settings.ChartRenderFps);
  return render.tempVideoBytes / (1024 * 1024) * (total / written) + audioMbps(state.settings) * seconds / 8;
}

export function hasEncoderResults(encoders: EncoderState): boolean {
  return Object.keys(encoders.results).length > 0;
}
