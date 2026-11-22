export type PatchState = "active" | "failed" | "blocked" | "inactive";

export type PageKey =
  | "overview"
  | "render"
  | "output"
  | "quality"
  | "capture"
  | "connect"
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
  // small / standard / high / lossless, or custom (CRF + bitrate as entered).
  ChartRenderQualityTier: string;
  ChartRenderDiagnosticMode: boolean;
  WebUiLanPort: number;
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
  // Raw video bytes written so far by the running render (live size projection).
  tempVideoBytes: number;
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
    lanPort: number;
    // Read-only links (no access token).
    lanViewUrls: string[];
  };
  compatibility: {
    gameVersion: string;
    // Build number shown in game as "r150"; -1 when unknown.
    gameRelease: number;
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
  update: UpdateState;
  quality: QualityState;
  chart: ChartState;
  disk: { export: DriveState; workspace: DriveState };
  workspace: WorkspaceState;
  lastResult: LastResult;
  // True when this page was opened through a read-only link.
  viewer?: boolean;
}

// Quality tier tables (ChartRenderQualityTiers), so the page shows exactly what the encoder uses.
export interface QualityState {
  // Encoder family -> quality value for [small, standard, high, lossless].
  quality: Record<string, number[]>;
  // Bitrate cap in Mbps per tier for the current codec/resolution/frame rate; 0 = no cap.
  caps: Record<string, number>;
  platformMbps: number;
  // "codec|tier" -> learned average bitrate / cap from earlier renders.
  learned: Record<string, number>;
}

export interface ChartState {
  loaded: boolean;
  seconds: number;
  // -1 when the editor has no continuous selection.
  selectionSeconds: number;
}

export interface DriveState {
  drive: string;
  freeBytes: number;
}

export interface WorkspaceState {
  path: string;
  tempFolders: number;
  tempBytes: number;
  logFiles: number;
  logBytes: number;
}

export interface LastResult {
  path: string;
  bytes: number;
  averageMbps: number;
  capRatio: number;
}

export interface UpdateState {
  status: "idle" | "checking" | "available" | "current" | "failed";
  latest: string;
  current: string;
  url: string;
  notes: string;
  auroraUrl: string;
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
  countdown: number;
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
