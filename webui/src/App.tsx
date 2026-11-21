import {
  Button,
  Message,
  Spin,
  Switch,
} from "@arco-design/web-react";
import {
  IconApps,
  IconCheckCircle,
  IconCheck,
  IconCode,
  IconDesktop,
  IconFile,
  IconFolder,
  IconImage,
  IconInfoCircle,
  IconPlayArrow,
  IconRefresh,
  IconSettings,
  IconTool,
  IconUndo,
  IconCloseCircle,
  IconDown,
  IconLink,
  IconMobile,
  IconSound,
  IconStorage,
  IconExport,
  IconCopy,
} from "@arco-design/web-react/icon";
import qrcode from "qrcode-generator";
import React from "react";
import { createEventSource, createMockState, detectSession, diagnosticsDownloadUrl, localDocumentUrl, postJson } from "./api";
import type { SessionMode } from "./api";
import {
  QUALITY_LABEL, TIERS, describeEstimate, diskCheck, encoderFamily, estimateSize, formatBytes, formatSize,
  normalizedTier, projectedSize, renderSeconds, sizeWarnings, tierCap, tierQuality,
} from "./estimate";
import type { TierId } from "./estimate";
import type { EncoderState, FfmpegState, PageKey, PatchStatus, RenderState, SettingsState, UpdateState, WebUiState } from "./types";

type NavEntry = { key: PageKey; label: string; description: string; icon: React.ReactNode } | { group: string };

const navEntries: NavEntry[] = [
  { key: "overview", label: "总览", description: "状态、编码器、FFmpeg", icon: <IconApps /> },
  { key: "render", label: "渲染", description: "开始渲染、进度、结果", icon: <IconPlayArrow /> },
  { group: "渲染设置" },
  { key: "output", label: "输出", description: "分辨率、帧率、格式、目录", icon: <IconImage /> },
  { key: "quality", label: "画质与编码", description: "画质档位、编码器", icon: <IconCode /> },
  { key: "capture", label: "画面与音频", description: "捕获方式、按键显示、音频", icon: <IconSound /> },
  { group: "其他" },
  { key: "connect", label: "网页与连接", description: "快捷键、平板访问", icon: <IconMobile /> },
  { key: "tools", label: "工具与诊断", description: "导出日志、清理、预设", icon: <IconTool /> },
];

const pageTitles: Record<PageKey, [string, string]> = {
  overview: ["总览", "一眼看到 Mod 是否正常。"],
  render: ["渲染", "开始渲染、查看进度和结果。"],
  output: ["输出", "视频的尺寸、帧率、文件格式和保存位置。"],
  quality: ["画质与编码", "选一个画质档位就够了；想细调再打开“详细编码设置”。"],
  capture: ["画面与音频", "录什么画面、按键显示 Mod、音频同步。"],
  connect: ["网页与连接", "怎么打开这个页面，以及用手机 / 平板看进度。"],
  tools: ["工具与诊断", "遇到问题、清理空间、预设与分享。"],
};

const patchDescriptions: Record<string, string> = {
  渲染输入保护: "渲染期间屏蔽输入干扰",
  谱面渲染: "将谱面画面和声音输出为视频",
};

const statusLabel: Record<string, string> = {
  active: "已启用",
  failed: "不可用",
  blocked: "已阻止",
  inactive: "未启用",
};

const settingLabels: Record<string, Record<string, string>> = {
  WebUiOpenMode: { auto: "自动（优先 Steam 覆盖层）", steam: "Steam 覆盖层", browser: "系统浏览器" },
  ChartRenderCaptureFormat: { rgba: "RGBA", bgra: "BGRA" },
  ChartRenderPreviewMode: { full: "完整", dim: "暗色", minimal: "极简" },
  ChartRenderVideoCodec: {
    h264: "H.264（推荐）",
    hevc: "HEVC",
    av1: "AV1",
    prores: "ProRes 422 HQ",
    vvc: "H.266（实验性）",
  },
};

const labelFor = (group: string, value: string) => settingLabels[group]?.[value] ?? value;

// The NVENC / CPU presets each simple encoder mode uses (FfmpegEncoder.GetNvencPreset / GetX264Preset).
const encoderPresetDefaults: Record<string, { hardware: string; cpu: string }> = {
  "auto-balanced": { hardware: "p4", cpu: "veryfast" },
  fastest: { hardware: "p1", cpu: "ultrafast" },
  balanced: { hardware: "p4", cpu: "veryfast" },
  quality: { hardware: "p6", cpu: "fast" },
  "cpu-compatibility": { hardware: "p4", cpu: "veryfast" },
  custom: { hardware: "p4", cpu: "veryfast" },
};

const proResOnlyMov = "ProRes 只能保存为 MOV";
const movNoFlac = "MOV 不支持 FLAC 音频";

// ProRes only fits in MOV, and MOV cannot hold FLAC (the mod switches these on its own as well, for
// API callers). Applied to every settings change so the page never saves such a pair, and returns
// what was switched so the user is told.
function applyContainerRules(current: SettingsState, requested: Partial<SettingsState>): { changes: Partial<SettingsState>; notes: string[] } {
  const next = { ...current, ...requested };
  const changes = { ...requested };
  const notes: string[] = [];
  if (next.ChartRenderVideoCodec.toLowerCase() === "prores" && next.ChartRenderVideoFormat.toLowerCase() !== "mov") {
    changes.ChartRenderVideoFormat = "mov";
    notes.push(`${proResOnlyMov}，视频输出格式已改为 MOV。`);
  }

  const codec = next.ChartRenderVideoCodec.toLowerCase();
  if (next.ChartRenderConstantBitrate && (codec === "prores" || codec === "vvc")) {
    changes.ChartRenderConstantBitrate = false;
    notes.push(codec === "prores" ? "ProRes 不支持恒定比特率，已关闭 CBR。" : "H.266 不支持恒定比特率，已关闭 CBR。");
  }

  const format = (changes.ChartRenderVideoFormat ?? next.ChartRenderVideoFormat).toLowerCase();
  if (format === "mov" && next.ChartRenderAudioFormat.toLowerCase() === "flac") {
    changes.ChartRenderAudioFormat = "alac";
    notes.push(`${movNoFlac}，音频格式已改为 ALAC（同为无损，音质不变）。`);
  }

  return { changes, notes };
}

// Settings that a share code carries: render settings only, never paths, hotkeys or device data.
const SHARE_KEYS: Array<keyof SettingsState> = [
  "ChartRenderWidth", "ChartRenderHeight", "ChartRenderFps", "ChartRenderVideoFormat", "ChartRenderAudioFormat",
  "ChartRenderCompletionTailSeconds", "ChartRenderVideoCodec", "ChartRenderQualityTier", "ChartRenderDetailedEncoderSettings",
  "ChartRenderEncoderMode", "ChartRenderHardwareEncoder", "ChartRenderHardwarePreset", "ChartRenderCpuPreset",
  "ChartRenderCrf", "ChartRenderBitrateMbps", "ChartRenderConstantBitrate", "ChartRenderCaptureSource",
  "ChartRenderGameViewBackend", "ChartRenderShowHitJudgments", "ChartRenderPreviewMode", "ChartRenderCompatKeyViewers",
  "ChartRenderAudioSyncOffsetMs",
];

const RENDER_PRESETS: Array<{ id: string; name: string; detail: string; changes: Partial<SettingsState> }> = [
  { id: "bili1080", name: "B 站 1080p60", detail: "1920×1080 · 60 · H.264 · 标准 · AAC 320k", changes: { ChartRenderWidth: 1920, ChartRenderHeight: 1080, ChartRenderFps: 60, ChartRenderVideoCodec: "h264", ChartRenderQualityTier: "standard", ChartRenderVideoFormat: "mp4", ChartRenderAudioFormat: "aac", ChartRenderConstantBitrate: false } },
  { id: "bili4k", name: "B 站 4K60", detail: "3840×2160 · 60 · HEVC · 标准（B 站推荐 H.264 / HEVC）", changes: { ChartRenderWidth: 3840, ChartRenderHeight: 2160, ChartRenderFps: 60, ChartRenderVideoCodec: "hevc", ChartRenderQualityTier: "standard", ChartRenderVideoFormat: "mp4", ChartRenderAudioFormat: "aac", ChartRenderConstantBitrate: false } },
  { id: "yt4k", name: "YouTube 4K60", detail: "3840×2160 · 60 · H.264 · 标准（YouTube 推荐 H.264）", changes: { ChartRenderWidth: 3840, ChartRenderHeight: 2160, ChartRenderFps: 60, ChartRenderVideoCodec: "h264", ChartRenderQualityTier: "standard", ChartRenderVideoFormat: "mp4", ChartRenderAudioFormat: "aac", ChartRenderConstantBitrate: false } },
  { id: "archive4k", name: "4K 存档", detail: "3840×2160 · 60 · HEVC · 高质量", changes: { ChartRenderWidth: 3840, ChartRenderHeight: 2160, ChartRenderFps: 60, ChartRenderVideoCodec: "hevc", ChartRenderQualityTier: "high", ChartRenderVideoFormat: "mp4", ChartRenderAudioFormat: "aac", ChartRenderConstantBitrate: false } },
  { id: "edit", name: "剪辑用", detail: "1920×1080 · 60 · ProRes · MOV · ALAC", changes: { ChartRenderWidth: 1920, ChartRenderHeight: 1080, ChartRenderFps: 60, ChartRenderVideoCodec: "prores", ChartRenderVideoFormat: "mov", ChartRenderAudioFormat: "alac", ChartRenderConstantBitrate: false } },
  { id: "preview", name: "快速预览", detail: "1280×720 · 30 · H.264 · 小文件", changes: { ChartRenderWidth: 1280, ChartRenderHeight: 720, ChartRenderFps: 30, ChartRenderVideoCodec: "h264", ChartRenderQualityTier: "small", ChartRenderVideoFormat: "mp4", ChartRenderAudioFormat: "aac", ChartRenderConstantBitrate: false } },
];

type SettingsHandler = (changes: Partial<SettingsState>) => void;

