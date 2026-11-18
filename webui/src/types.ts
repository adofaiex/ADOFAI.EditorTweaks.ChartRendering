export type PatchState = "active" | "failed" | "blocked" | "inactive";

export type PageKey =
  | "overview"
  | "render"
  | "tools";

export interface PatchStatus {
  id: string;
  name: string;
  description: string;
  state: PatchState;
  patchCount: number;
  reason: string;
  dependency?: string;
}

export interface SettingsState {
  WebUiOpenHotkey: string;
  WebUiOpenMode: string;
  WebUiAllowLan: boolean;
  ChartRenderWorkspaceDirectory: string;
  ChartRenderExportDirectory: string;
  ChartRenderWidth: number;
  ChartRenderHeight: number;
  ChartRenderFps: number;
  ChartRenderCrf: number;
  ChartRenderBitrateMbps: number;
  ChartRenderConstantBitrate: boolean;
  ChartRenderPreset: string;
  ChartRenderEncoderMode: string;
  ChartRenderCaptureFormat: string;
  ChartRenderCaptureSource: string;
  ChartRenderGameViewBackend: string;
  ChartRenderPreviewMode: string;
  ChartRenderAudioFormat: string;
  ChartRenderVideoFormat: string;
  ChartRenderCompletionTailSeconds: number;
  ChartRenderAudioSyncOffsetMs: number;
  ChartRenderShowHitJudgments: boolean;
  ChartRenderCompatKeyViewers: boolean;
  ChartRenderUseSelectedRange: boolean;
  ChartRenderCustomMuxArgs: string;
  ChartRenderVideoCodec: string;
  ChartRenderHardwareEncoder: string;
  ChartRenderHardwarePreset: string;
  ChartRenderCpuPreset: string;
  ChartRenderDetailedEncoderSettings: boolean;
  FfmpegCustomPath: string;
}

export interface RenderProgress {
  value: number;
  writtenFrames: number;
  totalFrames: number;
  duplicateFrames: number;
  duplicateRatio: number;
  processingFramesPerSecond: number;
  estimatedRemaining: string;
  stage: string;
  detail: string;
  encoderName: string;
  memoryBudget: string;
  queueBudget: string;
}

export interface RenderState {
  active: boolean;
  taskId: string;
  state: string;
  cancelRequested: boolean;
  writingOutput: boolean;
  quitRefused: boolean;
  captureSource: string;
  outputPath: string;
  message: string;
  progress: RenderProgress;
}

export interface WebUiState {
  server: {
    connected: boolean;
    version: string;
    lanEnabled: boolean;
    lanUrl: string;
    lanUrls: string[];
  };
  compatibility: {
    gameVersion: string;
    editorVersion: string;
    modVersion: string;
  };
  patches: PatchStatus[];
  patchSummary: {
    active: number;
    total: number;
    registrationError: boolean;
  };
  settings: SettingsState;
  render: RenderState;
  encoders: EncoderState;
  ffmpeg: FfmpegState;
}

// FFmpeg auto-download state (ChartFfmpegInstaller): the lite package ships without ffmpeg.exe.
export interface FfmpegState {
  status: "missing" | "downloading" | "verifying" | "installing" | "ready" | "failed";
  version: string;
  variant: string;
  progress: number;
  downloadedBytes: number;
  totalBytes: number;
  installedBytes: number;
  source: string;
  message: string;
  path: string;
  sha256: string;
}

// Encoder detection (ChartEncoderCapabilities). results maps an FFmpeg encoder name to whether it
// works on this computer; it is empty until the first detection finishes.
export interface EncoderState {
  status: "unknown" | "detecting" | "ready";
  detectedAt: string;
  gpu: string;
  results: Record<string, boolean>;
}