function App() {
  const [state, setState] = React.useState<WebUiState>(() => createMockState());
  const [page, setPage] = React.useState<PageKey>("render");
  const [session, setSession] = React.useState<SessionMode | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [connected, setConnected] = React.useState(false);
  const [reconnecting, setReconnecting] = React.useState(false);
  const [connectionReload, setConnectionReload] = React.useState(0);
  // Set once the mod has answered, and whether a render was running in the last state it sent: a
  // drop after that means the game was closed or crashed, possibly in the middle of a render.
  const [everConnected, setEverConnected] = React.useState(false);
  const [lostDuringRender, setLostDuringRender] = React.useState(false);
  const lastRenderActive = React.useRef(false);
  const renderActionInFlight = React.useRef(false);
  const demoMode = session === "demo";
  const viewer = session === "view";

  React.useEffect(() => {
    void detectSession().then(setSession);
  }, []);

  React.useEffect(() => {
    if (session === null) return;
    let disposed = false;
    let events: EventSource | undefined;
    let retryTimer: number | undefined;
    const live = session === "full" || session === "view";

    const scheduleReconnect = () => {
      if (disposed || !live || retryTimer !== undefined) return;
      setConnected(false);
      setReconnecting(true);
      retryTimer = window.setTimeout(() => {
        retryTimer = undefined;
        connect();
      }, 2000);
    };

    const connect = () => {
      if (disposed || !live) return;
      events?.close();
      events = undefined;
      const source = createEventSource((nextState) => {
        if (disposed) return;
        setState(nextState);
        lastRenderActive.current = nextState.render.active;
        setLoading(false);
        setConnected(true);
        setEverConnected(true);
        setLostDuringRender(false);
        setReconnecting(false);
      });
      events = source;
      source.onopen = () => {
        if (!disposed) {
          setLoading(false);
          setConnected(true);
          setReconnecting(false);
        }
      };
      source.onerror = () => {
        if (disposed) return;
        setLoading(false);
        setConnected(false);
        if (lastRenderActive.current) setLostDuringRender(true);
        source.close();
        if (events === source) events = undefined;
        scheduleReconnect();
      };
    };

    if (live) connect();
    else setLoading(false);

    return () => {
      disposed = true;
      events?.close();
      if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    };
  }, [connectionReload, session]);

  React.useEffect(() => {
    const preventBrowserSave = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
        event.preventDefault();
        event.stopPropagation();
        Message.info(demoMode ? "本地预览不需要保存网页。" : "设置会自动保存，不需要保存网页。");
      }
    };

    window.addEventListener("keydown", preventBrowserSave);
    return () => window.removeEventListener("keydown", preventBrowserSave);
  }, [demoMode]);

  const updateSettings: SettingsHandler = async (requested) => {
    if (viewer) return;
    if (state.render.active) {
      Message.warning("渲染进行中，设置暂时锁定，请等待渲染结束。");
      return;
    }

    const { changes, notes } = applyContainerRules(state.settings, requested);
    const announce = () => {
      if (notes.length > 0) Message.info({ content: notes.join(" "), duration: 6000 });
    };
    setState((current) => ({ ...current, settings: { ...current.settings, ...changes } }));
    if (demoMode) {
      announce();
      return;
    }

    try {
      const response = await postJson<{ state: WebUiState }>("/api/settings", { changes });
      if (response.state) setState(response.state);
      announce();
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "设置保存失败");
    }
  };

  const renderAction = async (action: "start" | "cancel") => {
    if (viewer || renderActionInFlight.current || (action === "cancel" && state.render.cancelRequested)) return;
    if (!demoMode && !connected) {
      Message.error("Mod 服务未连接，正在尝试重连。请稍后再试。");
      return;
    }

    renderActionInFlight.current = true;
    try {
      if (demoMode) {
        runDemoRender(action, setState);
      } else {
        const response = await postJson<{ state: WebUiState }>(`/api/render/${action}`, { taskId: state.render.taskId });
        if (response.state) {
          // The mod answers a cancel before the game publishes the canceling state, so the button
          // shows the request was taken right away.
          setState(action === "cancel" && response.state.render?.active
            ? { ...response.state, render: { ...response.state.render, cancelRequested: true } }
            : response.state);
        }
      }
      setPage("render");
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "渲染操作失败");
    } finally {
      renderActionInFlight.current = false;
    }
  };

  // Returns the response body (or null on failure / in the demo).
  const runAction = async <T extends object = Record<string, unknown>>(path: string, body?: unknown, success = "操作已完成"): Promise<T | null> => {
    if (demoMode) {
      Message.success(`演示状态：${success}`);
      return null;
    }

    try {
      const response = await postJson<T & { state?: WebUiState }>(path, body ?? {});
      if (response.state) setState(response.state);
      if (success) Message.success(success);
      return response;
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "操作失败");
      return null;
    }
  };

  const openDocument = (path: string) => {
    if (demoMode) {
      Message.info("本地预览没有连接 Mod 服务，帮助页面无法打开。");
      return;
    }

    const opened = window.open(localDocumentUrl(path), "_blank", "noopener,noreferrer");
    if (!opened) Message.error("浏览器阻止了帮助页面，请允许当前页面打开新标签页。");
  };

  if (session === "unauthorized") return <Unauthorized />;

  const offline = !demoMode && !connected;
  const visiblePage: PageKey = viewer ? "render" : page;
  const connectionLabel = demoMode
    ? "本地预览（演示数据）"
    : connected
      ? viewer ? "只读链接：只能查看进度" : "本机服务已连接"
      : reconnecting
        ? "连接断开，正在重连…"
        : "正在连接 Mod 服务…";
  const pageProps = { state, onSettings: updateSettings, locked: state.render.active || viewer };

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand-lockup">
          <span className="brand-mark"><IconApps /></span>
          <span className="brand-name">EDITOR <strong>TWEAKS</strong></span>
          <span className="brand-divider">/</span>
          <span className="brand-page">{viewer ? "渲染进度" : "设置"}</span>
        </div>
        <div className="topbar-actions">
          <span className={`connection-dot ${connected ? "is-online" : ""}`} />
          <span className="connection-label">{connectionLabel}</span>
          {!demoMode && !connected ? <button className="connection-retry" type="button" onClick={() => setConnectionReload((current) => current + 1)} title="重新连接 Mod 服务"><IconRefresh /> 重连</button> : null}
        </div>
      </header>

      {!demoMode && everConnected && !connected ? <div className="connection-lost-banner" role="alert">
        <IconCloseCircle />
        <div>
          <strong>{lostDuringRender ? "与游戏的连接已断开，渲染被中断" : "与游戏的连接已断开"}</strong>
          <span>{lostDuringRender
            ? "游戏可能已经关闭或崩溃。这次渲染没有完成，导出文件夹里不会有可用的视频（残留的 .part 和临时文件下次启动游戏时会自动清理）。"
            : "游戏可能已经关闭或崩溃。页面会自动尝试重连。"}</span>
        </div>
      </div> : null}
      <div className={`workspace ${viewer ? "is-viewer" : ""}`}>
        {viewer ? null : <aside className="sidebar">
          <nav>
            {navEntries.map((entry) => "group" in entry
              ? <div className="nav-group" key={entry.group}>{entry.group}</div>
              : <button className={`nav-item ${page === entry.key ? "is-active" : ""}`} key={entry.key} onClick={() => setPage(entry.key)}>
                  <span className="nav-icon">{entry.icon}</span>
                  <span className="nav-copy">
                    <span className="nav-label">{entry.label}</span>
                    <span className="nav-description">{entry.description}</span>
                  </span>
                </button>)}
          </nav>
        </aside>}

        <main className="main-content">
          <RunBar state={state} page={visiblePage} offline={offline} viewer={viewer} onStart={() => renderAction("start")} onCancel={() => renderAction("cancel")} />
          {state.update?.status === "available" && !viewer ? <UpdateNotice update={state.update} /> : null}
          {state.ffmpeg && state.ffmpeg.status !== "ready" && !viewer ? (
            <FfmpegNotice
              ffmpeg={state.ffmpeg}
              disabled={state.render.active}
              countdown={state.ffmpeg.countdown >= 0 ? state.ffmpeg.countdown : null}
              onCancelAuto={() => runAction("/api/ffmpeg/cancel-auto", undefined, "")}
              onInstall={() => runAction("/api/ffmpeg/install", { variant: "custom" }, "")}
              onInstallFull={() => runAction("/api/ffmpeg/install", { variant: "full" }, "")}
            />
          ) : null}
          {loading || session === null ? <div className="loading-state"><Spin size={32} /><span>正在连接 Mod 服务…</span></div> : <>
            <PageHeading title={pageTitles[visiblePage][0]} description={viewer ? "通过只读链接打开：可以查看进度，不能开始、取消渲染或修改设置。" : pageTitles[visiblePage][1]} />
            {visiblePage === "overview" ? <OverviewPage state={state} disabled={state.render.active} onAction={runAction} /> : null}
            {visiblePage === "render" ? <RenderPage state={state} offline={offline} viewer={viewer} onSettings={updateSettings} onNavigate={setPage} onStart={() => renderAction("start")} onCancel={() => renderAction("cancel")} onAction={runAction} demo={demoMode} /> : null}
            {visiblePage === "output" ? <OutputPage {...pageProps} demo={demoMode} onAction={runAction} /> : null}
            {visiblePage === "quality" ? <QualityPage {...pageProps} /> : null}
            {visiblePage === "capture" ? <CapturePage {...pageProps} /> : null}
            {visiblePage === "connect" ? <ConnectPage {...pageProps} demo={demoMode} onAction={runAction} /> : null}
            {visiblePage === "tools" ? <ToolsPage {...pageProps} demo={demoMode} onAction={runAction} onOpenDocument={openDocument} /> : null}
          </>}
        </main>
      </div>

      <footer className="statusbar">
        <span className="status-running"><span className="status-led" />{viewer ? "只读" : "运行中"}</span>
        <span className="status-divider" />
        <span className="status-version">EDITOR TWEAKS {state.compatibility.modVersion}</span>
      </footer>
    </div>
  );
}

function Unauthorized() {
  return <div className="unauthorized">
    <IconLink />
    <h1>需要从游戏里打开</h1>
    <p>这个页面需要访问链接才能使用。在电脑上按设置页快捷键（默认 F8）打开设置页，进入“网页与连接”，用手机或平板扫描那里的二维码即可。扫码打开一次后，这台设备会被记住，之后存成书签就能直接打开。</p>
  </div>;
}

// ---------------------------------------------------------------------------------------------
// Shared pieces

function summaryText(state: WebUiState): string {
  const s = state.settings;
  const tier = normalizedTier(s);
  const codec = s.ChartRenderVideoCodec.toLowerCase();
  const quality = codec === "prores" ? "ProRes" : s.ChartRenderConstantBitrate ? "恒定码率" : tier === "custom" ? "自定义画质" : `${TIERS.find((t) => t.id === tier)?.name ?? "标准"}画质`;
  const capture = s.ChartRenderCaptureSource.toLowerCase() === "game-view" ? "游戏画面" : "摄像机";
  return `${s.ChartRenderWidth}×${s.ChartRenderHeight} · ${s.ChartRenderFps} 帧/秒 · ${labelFor("ChartRenderVideoCodec", codec).replace(/（.*）/, "")} · ${quality} · ${capture}`;
}

function stageLabel(render: RenderState): string {
  if (!render.active) return render.progress.stage || "等待渲染";
  if (render.cancelRequested) return "正在取消…";
  return render.progress.stage || "渲染中";
}

// Visible on every page. The render page has its own full progress card, so here the bar only
// shows the settings summary there, never two progress bars on one page.
function RunBar({ state, page, offline, viewer, onStart, onCancel }: { state: WebUiState; page: PageKey; offline: boolean; viewer: boolean; onStart: () => void; onCancel: () => void }) {
  const summaryOnly = page === "render";
  const percent = Math.round(Math.max(0, Math.min(1, state.render.progress.value)) * 1000) / 10;
  return <div className={`run-bar ${summaryOnly ? "summary-only" : ""}`}>
    {summaryOnly ? <span className="run-bar-label">本次设置</span> : <span className="run-bar-stage">{state.render.active ? `${stageLabel(state.render)} ${percent}%` : "等待渲染"}</span>}
    {summaryOnly ? null : <div className="run-bar-track"><div style={{ width: `${percent}%` }} /></div>}
    <span className="run-bar-summary" title={summaryText(state)}>{summaryText(state)}</span>
    {summaryOnly || viewer ? null : state.render.active
      ? <Button className="danger-button" size="small" disabled={state.render.cancelRequested || offline} onClick={onCancel}>{state.render.cancelRequested ? "正在取消…" : "取消渲染"}</Button>
      : <Button className="primary-button" size="small" disabled={offline} icon={<IconPlayArrow />} onClick={onStart}>开始渲染</Button>}
  </div>;
}

function ChipSetting({ label, description, value, options, onChange, disabled, disabledOptions }: { label: string; description?: string; value: string; options: Array<[string, string]>; onChange: (value: string) => void; disabled?: boolean; disabledOptions?: Record<string, string> }) {
  return <div className="setting-row chip-setting">
    <div><strong>{label}</strong>{description ? <span>{description}</span> : null}</div>
    <div className="preset-options">
      {options.map(([option, text]) => {
        const reason = disabledOptions?.[option];
        return <button key={option} type="button" className={`preset-button ${value === option ? "is-active" : ""}`} disabled={disabled || Boolean(reason)} title={reason} onClick={() => onChange(option)}>{text}</button>;
      })}
    </div>
  </div>;
}

function LockedNote({ state }: { state: WebUiState }) {
  if (state.viewer || !state.render.active) return null;
  return <div className="render-lock-note"><IconInfoCircle /><span>渲染进行中，设置已锁定，渲染结束后自动恢复可编辑。</span></div>;
}

function Panel({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return <section className="render-settings angular-panel"><SectionHeading title={title} hint={hint} />{children}</section>;
}

// ---------------------------------------------------------------------------------------------
// Overview

function OverviewPage({ state, disabled, onAction }: { state: WebUiState; disabled: boolean; onAction: (path: string, body?: unknown, success?: string) => Promise<unknown> }) {
  const allAvailable = state.patchSummary.active === state.patchSummary.total && !state.patchSummary.registrationError;
  const release = state.compatibility.gameRelease;
  const ffmpeg = state.ffmpeg;
  const ffmpegText = ffmpeg.status === "ready"
    ? `${ffmpeg.variant || "FFmpeg"}${ffmpeg.version ? ` ${ffmpeg.version}` : ""}`
    : ffmpeg.status === "failed" ? "下载失败" : ffmpeg.status === "missing" ? "未安装" : "正在准备";
  return <>
    <section className="compatibility-panel angular-panel">
      <div className="compatibility-item"><IconDesktop /><span><small>游戏版本</small><strong>{state.compatibility.gameVersion}{release > 0 ? `（r${release}）` : ""}</strong></span></div>
      <div className="compatibility-item"><IconApps /><span><small>Mod 版本</small><strong>{state.compatibility.modVersion}</strong></span></div>
      <div className="compatibility-item"><IconCode /><span><small>FFmpeg</small><strong className={ffmpeg.status === "ready" ? "" : "text-warning"}>{ffmpeg.status === "ready" ? "✓ " : ""}{ffmpegText}</strong></span></div>
      <div className="compatibility-item"><IconCheckCircle /><span><small>补丁状态</small><strong className={allAvailable ? "text-accent" : "text-warning"}>{allAvailable ? "全部正常" : "部分不可用"}</strong></span></div>
    </section>

    <FormatAvailability encoders={state.encoders} disabled={disabled} onDetect={() => onAction("/api/encoders/detect", undefined, "已开始重新检测")} />

    <section className="section-block">
      <SectionHeading title="补丁状态" hint={`${state.patchSummary.active}/${state.patchSummary.total} 个功能组可用`} />
      <div className="patch-list">{state.patches.map((patch) => <PatchRow key={patch.id} patch={patch} />)}</div>
    </section>
  </>;
}

// What this PC can encode, in plain words. Encoder names are kept under "technical details".
function FormatAvailability({ encoders, disabled, onDetect }: { encoders: EncoderState; disabled: boolean; onDetect: () => void }) {
  const [details, setDetails] = React.useState(false);
  const r = encoders.results;
  const detecting = encoders.status === "detecting";
  const has = Object.keys(r).length > 0;
  const vendor = (codec: string): string | null => r[`${codec}_nvenc`] ? "NVIDIA" : r[`${codec}_amf`] ? "AMD" : r[`${codec}_qsv`] ? "Intel" : null;
  const rows: Array<{ name: string; ok: boolean; how: string; tip: string }> = [
    { name: "H.264", ok: true, how: vendor("h264") ? `显卡编码（${vendor("h264")}）` : "CPU 编码", tip: vendor("h264") ? "最快，推荐" : "较慢" },
    { name: "HEVC / H.265", ok: true, how: vendor("hevc") ? `显卡编码（${vendor("hevc")}）` : "CPU 编码", tip: "文件比 H.264 小" },
    { name: "AV1", ok: Boolean(vendor("av1")), how: vendor("av1") ? `显卡编码（${vendor("av1")}）` : r.libsvtav1 || r["libaom-av1"] ? "CPU 编码" : "不可用", tip: vendor("av1") ? "体积最小" : "这块显卡不支持 AV1 显卡编码（需要 RTX 40 / RDNA 3 / Intel Arc），渲染会慢一些" },
    { name: "ProRes", ok: Boolean(r.prores_aw), how: r.prores_aw ? "CPU 编码" : "不可用", tip: "给剪辑软件用" },
    { name: "H.266", ok: false, how: r.libvvenc ? "CPU 编码" : "不可用", tip: "实验性，非常慢" },
  ];
  const noVendor = has && !["nvenc", "amf", "qsv"].some((suffix) => Object.keys(r).some((k) => k.endsWith(`_${suffix}`) && r[k]));
  const amdIntelMissing = has && !Object.keys(r).some((k) => (k.endsWith("_amf") || k.endsWith("_qsv")) && r[k]);
  return <section className="section-block">
    <SectionHeading title="这台电脑能用的视频格式" hint={detecting ? "正在检测…" : encoders.detectedAt ? `检测于 ${encoders.detectedAt}` : "尚未检测"} />
    {encoders.gpu ? <div className="settings-note">显卡：{encoders.gpu}</div> : null}
    {has ? <div className="patch-list">
      {rows.map((row) => <div className="patch-row" key={row.name}>
        <span className={`patch-icon ${row.ok ? "active" : "warning"}`}>{row.ok ? <IconCheckCircle /> : <IconInfoCircle />}</span>
        <div className="patch-name"><strong>{row.name}</strong><span>{row.tip}</span></div>
        <span className={`patch-state ${row.how === "不可用" ? "inactive" : "active"}`}><span className="patch-state-dot" />{row.how}</span>
      </div>)}
    </div> : <div className="settings-note">{detecting ? "正在检测这台电脑能用哪些编码器…" : "还没有检测结果。"}</div>}
    {noVendor ? <div className="settings-note">没有检测到可用的显卡编码，会使用 CPU 编码（较慢）。</div> : amdIntelMissing ? <div className="settings-note">没有检测到可用的 AMD 或 Intel 显卡编码。</div> : null}
    {has ? <DisclosureButton label={details ? "隐藏技术细节" : "技术细节（反馈问题时用）"} open={details} onClick={() => setDetails((v) => !v)} /> : null}
    {details ? <div className="encoder-raw">{encoderOrder.filter((name) => name in r).map((name) => <span key={name} className={r[name] ? "is-ok" : ""}>{r[name] ? "✓" : "✗"} {name}</span>)}</div> : null}
    <div className="page-actions"><span className="settings-note inline-note">更新显卡驱动后点一下</span><Button className="secondary-button" icon={<IconRefresh />} disabled={disabled || detecting} onClick={onDetect}>{detecting ? "正在检测…" : "重新检测"}</Button></div>
  </section>;
}

// ---------------------------------------------------------------------------------------------
// Render

function RenderPage({ state, offline, viewer, demo, onSettings, onNavigate, onStart, onCancel, onAction }: { state: WebUiState; offline: boolean; viewer: boolean; demo: boolean; onSettings: SettingsHandler; onNavigate: (page: PageKey) => void; onStart: () => void; onCancel: () => void; onAction: (path: string, body?: unknown, success?: string) => Promise<unknown> }) {
  const progress = state.render.progress;
  const percent = Math.round(Math.max(0, Math.min(1, progress.value)) * 1000) / 10;
  const terminal = getRenderResult(state.render);
  const settings = state.settings;
  const predicted = predictEncoder(settings, state.encoders);
  const estimate = estimateSize(state);
  const projected = projectedSize(state);
  const seconds = renderSeconds(state);
  const disk = diskCheck(state);
  const last = state.lastResult;
  const lastStats = last && last.bytes > 0
    ? `${formatBytes(last.bytes)} · 平均 ${last.averageMbps.toFixed(1)} Mbps${last.capRatio >= 0 ? `（用了上限的 ${Math.round(last.capRatio * 100)}%）` : ""}`
    : "";
  const isGameView = settings.ChartRenderCaptureSource.toLowerCase() === "game-view";
  const openFolder = () => demo ? Message.info("演示状态：会在资源管理器里打开成片所在文件夹。") : onAction("/api/folders/open", { target: "lastOutput" }, "");
  return <>
    {terminal ? <div className={`render-result-banner ${terminal.kind}`}><span className="render-result-icon">{terminal.kind === "success" ? <IconCheckCircle /> : terminal.kind === "failed" ? <IconCloseCircle /> : <IconInfoCircle />}</span><div><strong>{terminal.title}</strong><span>{terminal.detail}</span>{terminal.kind === "success" && lastStats ? <span>{lastStats}</span> : null}{state.render.outputPath && terminal.kind === "success" ? <code title={state.render.outputPath}>{state.render.outputPath}</code> : null}</div></div> : null}
    <section className="render-panel angular-panel">
      <div className="render-head"><div><span className="eyebrow">{state.render.active ? "正在处理" : "渲染状态"}</span><h2>{stageLabel(state.render)}</h2><p>{progress.detail || "点“开始渲染”后，全部进度会在这里实时更新。"}</p></div><div className="render-percent">{percent}<small>%</small></div></div>
      <div className="progress-track has-marks"><div className="progress-fill" style={{ width: `${percent}%` }} /><span className="progress-mark" style={{ left: "90%" }} /><span className="progress-mark" style={{ left: "92%" }} /></div>
      <div className="progress-stages"><span>0–90% 捕获画面</span><span>90–92% 完成编码</span><span>92–100% 合成音频</span></div>
      {state.render.active && state.render.writingOutput ? <div className="render-lock-note"><IconInfoCircle /><span>画面已经渲染完成，正在写入视频文件。请不要关闭游戏，等进度到 100% 并显示“渲染完成”后视频才可用。</span></div> : null}
      {state.render.active && state.render.quitRefused ? <div className="settings-note warning-note">刚才有一次关闭游戏的操作被拦下了，因为渲染还没完成。10 秒内再次关闭游戏会强制退出，视频将丢失。</div> : null}
      <div className="render-metrics">
        <Metric icon={<IconImage />} label="写入帧" value={`${progress.writtenFrames.toLocaleString()} / ${progress.totalFrames.toLocaleString()}`} />
        <Metric icon={<IconPlayArrow />} label="处理速度" value={`${progress.processingFramesPerSecond.toFixed(1)} 帧/秒`} />
        <Metric icon={<IconRefresh />} label="预计剩余" value={state.render.active && state.render.writingOutput ? "写入文件中" : progress.estimatedRemaining || "00:00:00"} />
        <Metric icon={<IconFile />} label="按目前进度推算大小" value={projected != null ? `约 ${formatSize(projected)}` : "—"} />
      </div>
      <div className="render-details">
        <DetailLine label="编码器" value={progress.encoderName || (predicted && predicted !== "none" ? encoderLabels[predicted] ?? predicted : "—")} />
        <DetailLine label="画质" value={summaryText(state).split(" · ")[3] ?? "—"} />
        <DetailLine label="捕获方式" value={isGameView ? `游戏画面 · ${settings.ChartRenderGameViewBackend === "screen" ? "最终屏幕" : "离屏"}` : "摄像机"} />
        <DetailLine label="输出路径" value={state.render.outputPath || settings.ChartRenderExportDirectory || "—"} />
      </div>
      {viewer ? null : state.render.active
        ? <Button className="danger-button" disabled={state.render.cancelRequested || offline} icon={<IconCloseCircle />} onClick={onCancel}>{offline ? "连接已断开" : state.render.cancelRequested ? "正在取消…" : "取消渲染"}</Button>
        : <div className="render-action-row"><div className="render-idle"><IconCheckCircle /> {last?.path ? `上次：${last.path.split(/[\\/]/).pop()}${lastStats ? ` · ${lastStats}` : ""}` : state.render.message || "当前没有正在运行的渲染任务"}</div><div className="button-row">{last?.path ? <Button className="secondary-button" icon={<IconFolder />} onClick={openFolder}>打开文件夹</Button> : null}<Button className="primary-button" disabled={offline} icon={<IconPlayArrow />} onClick={onStart}>开始渲染</Button></div></div>}
    </section>

    <Panel title="本次渲染会用" hint={viewer ? undefined : "点“修改”跳到对应设置"}>
      <div className="summary-list">
        <SummaryLine label="输出" value={`${settings.ChartRenderWidth}×${settings.ChartRenderHeight} · ${settings.ChartRenderFps} 帧/秒 · ${settings.ChartRenderVideoFormat.toUpperCase()} · ${settings.ChartRenderAudioFormat.toUpperCase()}`} onEdit={viewer ? undefined : () => onNavigate("output")} />
        <SummaryLine label="画质" value={`${summaryText(state).split(" · ")[3]} · ${predicted && predicted !== "none" ? encoderLabels[predicted] ?? predicted : "编码器检测中"} · ${describeEstimate(estimate)}`} onEdit={viewer ? undefined : () => onNavigate("quality")} />
        <SummaryLine label="画面" value={isGameView ? `游戏画面 · ${settings.ChartRenderGameViewBackend === "screen" ? "最终屏幕捕获" : "离屏渲染"}${settings.ChartRenderCompatKeyViewers ? " · 兼容按键显示 Mod" : ""}` : "摄像机渲染"} onEdit={viewer ? undefined : () => onNavigate("capture")} />
        {viewer ? null : <div className="setting-row"><div><strong>范围</strong><span>{state.chart?.loaded ? `当前谱面 ${formatDuration(state.chart.seconds)}${state.chart.selectionSeconds > 0 ? `，选中段落 ${formatDuration(state.chart.selectionSeconds)}` : "，没有框选段落"}` : "还没有加载谱面"}</span></div><span className="inline-switch"><Switch checked={settings.ChartRenderUseSelectedRange} disabled={state.render.active} onChange={(value) => onSettings({ ChartRenderUseSelectedRange: value })} /> 仅渲染选中段落</span></div>}
        {viewer || !disk ? null : <SummaryLine label="硬盘空间" value={disk.text} tone={disk.enough ? "ok" : "warn"} />}
        {seconds != null && !viewer ? null : null}
      </div>
      {viewer ? null : sizeWarnings(state).map((w) => <div className="settings-note warning-note" key={w}>{w}</div>)}
    </Panel>
  </>;
}

function SummaryLine({ label, value, onEdit, tone }: { label: string; value: string; onEdit?: () => void; tone?: "ok" | "warn" }) {
  return <div className="summary-line"><span>{label}</span><strong className={tone === "warn" ? "text-warning" : undefined}>{value}{tone === "ok" ? <em className="text-accent"> 够用</em> : null}</strong>{onEdit ? <button type="button" className="link-button" onClick={onEdit}>修改 ›</button> : null}</div>;
}

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

// ---------------------------------------------------------------------------------------------
// Output

const resolutionPresets: Array<[string, number, number]> = [["720p", 1280, 720], ["1080p", 1920, 1080], ["2K", 2560, 1440], ["4K", 3840, 2160], ["9:16", 1080, 1920], ["21:9", 2560, 1080]];

function OutputPage({ state, onSettings, locked, demo, onAction }: { state: WebUiState; onSettings: SettingsHandler; locked: boolean; demo: boolean; onAction: (path: string, body?: unknown, success?: string) => Promise<unknown> }) {
  const s = state.settings;
  const isProRes = s.ChartRenderVideoCodec.toLowerCase() === "prores";
  const shownVideoFormat = isProRes ? "mov" : s.ChartRenderVideoFormat.toLowerCase();
  const shownAudioFormat = shownVideoFormat === "mov" && s.ChartRenderAudioFormat.toLowerCase() === "flac" ? "alac" : s.ChartRenderAudioFormat.toLowerCase();
  const [picker, setPicker] = React.useState<null | "ChartRenderExportDirectory" | "ChartRenderWorkspaceDirectory">(null);
  const [workspaceOpen, setWorkspaceOpen] = React.useState(false);
  const screenBackend = s.ChartRenderCaptureSource.toLowerCase() === "game-view" && s.ChartRenderGameViewBackend === "screen";
  const openFolder = (target: string) => demo ? Message.info("演示状态：会在资源管理器里打开这个文件夹。") : onAction("/api/folders/open", { target }, "");
  return <>
    <LockedNote state={state} />
    <Panel title="画面尺寸">
      <PresetSetting label="分辨率预设" options={resolutionPresets.map(([name]) => name)} active={resolutionPresets.find(([, w, h]) => w === s.ChartRenderWidth && h === s.ChartRenderHeight)?.[0] ?? ""} onSelect={(name) => { const p = resolutionPresets.find(([n]) => n === name); if (p) onSettings({ ChartRenderWidth: p[1], ChartRenderHeight: p[2] }); }} disabled={locked} />
      <div className="settings-grid">
        <SettingNumber label="视频宽度" value={s.ChartRenderWidth} min={16} max={7680} step={2} onChange={(value) => onSettings({ ChartRenderWidth: value })} suffix="px" disabled={locked} />
        <SettingNumber label="视频高度" value={s.ChartRenderHeight} min={16} max={4320} step={2} onChange={(value) => onSettings({ ChartRenderHeight: value })} suffix="px" disabled={locked} />
      </div>
      <PresetSetting label="帧率预设" options={["30", "60", "120"]} active={String(s.ChartRenderFps)} onSelect={(preset) => onSettings({ ChartRenderFps: Number(preset) })} disabled={locked} />
      <div className="settings-grid">
        <SettingNumber label="帧率" value={s.ChartRenderFps} min={1} max={240} step={1} onChange={(value) => onSettings({ ChartRenderFps: value })} suffix="帧/秒" disabled={locked} />
      </div>
      {s.ChartRenderFps > 180 ? <div className="settings-note warning-note">帧率超过约 180 时，每帧的音频不足一个音频缓冲，音频无法捕获，渲染开始时会直接报错（48 kHz 下上限约 187）。</div> : null}
      {screenBackend ? <InfoBlock text="当前是“最终屏幕捕获”，实际分辨率跟随游戏窗口，这里的分辨率不生效。" /> : null}
    </Panel>
    <Panel title="文件">
      <ChipSetting label="视频格式" value={shownVideoFormat} options={[["mp4", "MP4"], ["mkv", "MKV"], ["mov", "MOV"]]} disabledOptions={isProRes ? { mp4: proResOnlyMov, mkv: proResOnlyMov } : undefined} onChange={(value) => onSettings({ ChartRenderVideoFormat: value })} disabled={locked} />
      <ChipSetting label="音频格式" description="AAC 体积小；FLAC / ALAC 无损" value={shownAudioFormat} options={[["aac", "AAC"], ["flac", "FLAC"], ["alac", "ALAC"]]} disabledOptions={shownVideoFormat === "mov" ? { flac: movNoFlac } : undefined} onChange={(value) => onSettings({ ChartRenderAudioFormat: value })} disabled={locked} />
      <PathSetting label="导出目录" value={s.ChartRenderExportDirectory} onChange={(value) => onSettings({ ChartRenderExportDirectory: value })} onBrowse={() => setPicker("ChartRenderExportDirectory")} onOpen={() => openFolder("export")} disabled={locked} />
      <div className="settings-grid">
        <SettingNumber label="结束后延迟停止" value={s.ChartRenderCompletionTailSeconds} min={0} step={0.1} onChange={(value) => onSettings({ ChartRenderCompletionTailSeconds: value })} suffix="秒" disabled={locked} />
      </div>
      <DisclosureButton label={workspaceOpen ? "隐藏工作区目录" : "工作区目录（临时文件和日志）"} open={workspaceOpen} onClick={() => setWorkspaceOpen((v) => !v)} />
      {workspaceOpen ? <>
        <PathSetting label="工作区目录" value={s.ChartRenderWorkspaceDirectory} onChange={(value) => onSettings({ ChartRenderWorkspaceDirectory: value })} onBrowse={() => setPicker("ChartRenderWorkspaceDirectory")} onOpen={() => openFolder("workspace")} disabled={locked} />
        <div className="settings-note">渲染时的临时视频和音频放在这里，渲染结束（成功或失败）后自动删除。和导出目录放在同一块硬盘上，合成更快。</div>
      </> : null}
    </Panel>
    {picker ? <FolderPicker demo={demo} initial={s[picker]} onCancel={() => setPicker(null)} onPick={(path) => { onSettings({ [picker]: path }); setPicker(null); }} /> : null}
  </>;
}

function PathSetting({ label, value, onChange, onBrowse, onOpen, disabled }: { label: string; value: string; onChange: (value: string) => void; onBrowse: () => void; onOpen: () => void; disabled?: boolean }) {
  const [draft, setDraft] = React.useState(value);
  React.useEffect(() => setDraft(value), [value]);
  return <div className="setting-row path-setting"><div><strong>{label}</strong></div>
    <div className="path-control">
      <input className="text-input" value={draft} disabled={disabled} onChange={(event) => setDraft(event.target.value)} onBlur={() => { if (!disabled && draft !== value) onChange(draft); }} onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur(); }} />
      <Button className="secondary-button" disabled={disabled} onClick={onBrowse}>浏览…</Button>
      <Button className="secondary-button" onClick={onOpen}>打开</Button>
    </div>
  </div>;
}

interface FolderListing { path: string; parent: string; entries: Array<{ name: string; path: string }>; drives: Array<{ name: string; path: string }>; error: string }

const demoFolders: Record<string, string[]> = {
  "": [], "C:\\": ["Users", "Program Files"], "D:\\": ["ADOFAI Renders", "SteamLibrary", "视频"], "D:\\ADOFAI Renders": ["Creplay", "谱面"], "D:\\视频": ["B站投稿"],
};

// Lists folders through the mod: a browser cannot hand the page a picked folder's full path, and a
// native dialog would open behind a fullscreen game or on the PC instead of the tablet.
function FolderPicker({ initial, demo, onPick, onCancel }: { initial: string; demo: boolean; onPick: (path: string) => void; onCancel: () => void }) {
  const [listing, setListing] = React.useState<FolderListing | null>(null);
  const [typed, setTyped] = React.useState(initial);
  const load = React.useCallback(async (path: string) => {
    if (demo) {
      const key = path.replace(/\\$/, "") || "";
      const normalized = /^[A-Z]:$/.test(key) ? `${key}\\` : key;
      const children = demoFolders[normalized] ?? [];
      setListing({ path: normalized, parent: normalized.includes("\\") && normalized.length > 3 ? normalized.replace(/\\[^\\]+$/, "").replace(/^([A-Z]:)$/, "$1\\") : "", entries: children.map((name) => ({ name, path: `${normalized.replace(/\\$/, "")}\\${name}` })), drives: [{ name: "C:（剩余 80 GB）", path: "C:\\" }, { name: "D:（剩余 120 GB）", path: "D:\\" }], error: "" });
      setTyped(normalized);
      return;
    }
    try {
      const result = await postJson<FolderListing>("/api/folders/list", { path });
      setListing(result);
      if (result.path) setTyped(result.path);
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "读取文件夹失败");
    }
  }, [demo]);
  React.useEffect(() => { void load(initial); }, [initial, load]);
  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onCancel(); }}>
    <div className="modal-panel angular-panel">
      <h3>选择文件夹</h3>
      <div className="folder-drives">{listing?.drives.map((drive) => <button type="button" className="preset-button" key={drive.path} onClick={() => load(drive.path)}>{drive.name}</button>)}</div>
      <div className="path-control">
        <input className="text-input" value={typed} onChange={(event) => setTyped(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") void load(typed); }} />
        <Button className="secondary-button" disabled={!listing?.parent} onClick={() => listing && load(listing.parent)}>上一级</Button>
      </div>
      {listing?.error ? <div className="settings-note warning-note">{listing.error}</div> : null}
      <div className="folder-list">
        {listing && listing.entries.length === 0 && !listing.error ? <div className="folder-empty">（没有子文件夹）</div> : null}
        {listing?.entries.map((entry) => <button type="button" key={entry.path} onClick={() => load(entry.path)}><IconFolder /> {entry.name}</button>)}
      </div>
      <div className="settings-note">由 Mod 列出电脑上的文件夹，所以在 Steam 覆盖层、浏览器、平板上都能用。</div>
      <div className="page-actions"><Button className="secondary-button" onClick={onCancel}>取消</Button><Button className="primary-button" disabled={!listing?.path} onClick={() => listing && onPick(listing.path)}>选择这个文件夹</Button></div>
    </div>
  </div>;
}

// ---------------------------------------------------------------------------------------------
// Quality & encoding

const HW_PRESETS: Array<[string, string]> = [["p1", "p1 最快"], ["p2", "p2"], ["p3", "p3"], ["p4", "p4 默认"], ["p5", "p5"], ["p6", "p6"], ["p7", "p7 最慢、最清晰"]];
const CPU_PRESETS = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

function QualityPage({ state, onSettings, locked }: { state: WebUiState; onSettings: SettingsHandler; locked: boolean }) {
  const s = state.settings;
  const codec = s.ChartRenderVideoCodec.toLowerCase();
  const isProRes = codec === "prores";
  const isVvc = codec === "vvc";
  const tier = normalizedTier(s);
  const predicted = predictEncoder(s, state.encoders);
  const family = encoderFamily(predicted);
  const qLabel = family ? QUALITY_LABEL[family] : "—";
  const detailed = s.ChartRenderDetailedEncoderSettings;
  const encoderMode = s.ChartRenderEncoderMode.toLowerCase();
  const device = isProRes || isVvc ? "cpu" : encoderMode === "custom" ? "custom" : encoderMode === "cpu-compatibility" ? "cpu" : "gpu";
  const modeDefaults = encoderPresetDefaults[encoderMode] ?? encoderPresetDefaults["auto-balanced"];
  const hwPreset = s.ChartRenderHardwarePreset.toLowerCase() === "auto" ? modeDefaults.hardware : s.ChartRenderHardwarePreset;
  const cpuPreset = s.ChartRenderCpuPreset.toLowerCase() === "auto" ? modeDefaults.cpu : s.ChartRenderCpuPreset;
  const detected = Object.keys(state.encoders.results).length > 0;
  const candidates = hardwareCandidates(codec);
  const disabledVendors: Record<string, string> = {};
  if (detected && candidates.length > 0) {
    if (!candidates.some((n) => n.endsWith("_nvenc") && state.encoders.results[n])) disabledVendors.nvidia = "这台电脑的 NVIDIA 显卡不支持这个格式，或没有 NVIDIA 显卡";
    if (!candidates.some((n) => n.endsWith("_amf") && state.encoders.results[n])) disabledVendors.amd = "没有检测到可用的 AMD 编码器";
    if (!candidates.some((n) => n.endsWith("_qsv") && state.encoders.results[n])) disabledVendors.intel = "没有检测到可用的 Intel 编码器";
  }
  const fellBack = predicted && device === "gpu" && predicted.startsWith("lib");
  const encoderNote = predicted === null
    ? (state.encoders.status === "detecting" ? "正在检测这台电脑能用的编码器…" : "还没有编码器检测结果。")
    : predicted === "none"
      ? `这台电脑没有可用的 ${isVvc ? "H.266" : "AV1"} 编码器，渲染会失败，请换一种格式。`
      : `这台电脑将使用：${encoderLabels[predicted] ?? predicted}${fellBack ? "。没有可用的显卡编码器，改用 CPU（较慢）" : ""}。`;
  const custom = tier === "custom";
  const tierValue = (id: string) => tierQuality(state, family, id);
  const setDetailed = (value: boolean) => onSettings(value
    ? { ChartRenderDetailedEncoderSettings: true, ChartRenderHardwarePreset: hwPreset, ChartRenderCpuPreset: cpuPreset }
    : { ChartRenderDetailedEncoderSettings: false, ...(encoderMode === "custom" ? { ChartRenderEncoderMode: "auto-balanced" } : {}) });
  const setDevice = (value: string) => onSettings({ ChartRenderEncoderMode: value === "cpu" ? "cpu-compatibility" : value === "custom" ? "custom" : "auto-balanced" });
  // Editing the quality value or bitrate by hand leaves the tiers: the encoder then uses exactly
  // what was entered (custom).
  const currentQ = custom ? s.ChartRenderCrf : tierValue(tier) ?? s.ChartRenderCrf;
  const currentCap = custom ? s.ChartRenderBitrateMbps : tierCap(tier, s);
  return <>
    <LockedNote state={state} />
    <Panel title="视频编码格式">
      <ChipSetting label="格式" value={codec} options={[["h264", "H.264（推荐）"], ["hevc", "HEVC"], ["av1", "AV1"], ["prores", "ProRes"], ["vvc", "H.266（实验性）"]]} onChange={(value) => onSettings({ ChartRenderVideoCodec: value })} disabled={locked} />
      <InfoBlock text={encoderNote} />
      {isVvc ? <div className="settings-note warning-note">H.266 是实验性格式：只能 CPU 编码，1080p60 大约每秒几帧，大多数播放器和网站打不开，仅供尝鲜。</div> : null}
      {isProRes ? <div className="settings-note">ProRes 文件很大，适合导入剪辑软件；会自动使用 MOV，FLAC 会改为 ALAC。</div> : null}
    </Panel>
    <Panel title="画质" hint={renderSeconds(state) != null ? `按当前谱面 ${formatDuration(renderSeconds(state) ?? 0)}${s.ChartRenderUseSelectedRange && state.chart.selectionSeconds > 0 ? "（选中段落）" : ""}估算` : "加载谱面后显示大小估算"}>
      <div className="tier-grid">
        {TIERS.map((t) => {
          const estimate = estimateSize(state, t.id);
          const on = !custom && tier === t.id && !isProRes && !s.ChartRenderConstantBitrate;
          const off = locked || isProRes || s.ChartRenderConstantBitrate;
          const cap = tierCap(t.id, s);
          const value = tierValue(t.id);
          return <button type="button" key={t.id} className={`tier-card ${on ? "is-active" : ""}`} disabled={off} onClick={() => onSettings({ ChartRenderQualityTier: t.id })} title={estimate.kind === "learned" ? "按你上次用这个档位渲染的实际码率估算" : estimate.kind === "guess" ? "首次估算：按平均用到上限的 60% 计算，渲染一次后会按实际结果估算" : undefined}>
            {"recommended" in t && t.recommended ? <span className="tier-badge">推荐</span> : null}
            <strong>{t.name}</strong>
            <span className="tier-use">{t.use}</span>
            {estimate.kind === "unbounded" ? <><span className="tier-size">不限码率</span><span className="tier-param">可能几 GB，取决于画面</span></>
              : estimate.kind === "nochart" ? <span className="tier-param">加载谱面后显示大小</span>
              : <><span className="tier-size">通常约 {formatSize(estimate.typical)}{estimate.kind === "learned" ? " ✓" : ""}</span><span className="tier-param">最多约 {formatSize(estimate.max)}</span></>}
            <span className="tier-param">{qLabel} {value ?? "—"} · 上限 {cap > 0 ? `${cap} Mbps` : "不限"}</span>
          </button>;
        })}
      </div>
      {isProRes ? <div className="settings-note">ProRes 的码率由格式本身决定，画质档位不适用。</div>
        : s.ChartRenderConstantBitrate ? <div className="settings-note">已打开恒定比特率，画质档位不生效（见下方详细编码设置）。</div>
        : <div className="settings-note">“通常约”是估算，粒子多的谱面会更大；“最多约”是上限，画面再复杂也不会超过（超出时画质会稍微下降）。带 ✓ 的是按你上次渲染的实际结果估算。</div>}
      <div className="settings-note">参考：B 站要求 1080p 高于 6 Mbps、4K 高于 20 Mbps；YouTube 推荐 1080p60 12 Mbps、4K60 53–68 Mbps。“标准”档的上限是 YouTube 推荐值的 2 倍，平台转码后仍有余量。</div>
      {sizeWarnings(state).map((w) => <div className="settings-note warning-note" key={w}>{w}</div>)}
      <SettingSwitch label="详细编码设置" description={`编码器、速度预设、${qLabel}、码率、CBR`} value={detailed} onChange={setDetailed} disabled={locked} />
      {detailed ? <div className="settings-section">
        <ChipSetting label="编码器" value={device} options={[["gpu", "显卡"], ["cpu", "CPU"], ["custom", "自定义参数"]]} disabledOptions={isProRes || isVvc ? { gpu: "这个格式只能用 CPU 编码", custom: "这个格式不支持自定义参数" } : detected && candidates.length > 0 && !candidates.some((n) => state.encoders.results[n]) ? { gpu: "没有检测到可用的显卡编码器" } : undefined} onChange={setDevice} disabled={locked} />
        {device === "gpu" && !isProRes && !isVvc ? <>
          <ChipSetting label="硬件编码器" value={s.ChartRenderHardwareEncoder.toLowerCase()} options={[["auto", "自动"], ["nvidia", "NVIDIA"], ["amd", "AMD"], ["intel", "Intel"]]} disabledOptions={disabledVendors} onChange={(value) => onSettings({ ChartRenderHardwareEncoder: value })} disabled={locked} />
          <ChipSetting label="显卡速度预设" value={hwPreset} options={HW_PRESETS} onChange={(value) => onSettings({ ChartRenderHardwarePreset: value })} disabled={locked} />
          {family === "amf" || family === "qsv" ? <div className="settings-note">AMD / Intel 会把 p1–p7 换算成各自的速度档位。</div> : null}
        </> : null}
        {(device === "cpu" || fellBack) && !isProRes ? <>
          <ChipSetting label="CPU 速度预设" value={cpuPreset} options={(isVvc ? ["faster", "fast", "medium", "slow", "slower"] : CPU_PRESETS).map((p) => [p, p] as [string, string])} onChange={(value) => onSettings({ ChartRenderCpuPreset: value })} disabled={locked} />
          {["slow", "slower", "veryslow"].includes(cpuPreset) ? <div className="settings-note warning-note">慢预设画质略好，但每一帧都要等编码器，渲染会明显变慢。</div> : null}
          {codec === "av1" ? <div className="settings-note">AV1 CPU 编码（SVT-AV1）会把这些档位换算成它自己的速度值。</div> : null}
        </> : null}
        {device === "custom" ? <>
          <SettingText label="自定义编码参数" value={s.ChartRenderPreset} onChange={(value) => onSettings({ ChartRenderPreset: value })} disabled={locked} />
          <div className="settings-note">自定义模式只尝试 NVIDIA；AMD / Intel 请用“显卡”。</div>
        </> : null}
        {isProRes ? null : <div className="settings-grid">
          <SettingNumber label={`画质参数（${qLabel}，越小越清晰）${custom ? "" : " · 跟随档位"}`} value={currentQ} min={0} max={63} step={1} onChange={(value) => onSettings({ ChartRenderQualityTier: "custom", ChartRenderCrf: value, ChartRenderBitrateMbps: custom ? s.ChartRenderBitrateMbps : tierCap(tier, s) })} suffix="" disabled={locked || s.ChartRenderConstantBitrate} />
          <SettingNumber label={s.ChartRenderConstantBitrate ? "恒定码率（0 = 按分辨率自动）" : `码率上限${custom ? "（0 = 不限）" : " · 跟随档位"}`} value={currentCap} min={0} max={300} step={1} onChange={(value) => onSettings({ ChartRenderQualityTier: "custom", ChartRenderBitrateMbps: value, ChartRenderCrf: currentQ })} suffix="Mbps" disabled={locked} />
        </div>}
        {custom && !isProRes ? <div className="settings-note">已手动修改画质参数，没有选中档位。码率填 0 时只按画质编码、没有上限（1.1.1 的方式），文件可能很大。<button type="button" className="link-button" disabled={locked} onClick={() => onSettings({ ChartRenderQualityTier: "standard" })}>恢复为“标准”档</button></div> : null}
        <SettingSwitch label="恒定比特率（CBR）" description={isProRes ? "ProRes 的码率由格式决定" : isVvc ? "H.266 不支持，会用可变码率" : "打开后按上面的码率恒定输出，不再看画质参数"} value={s.ChartRenderConstantBitrate} onChange={(value) => onSettings({ ChartRenderConstantBitrate: value })} disabled={locked || isProRes || isVvc} />
      </div> : null}
    </Panel>
  </>;
}

// ---------------------------------------------------------------------------------------------
// Capture & audio

function CapturePage({ state, onSettings, locked }: { state: WebUiState; onSettings: SettingsHandler; locked: boolean }) {
  const s = state.settings;
  const camera = s.ChartRenderCaptureSource.toLowerCase() !== "game-view";
  return <>
    <LockedNote state={state} />
    <Panel title="画面捕获">
      <ChipSetting label="捕获方式" value={camera ? "camera" : "game-view"} options={[["camera", "摄像机渲染（只录谱面，推荐）"], ["game-view", "游戏画面渲染（包含界面）"]]} onChange={(value) => onSettings({ ChartRenderCaptureSource: value })} disabled={locked} />
      {camera ? null : <>
        <ChipSetting label="游戏画面后端" value={s.ChartRenderGameViewBackend} options={[["offscreen", "离屏渲染（支持 2K/4K）"], ["screen", "最终屏幕捕获（分辨率跟随窗口）"]]} onChange={(value) => onSettings({ ChartRenderGameViewBackend: value })} disabled={locked} />
        <InfoBlock text={s.ChartRenderGameViewBackend === "screen" ? "最终屏幕捕获直接截取游戏最终画面，兼容性最好，但输出分辨率跟随游戏窗口。" : "离屏渲染在后台按设置的分辨率渲染完整画面（包括界面），可以超过显示器分辨率；屏幕上只显示预览。宽高比建议和游戏窗口一致。"} />
      </>}
      <SettingSwitch label="渲染时显示判定文字" description="Perfect、Early、Late 等" value={s.ChartRenderShowHitJudgments} onChange={(value) => onSettings({ ChartRenderShowHitJudgments: value })} disabled={locked} />
      <ChipSetting label="渲染预览" description="渲染时屏幕上显示的画面" value={s.ChartRenderPreviewMode.toLowerCase()} options={[["full", "完整"], ["dim", "暗色"], ["minimal", "极简（略快）"]]} onChange={(value) => onSettings({ ChartRenderPreviewMode: value })} disabled={locked} />
    </Panel>
    <Panel title="按键显示 Mod">
      <SettingSwitch label="兼容其他按键显示 Mod（实验性）" description={camera ? "摄像机渲染录不到按键显示，改用游戏画面渲染才会生效。" : "JRP、JKV、Quartz 的雨键、KPS 和按键动画跟随视频时间（CheryTools 自带适配）。出现画面异常时可以关闭。"} value={s.ChartRenderCompatKeyViewers} onChange={(value) => onSettings({ ChartRenderCompatKeyViewers: value })} disabled={locked || camera} />
    </Panel>
    <Panel title="音频">
      <div className="settings-grid">
        <SettingNumber label="音频同步偏移（正数让声音更晚）" value={s.ChartRenderAudioSyncOffsetMs} min={-5000} max={5000} step={1} onChange={(value) => onSettings({ ChartRenderAudioSyncOffsetMs: value })} suffix="毫秒" disabled={locked} />
        <SettingSelect label="回读格式" group="ChartRenderCaptureFormat" value={s.ChartRenderCaptureFormat.toLowerCase()} options={["rgba", "bgra"]} onChange={(value) => onSettings({ ChartRenderCaptureFormat: value })} disabled={locked} />
      </div>
    </Panel>
  </>;
}

// ---------------------------------------------------------------------------------------------
// Web page & connection

function QrCode({ text }: { text: string }) {
  const svg = React.useMemo(() => {
    const qr = qrcode(0, "M");
    qr.addData(text);
    qr.make();
    return qr.createSvgTag({ cellSize: 4, margin: 2, scalable: true });
  }, [text]);
  return <div className="qr-code" dangerouslySetInnerHTML={{ __html: svg }} />;
}

function ConnectPage({ state, onSettings, demo, onAction }: { state: WebUiState; onSettings: SettingsHandler; demo: boolean; onAction: (path: string, body?: unknown, success?: string) => Promise<unknown> }) {
  const s = state.settings;
  const [listening, setListening] = React.useState(false);
  const pairUrl = state.server.lanUrls[0] ?? "";
  const viewUrl = state.server.lanViewUrls?.[0] ?? "";
  React.useEffect(() => {
    if (!listening) return;
    const onKey = (event: KeyboardEvent) => {
      event.preventDefault();
      if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
      setListening(false);
      if (event.key === "Escape") return;
      const key = event.key.length === 1 ? event.key.toUpperCase() : event.key;
      const combo = `${event.ctrlKey ? "Ctrl+" : ""}${event.shiftKey ? "Shift+" : ""}${event.altKey ? "Alt+" : ""}${key}`;
      if (combo === "Ctrl+Shift+E") Message.warning("Ctrl+Shift+E 和编辑器自带的快捷键冲突，建议换一个。");
      onSettings({ WebUiOpenHotkey: combo });
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [listening, onSettings]);
  const copy = (text: string, done: string) => {
    void navigator.clipboard?.writeText(text).then(() => Message.success(done), () => Message.info(text));
  };
  return <div className="two-column">
    <Panel title="打开方式">
      <div className="setting-row"><div><strong>打开设置页快捷键</strong><span>点一下，再按新的按键（Esc 取消）</span></div><button type="button" className={`hotkey-button ${listening ? "is-listening" : ""}`} onClick={() => setListening(true)}>{listening ? "请按下按键…" : s.WebUiOpenHotkey}</button></div>
      <ChipSetting label="打开方式" value={s.WebUiOpenMode} options={[["auto", "自动"], ["steam", "Steam 覆盖层"], ["browser", "系统浏览器"]]} onChange={(value) => onSettings({ WebUiOpenMode: value })} />
      <div className="settings-note">自动：Steam 在运行且覆盖层开着时用覆盖层，否则用系统浏览器。</div>
    </Panel>
    <Panel title="手机 / 平板访问">
      <SettingSwitch label="允许局域网访问" description="同一个 Wi-Fi 下的手机、平板可以打开这个页面" value={s.WebUiAllowLan} onChange={(value) => onSettings({ WebUiAllowLan: value })} />
      {s.WebUiAllowLan ? <>
        {pairUrl ? <div className="qr-row">
          <QrCode text={pairUrl} />
          <div>
            <strong>用平板扫码打开一次</strong>
            <span>之后这台设备会被记住，<b>存成书签</b>就能直接打开，不用每次复制链接。</span>
            <code>{pairUrl.replace(/\?token=.*$/, "")}</code>
            <div className="button-row">
              <Button className="secondary-button" icon={<IconCopy />} disabled={!viewUrl} onClick={() => copy(viewUrl, "已复制只读链接：打开后只能看进度，不能开始、取消或改设置。")}>复制只读链接</Button>
              <Button className="danger-outline" onClick={() => demo ? Message.info("演示状态：会让所有设备和只读链接失效。") : onAction("/api/access/forget", undefined, "已让所有设备失效，需要重新扫码")}>忘记所有设备</Button>
            </div>
          </div>
        </div> : <InfoBlock text="正在启动局域网服务。如果手机打不开，请在 Windows 防火墙中允许游戏访问网络。" />}
        <div className="settings-grid">
          <SettingNumber label="局域网端口" value={s.WebUiLanPort} min={1024} max={65535} step={1} onChange={(value) => onSettings({ WebUiLanPort: value })} suffix="" />
        </div>
        <div className="settings-note">端口固定，书签才不会失效；建议在路由器里给电脑固定 IP。被占用时会自动换成附近的端口。{state.server.lanPort && state.server.lanPort !== s.WebUiLanPort ? `（端口 ${s.WebUiLanPort} 被占用，当前用的是 ${state.server.lanPort}）` : ""}</div>
        <div className="settings-note">需要在外网查看时，用 Tailscale / ZeroTier 等组网工具，不要把端口直接暴露到公网。</div>
      </> : <div className="settings-note">关闭时只有这台电脑能打开设置页。</div>}
    </Panel>
  </div>;
}

// ---------------------------------------------------------------------------------------------
// Tools & diagnostics

function ToolsPage({ state, onSettings, locked, demo, onAction, onOpenDocument }: { state: WebUiState; onSettings: SettingsHandler; locked: boolean; demo: boolean; onAction: <T extends object>(path: string, body?: unknown, success?: string) => Promise<T | null>; onOpenDocument: (path: string) => void }) {
  const [exported, setExported] = React.useState<{ file: string; bytes: number } | null>(null);
  const [exporting, setExporting] = React.useState(false);
  const [importing, setImporting] = React.useState(false);
  const [importText, setImportText] = React.useState("");
  const ws = state.workspace;
  const exportDiagnostics = async () => {
    if (demo) {
      setExported({ file: "AETC-诊断包-20261122-183000.zip", bytes: 2.4 * 1024 * 1024 });
      return;
    }
    setExporting(true);
    const result = await onAction<{ file: string; bytes: number }>("/api/diagnostics/export", undefined, "诊断包已导出");
    setExporting(false);
    if (result?.file) setExported({ file: result.file, bytes: result.bytes });
  };
  const shareCode = () => {
    const data: Record<string, unknown> = { v: 1 };
    SHARE_KEYS.forEach((key) => { data[key] = state.settings[key]; });
    return `AETC1:${btoa(unescape(encodeURIComponent(JSON.stringify(data))))}`;
  };
  const importCode = () => {
    try {
      const raw = importText.trim();
      if (!raw.startsWith("AETC1:")) throw new Error("这不是 AETC 分享码");
      const data = JSON.parse(decodeURIComponent(escape(atob(raw.slice(6))))) as Record<string, unknown>;
      const changes: Partial<SettingsState> = {};
      SHARE_KEYS.forEach((key) => { if (key in data) (changes as Record<string, unknown>)[key] = data[key]; });
      onSettings(changes);
      setImporting(false);
      Message.success(`已导入 ${Object.keys(changes).length} 项渲染设置`);
    } catch (error) {
      Message.error(`导入失败：${error instanceof Error ? error.message : "格式不对"}`);
    }
  };
  return <>
    <Panel title="遇到问题">
      <div className="tool-row"><IconExport /><div><strong>导出诊断包</strong><span>打包最近的渲染日志、游戏日志、电脑和编码器信息、日志分析指南。整包发给 AI 或开发者即可；令牌和用户名已去掉。</span></div><Button className="primary-button" loading={exporting} onClick={exportDiagnostics}>导出</Button></div>
      {exported ? <div className="info-block"><IconCheckCircle /><span>已导出 {exported.file}（{formatBytes(exported.bytes)}）。可以直接发给 AI，并说一句“请按诊断包里的《日志分析指南》分析问题”。<span className="button-row">{demo ? null : <a className="link-button" href={diagnosticsDownloadUrl(exported.file)}>下载到这台设备</a>}<button type="button" className="link-button" onClick={() => demo ? Message.info("演示状态：会打开诊断包所在文件夹。") : onAction("/api/folders/open", { target: "diagnostics" }, "")}>打开所在文件夹</button></span></span></div> : null}
      <SettingSwitch label="诊断模式" description="日志记录得更详细，渲染完保留临时文件方便检查。排查问题时再打开，平时关着。" value={state.settings.ChartRenderDiagnosticMode} onChange={(value) => onSettings({ ChartRenderDiagnosticMode: value })} disabled={locked} />
    </Panel>
    <Panel title="空间">
      <div className="tool-row"><IconStorage /><div><strong>工作区</strong><span>临时文件 {ws ? formatBytes(ws.tempBytes) : "—"}{ws && ws.tempFolders > 0 ? `（${ws.tempFolders} 个文件夹）` : ""} · 日志 {ws ? `${ws.logFiles} 份 ${formatBytes(ws.logBytes)}` : "—"}</span></div><Button className="secondary-button" disabled={locked || !ws || ws.tempFolders === 0} onClick={() => onAction("/api/workspace/clean", undefined, "临时文件已清理")}>清理临时文件</Button></div>
    </Panel>
    <Panel title="预设与分享">
      {RENDER_PRESETS.map((preset) => <div className="tool-row" key={preset.id}><IconSettings /><div><strong>{preset.name}</strong><span>{preset.detail}</span></div><Button className="secondary-button" disabled={locked} onClick={() => { onSettings(preset.changes); Message.success(`已应用预设：${preset.name}`); }}>应用</Button></div>)}
      <div className="tool-row"><IconLink /><div><strong>分享设置</strong><span>只包含渲染设置，不含路径、快捷键和设备信息。</span></div><div className="button-row"><Button className="secondary-button" onClick={() => { void navigator.clipboard?.writeText(shareCode()).then(() => Message.success("分享码已复制"), () => Message.info(shareCode())); }}>复制分享码</Button><Button className="secondary-button" disabled={locked} onClick={() => setImporting(true)}>导入</Button></div></div>
      {importing ? <div className="import-box"><textarea value={importText} onChange={(event) => setImportText(event.target.value)} placeholder="粘贴别人发给你的分享码（以 AETC1: 开头）" /><div className="button-row"><Button className="secondary-button" onClick={() => setImporting(false)}>取消</Button><Button className="primary-button" onClick={importCode}>导入</Button></div></div> : null}
      <div className="tool-row"><IconUndo /><div><strong>恢复默认</strong><span>把所有设置恢复成默认值（已记住的设备不受影响）。</span></div><Button className="danger-outline" disabled={locked} onClick={() => { if (window.confirm("把所有设置恢复成默认值？")) void onAction("/api/settings/reset", undefined, "已恢复默认设置"); }}>恢复默认</Button></div>
    </Panel>
    <Panel title="帮助">
      <div className="tool-row"><IconFile /><div><strong>使用手册</strong><span>在当前 Web 设置服务中打开。</span></div><Button className="secondary-button" onClick={() => onOpenDocument("/docs/manual")}>打开</Button></div>
      <div className="tool-row"><IconImage /><div><strong>FFmpeg 参数参考</strong><span>视频、音频和合成参数说明。</span></div><Button className="secondary-button" onClick={() => onOpenDocument("/docs/ffmpeg")}>打开</Button></div>
    </Panel>
  </>;
}

// ---------------------------------------------------------------------------------------------
// Notices

function UpdateNotice({ update }: { update: UpdateState }) {
  const open = (url: string) => window.open(url, "_blank", "noopener");
  return (
    <div className="update-notice" role="status">
      <IconCheckCircle />
      <div className="update-notice-body">
        <strong>发现新版本 {update.latest}</strong>
        <span>当前版本 {update.current}。推荐用 Aurora 更新，也可以在资源站或 GitHub 下载新包；更新由 UMM / Aurora 完成。</span>
      </div>
      <div className="update-notice-actions">
        <button type="button" onClick={() => open(update.auroraUrl)}>在 Aurora 中更新</button>
        <button type="button" className="is-secondary" onClick={() => open(update.url)}>打开下载页</button>
      </div>
    </div>
  );
}

function FfmpegNotice({ ffmpeg, disabled, countdown, onCancelAuto, onInstall, onInstallFull }: { ffmpeg: FfmpegState; disabled: boolean; countdown: number | null; onCancelAuto: () => void; onInstall: () => void; onInstallFull: () => void }) {
  const percent = Math.round(Math.max(0, Math.min(1, ffmpeg.progress)) * 100);
  const megabytes = (bytes: number) => (bytes / (1024 * 1024)).toFixed(1);
  const busy = ffmpeg.status === "downloading" || ffmpeg.status === "verifying" || ffmpeg.status === "installing";
  const failed = ffmpeg.status === "failed";
  const counting = countdown != null && !busy && !failed;
  const title = failed ? "FFmpeg 自动下载失败" : busy ? "正在准备 FFmpeg" : counting ? "即将自动下载 FFmpeg" : "需要安装 FFmpeg";
  const detail = failed
    ? (ffmpeg.message || "请检查网络后重试，或手动安装 FFmpeg。")
    : busy
      ? `${ffmpeg.status === "installing" ? "正在安装" : ffmpeg.status === "verifying" ? "正在校验" : "正在下载"} ${percent}%（${megabytes(ffmpeg.downloadedBytes)} / ${megabytes(ffmpeg.totalBytes)} MB，安装后约 ${megabytes(ffmpeg.installedBytes)} MB，来源 ${ffmpeg.source || "准备中"}）`
      : counting
        ? `${countdown} 秒后自动下载定制版（下载约 12.7 MB，安装后约 44.2 MB）。不需要的话可以取消；也可以直接选完整版（下载约 84.0 MB，安装后约 217.3 MB）。`
        : `需要安装 FFmpeg。定制版：下载约 12.7 MB，安装后约 44.2 MB；完整版：下载约 84.0 MB，安装后约 217.3 MB。游戏启动后会自动下载定制版；也可以现在手动选择。`;
  return (
    <div className={`ffmpeg-notice ${failed ? "is-error" : busy ? "is-busy" : ""}`} role="status">
      <IconDown />
      <div className="ffmpeg-notice-body">
        <strong>{title}</strong>
        <span>{detail}</span>
        {busy ? <div className="ffmpeg-progress"><div style={{ width: `${percent}%` }} /></div> : null}
      </div>
      {!busy ? (
        <div className="ffmpeg-notice-actions">
          <button type="button" onClick={onInstall} disabled={disabled}>{failed ? "重试定制版" : counting ? "立即下载" : "下载定制版"}</button>
          {counting ? (
            <button type="button" className="is-secondary" onClick={onCancelAuto} disabled={disabled}>取消自动下载</button>
          ) : (
            <button type="button" className="is-secondary" onClick={onInstallFull} disabled={disabled}>下载完整版</button>
          )}
        </div>
      ) : null}
    </div>
  );
}

function getRenderResult(render: RenderState): { kind: "success" | "failed" | "canceled"; title: string; detail: string } | null {
  if (render.active) return null;
  if (render.state === "Completed") return { kind: "success", title: "渲染已完成", detail: "视频已成功导出。" };
  if (render.state === "Failed") return { kind: "failed", title: "渲染失败", detail: render.message || render.progress.detail || "渲染任务失败。可以在“工具与诊断”导出诊断包。" };
  if (render.state === "Canceled") return { kind: "canceled", title: "渲染已取消", detail: render.message || "渲染任务已取消。" };
  return null;
}

// The demo page simulates a render so the layout can be checked without the game.
function runDemoRender(action: "start" | "cancel", setState: React.Dispatch<React.SetStateAction<WebUiState>>) {
  const w = window as unknown as { __aetcDemoTimer?: number };
  if (w.__aetcDemoTimer) window.clearInterval(w.__aetcDemoTimer);
  if (action === "cancel") {
    setState((current) => ({ ...current, render: { ...current.render, active: false, cancelRequested: false, state: "Canceled", message: "演示渲染已取消", progress: { ...current.render.progress, value: 0 } } }));
    return;
  }
  const total = 60 * 197;
  let value = 0;
  setState((current) => ({ ...current, render: { ...current.render, active: true, state: "Rendering", cancelRequested: false, message: "", tempVideoBytes: 0, progress: { ...current.render.progress, value: 0, totalFrames: total, writtenFrames: 0, stage: "捕获画面", detail: "游戏正在逐帧播放谱面，画面和声音同时录制。", encoderName: "h264_nvenc p4" } } }));
  w.__aetcDemoTimer = window.setInterval(() => {
    value = Math.min(1, value + 0.004);
    const frames = Math.round(total * Math.min(value, 0.9) / 0.9);
    const finishing = value >= 0.9;
    setState((current) => ({
      ...current,
      render: {
        ...current.render,
        active: value < 1,
        state: value < 1 ? (finishing ? "Finalizing" : "Rendering") : "Completed",
        writingOutput: finishing && value < 1,
        tempVideoBytes: frames * 52000,
        progress: { ...current.render.progress, value, writtenFrames: frames, processingFramesPerSecond: 42 + Math.random() * 3, estimatedRemaining: `00:00:${String(Math.max(0, Math.round((1 - value) * 60))).padStart(2, "0")}`, stage: value < 1 ? (value < 0.9 ? "捕获画面" : value < 0.92 ? "完成编码" : "合成音频") : "渲染完成", detail: value < 1 ? (finishing ? "正在把画面和声音合成为最终视频文件，请不要关闭游戏。" : "游戏正在逐帧播放谱面，画面和声音同时录制。") : "视频已成功导出。" },
      },
    }));
    if (value >= 1 && w.__aetcDemoTimer) window.clearInterval(w.__aetcDemoTimer);
  }, 50);
}

function PresetSetting({ label, options, active, onSelect, disabled }: { label: string; options: string[]; active: string; onSelect: (value: string) => void; disabled?: boolean }) {
  return <div className="setting-row preset-setting"><div><strong>{label}</strong></div><div className="preset-options">{options.map((option) => <button className={`preset-button ${active === option ? "is-active" : ""}`} key={option} type="button" disabled={disabled} onClick={() => onSelect(option)}>{option}</button>)}</div></div>;
}

function DisclosureButton({ label, open, onClick, disabled }: { label: string; open: boolean; onClick: () => void; disabled?: boolean }) {
  return <button className={`disclosure-button ${open ? "is-open" : ""}`} type="button" disabled={disabled} onClick={onClick}><span>{label}</span><IconDown /></button>;
}

function PatchRow({ patch }: { patch: PatchStatus }) {
  const stateName = patch.state === "active" ? "active" : patch.state === "failed" ? "failed" : "inactive";
  const icon = patch.name.includes("谱面") ? <IconImage /> : <IconTool />;
  return <div className="patch-row"><span className={`patch-icon ${stateName}`}>{icon}</span><div className="patch-name"><strong>{patch.name}</strong><span>{patchDescriptions[patch.name] ?? patch.description}</span></div><span className={`patch-state ${patch.state}`}><span className="patch-state-dot" />{statusLabel[patch.state] ?? patch.state}</span></div>;
}

function PageHeading({ title, description }: { title: string; description: string }) {
  return <div className="page-heading"><div><h1>{title}</h1><p>{description}</p></div><span className="page-rule" /></div>;
}

function SectionHeading({ title, hint }: { title: string; hint?: string }) {
  return <div className="section-heading"><h2>{title}</h2>{hint ? <span>{hint}</span> : null}</div>;
}

function SettingSwitch({ label, description, value, onChange, disabled }: { label: string; description?: string; value: boolean; onChange: (value: boolean) => void; disabled?: boolean }) {
  return <div className="setting-row"><div><strong>{label}</strong>{description ? <span>{description}</span> : null}</div><Switch checked={value} disabled={disabled} onChange={onChange} /></div>;
}

function SettingNumber({ label, value, min, max, step, suffix, onChange, disabled }: { label: string; value: number; min: number; max?: number; step: number; suffix: string; onChange: (value: number) => void; disabled?: boolean }) {
  const [draft, setDraft] = React.useState(String(value));

  React.useEffect(() => {
    setDraft(String(value));
  }, [value]);

  const commit = () => {
    if (disabled) {
      setDraft(String(value));
      return;
    }

    if (draft.trim() === "") {
      setDraft(String(value));
      return;
    }

    const next = Number(draft);
    if (Number.isFinite(next)) {
      if (next !== value) onChange(next);
    } else {
      setDraft(String(value));
    }
  };

  return <div className="setting-row"><div><strong>{label}</strong></div><div className="number-control"><input className="number-input" type="number" value={draft} min={min} max={max} step={step} disabled={disabled} onChange={(event) => setDraft(event.target.value)} onBlur={commit} onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur(); }} /><span>{suffix}</span></div></div>;
}

function SettingText({ label, value, onChange, disabled }: { label: string; value: string; onChange: (value: string) => void; disabled?: boolean }) {
  const [draft, setDraft] = React.useState(value);

  React.useEffect(() => {
    setDraft(value);
  }, [value]);

  const commit = () => {
    if (disabled) {
      setDraft(value);
      return;
    }

    if (draft !== value) onChange(draft);
  };

  return <div className="setting-row"><div><strong>{label}</strong></div><input className="text-input" value={draft} disabled={disabled} onChange={(event) => setDraft(event.target.value)} onBlur={commit} onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur(); }} /></div>;
}

function SettingSelect({ label, group, value, options, onChange, disabled, disabledOptions }: { label: string; group?: string; value: string; options: string[]; onChange: (value: string) => void; disabled?: boolean; disabledOptions?: Record<string, string> }) {
  return <div className="setting-row"><div><strong>{label}</strong></div><CustomSelect group={group} value={value} options={options} onChange={onChange} disabled={disabled} disabledOptions={disabledOptions} /> </div>;
}

// disabledOptions maps an option that cannot be chosen right now to the reason, shown on hover.
function CustomSelect({ group, value, options, onChange, disabled, disabledOptions }: { group?: string; value: string; options: string[]; onChange: (value: string) => void; disabled?: boolean; disabledOptions?: Record<string, string> }) {
  const [open, setOpen] = React.useState(false);
  const [openUp, setOpenUp] = React.useState(false);
  const controlRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const closeOnOutsideClick = (event: MouseEvent) => {
      const target = event.target as Node;
      if (controlRef.current && !controlRef.current.contains(target) && !menuRef.current?.contains(target)) setOpen(false);
    };
    document.addEventListener("mousedown", closeOnOutsideClick);
    return () => document.removeEventListener("mousedown", closeOnOutsideClick);
  }, [open]);

  const toggle = () => {
    if (!open) {
      const rect = controlRef.current?.getBoundingClientRect();
      const estimatedHeight = options.length * 40 + 12;
      // Open upward when the menu would run into the status bar. Keeping the menu inside the
      // field also avoids the Steam overlay misplacing position:fixed portals.
      setOpenUp(Boolean(rect && rect.bottom + estimatedHeight > window.innerHeight - 72));
    }
    setOpen((current) => !current);
  };

  const menu = open ? (
    <div
      ref={menuRef}
      className="select-menu"
      role="listbox"
      style={openUp ? { top: "auto", bottom: "calc(100% + 6px)" } : undefined}
    >
      {options.map((option) => {
        const disabledReason = disabledOptions?.[option];
        return <button className={`select-option ${option === value ? "is-selected" : ""}`} key={option} type="button" role="option" aria-selected={option === value} aria-disabled={disabledReason ? true : undefined} disabled={Boolean(disabledReason)} title={disabledReason} onClick={() => { onChange(option); setOpen(false); }}><span className="select-option-check">{option === value ? <IconCheck /> : null}</span><span>{labelFor(group ?? "", option)}</span></button>;
      })}
    </div>
  ) : null;

  return <div className="select-field"><button ref={controlRef} className={`select-control ${open ? "is-open" : ""}`} type="button" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} onClick={toggle}><span className="select-value">{labelFor(group ?? "", value)}</span><IconDown className="select-chevron" /></button>{menu}</div>;
}

function InfoBlock({ text }: { text: string }) { return <div className="info-block"><IconInfoCircle /><span>{text}</span></div>; }

const encoderLabels: Record<string, string> = {
  h264_nvenc: "H.264 显卡编码（NVIDIA）",
  hevc_nvenc: "HEVC 显卡编码（NVIDIA）",
  av1_nvenc: "AV1 显卡编码（NVIDIA，需要 RTX 40 系或更新）",
  h264_amf: "H.264 显卡编码（AMD AMF）",
  hevc_amf: "HEVC 显卡编码（AMD AMF）",
  av1_amf: "AV1 显卡编码（AMD AMF，需要 RDNA 3 或更新）",
  h264_qsv: "H.264 显卡编码（Intel QSV）",
  hevc_qsv: "HEVC 显卡编码（Intel QSV）",
  av1_qsv: "AV1 显卡编码（Intel QSV，需要 Arc 或更新）",
  libx264: "H.264 CPU 编码（x264）",
  libx265: "HEVC CPU 编码（x265）",
  libsvtav1: "AV1 CPU 编码（SVT-AV1，较快）",
  "libaom-av1": "AV1 CPU 编码（libaom，很慢）",
  libvvenc: "H.266/VVC CPU 编码（VVenC，实验性，很慢）",
  prores_aw: "ProRes 422 HQ",
};
const encoderOrder = ["h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_amf", "hevc_amf", "av1_amf", "h264_qsv", "hevc_qsv", "av1_qsv", "libx264", "libx265", "libsvtav1", "libaom-av1", "libvvenc", "prores_aw"];

function hardwareCandidates(codec: string): string[] {
  switch (codec) {
    case "h264": return ["h264_nvenc", "h264_amf", "h264_qsv"];
    case "hevc": return ["hevc_nvenc", "hevc_amf", "hevc_qsv"];
    case "av1": return ["av1_nvenc", "av1_amf", "av1_qsv"];
    default: return [];
  }
}

function hardwareSuffix(vendor: string): string {
  switch (vendor) {
    case "nvidia": return "_nvenc";
    case "amd": return "_amf";
    case "intel": return "_qsv";
    default: return "";
  }
}

// The encoder FfmpegEncoder will pick for these settings on this computer (same order of fallbacks),
// or null when nothing is detected yet. "none" when no AV1/VVC encoder works at all.
function predictEncoder(settings: SettingsState, encoders: EncoderState): string | null {
  const results = encoders.results;
  if (Object.keys(results).length === 0) return null;
  const mode = settings.ChartRenderEncoderMode.toLowerCase();
  const forceCpu = mode === "cpu-compatibility";
  if (mode === "custom") {
    const preset = settings.ChartRenderPreset.trim().toLowerCase();
    const customCpu = preset === "cpu" || preset === "x264" || preset.startsWith("x264:");
    return !customCpu && results.h264_nvenc ? "h264_nvenc" : "libx264";
  }

  const codec = settings.ChartRenderVideoCodec.toLowerCase();
  if (codec === "prores") return "prores_aw";
  if (codec === "vvc") return results.libvvenc ? "libvvenc" : "none";

  const candidates = hardwareCandidates(codec);
  if (!forceCpu) {
    const vendor = settings.ChartRenderHardwareEncoder.toLowerCase();
    if (vendor !== "auto") {
      const suffix = hardwareSuffix(vendor);
      const match = candidates.find((name) => name.endsWith(suffix) && results[name]);
      if (match) return match;
    } else {
      const match = candidates.find((name) => results[name]);
      if (match) return match;
    }
  }

  if (codec === "av1") return results.libsvtav1 ? "libsvtav1" : results["libaom-av1"] ? "libaom-av1" : "none";
  if (codec === "hevc") return "libx265";
  return "libx264";
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) { return <div className="metric"><span className="metric-icon">{icon}</span><span><small>{label}</small><strong>{value}</strong></span></div>; }
function DetailLine({ label, value }: { label: string; value: string }) { return <div className="detail-line"><span>{label}</span><strong title={value}>{value}</strong></div>; }

export default App;
