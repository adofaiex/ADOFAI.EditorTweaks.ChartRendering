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
  IconImage,
  IconInfoCircle,
  IconPlayArrow,
  IconRefresh,
  IconSettings,
  IconTool,
  IconUndo,
  IconCloseCircle,
  IconDown,
} from "@arco-design/web-react/icon";
import React from "react";
import { createPortal } from "react-dom";
import { createEventSource, createMockState, isLocalPreview, localDocumentUrl, postJson } from "./api";
import type { EncoderState, PageKey, PatchStatus, RenderState, SettingsState, WebUiState } from "./types";

const pageItems: Array<{ key: PageKey; label: string; description: string; icon: React.ReactNode }> = [
  { key: "overview", label: "总览", description: "兼容性与全部功能状态", icon: <IconApps /> },
  { key: "render", label: "谱面渲染", description: "视频输出与实时进度", icon: <IconImage /> },
  { key: "tools", label: "工具", description: "帮助、日志与诊断", icon: <IconSettings /> },
];

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
  ChartRenderEncoderMode: {
    "auto-balanced": "自动均衡",
    fastest: "最快",
    balanced: "均衡",
    quality: "质量",
    "cpu-compatibility": "CPU 兼容",
    custom: "自定义",
    AutoBalanced: "自动均衡",
    Fastest: "最快",
    Balanced: "均衡",
    Quality: "质量",
    CpuCompatibility: "CPU 兼容",
    Custom: "自定义",
  },
  ChartRenderCaptureFormat: { rgba: "RGBA", bgra: "BGRA", RGBA: "RGBA", BGRA: "BGRA" },
  ChartRenderCaptureSource: {
    camera: "摄像机渲染（推荐）",
    "game-view": "游戏画面渲染（包含界面）",
    Camera: "摄像机渲染（推荐）",
    GameView: "游戏画面渲染（包含界面）",
  },
  ChartRenderPreviewMode: { full: "完整", dim: "暗色", minimal: "极简", Full: "完整", Dim: "暗色", Minimal: "极简" },
  ChartRenderAudioFormat: { aac: "AAC", flac: "FLAC（无损）", alac: "ALAC（无损）", AAC: "AAC", FLAC: "FLAC（无损）", ALAC: "ALAC（无损）" },
  ChartRenderVideoFormat: {
    mp4: "MP4",
    mkv: "MKV（Matroska）",
    mov: "MOV（QuickTime）",
    MP4: "MP4",
    MKV: "MKV（Matroska）",
    MOV: "MOV（QuickTime）",
  },
  ChartRenderVideoCodec: {
    h264: "H.264（兼容性最好，推荐）",
    hevc: "HEVC / H.265（体积更小）",
    av1: "AV1（体积最小，编码较慢）",
    vvc: "H.266 / VVC（实验性，不推荐，编码极慢）",
    prores: "ProRes 422 HQ（剪辑用，体积很大）",
  },
  ChartRenderEncoderDevice: {
    gpu: "显卡（NVIDIA / AMD / Intel）",
    cpu: "CPU",
    custom: "自定义参数",
  },
  ChartRenderHardwareEncoder: {
    auto: "自动（优先 NVENC → AMF → QSV）",
    nvidia: "NVIDIA NVENC",
    intel: "Intel QSV",
    amd: "AMD AMF",
  },
  ChartRenderHardwarePreset: {
    auto: "跟随编码档位",
    p1: "p1（最快）",
    p2: "p2",
    p3: "p3",
    p4: "p4（均衡）",
    p5: "p5",
    p6: "p6",
    p7: "p7（质量最好，最慢）",
  },
  ChartRenderCpuPreset: {
    auto: "跟随编码档位",
    ultrafast: "ultrafast（最快）",
    superfast: "superfast",
    veryfast: "veryfast",
    faster: "faster",
    fast: "fast",
    medium: "medium",
    slow: "slow",
    slower: "slower",
    veryslow: "veryslow（质量最好，最慢）",
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
    notes.push(codec === "prores"
      ? "ProRes 不支持恒定比特率，已关闭 CBR。"
      : "H.266/VVC 不支持恒定比特率，已关闭 CBR。");
  }

  const format = (changes.ChartRenderVideoFormat ?? next.ChartRenderVideoFormat).toLowerCase();
  if (format === "mov" && next.ChartRenderAudioFormat.toLowerCase() === "flac") {
    changes.ChartRenderAudioFormat = "alac";
    notes.push(`${movNoFlac}，音频格式已改为 ALAC（同为无损，音质不变）。`);
  }

  return { changes, notes };
}

function App() {
  const [state, setState] = React.useState<WebUiState>(() => createMockState());
  const [page, setPage] = React.useState<PageKey>("overview");
  const [loading, setLoading] = React.useState(true);
  const [connected, setConnected] = React.useState(false);
  const [reconnecting, setReconnecting] = React.useState(false);
  const [connectionReload, setConnectionReload] = React.useState(0);
  // Set once the mod has answered, and whether a render was running in the last state it sent: a
  // drop after that means the game was closed or crashed, possibly in the middle of a render.
  const [everConnected, setEverConnected] = React.useState(false);
  const [lostDuringRender, setLostDuringRender] = React.useState(false);
  const lastRenderActive = React.useRef(false);
  const localPreview = isLocalPreview();
  const [demoMode, setDemoMode] = React.useState(localPreview);
  const renderActionInFlight = React.useRef(false);

  React.useEffect(() => {
    let disposed = false;
    let events: EventSource | undefined;
    let retryTimer: number | undefined;

    const scheduleReconnect = () => {
      if (disposed || localPreview || retryTimer !== undefined) return;
      setConnected(false);
      setReconnecting(true);
      retryTimer = window.setTimeout(() => {
        retryTimer = undefined;
        void connect();
      }, 2000);
    };

    const connect = () => {
      if (disposed || localPreview) return;
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
        setDemoMode(false);
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

    if (localPreview) {
      setLoading(false);
      setConnected(false);
      setReconnecting(false);
      setDemoMode(true);
    } else {
      setDemoMode(false);
      connect();
    }

    return () => {
      disposed = true;
      events?.close();
      if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    };
  }, [connectionReload, localPreview]);

  React.useEffect(() => {
    const preventBrowserSave = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
        event.preventDefault();
        event.stopPropagation();
        Message.info(localPreview ? "本地预览不需要保存网页。" : "设置会自动保存，不需要保存网页。");
      }
    };

    window.addEventListener("keydown", preventBrowserSave);
    return () => window.removeEventListener("keydown", preventBrowserSave);
  }, [localPreview]);

  const updateSettings = async (requested: Partial<SettingsState>) => {
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
      Message.success("演示状态：设置已更新");
      announce();
      return;
    }

    try {
      const response = await postJson<{ state: WebUiState }>("/api/settings", { changes });
      if (response.state) setState(response.state);
      if (notes.length > 0) announce();
      else Message.success("设置已保存");
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "设置保存失败");
    }
  };

  const renderAction = async (action: "start" | "cancel") => {
    if (renderActionInFlight.current || (action === "cancel" && state.render.cancelRequested)) return;
    if (!demoMode && !connected) {
      Message.error("Mod 服务未连接，正在尝试重连。请稍后再试。");
      return;
    }

    renderActionInFlight.current = true;
    try {
      if (demoMode) {
        setState((current) => ({
          ...current,
          render: action === "start"
            ? {
                ...current.render,
                active: true,
                state: "Rendering",
                cancelRequested: false,
                message: "演示渲染任务正在运行",
                progress: {
                  ...current.render.progress,
                  value: 0.62,
                  writtenFrames: 3721,
                  totalFrames: 6000,
                  duplicateFrames: 148,
                  duplicateRatio: 0.0398,
                  processingFramesPerSecond: 58.4,
                  estimatedRemaining: "00:00:21",
                  stage: "正在编码",
                  detail: "正在写入视频帧并合并音频。",
                  encoderName: "libx264 / veryfast",
                  memoryBudget: "1.2 GB / 2.0 GB",
                  queueBudget: "3 / 8"
                }
              }
            : {
                ...current.render,
                active: false,
                cancelRequested: false,
                state: "Canceled",
                message: "演示渲染已取消"
              }
        }));
      } else {
        const response = await postJson<{ state: WebUiState }>(`/api/render/${action}`, {
          taskId: state.render.taskId,
        });
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

  const runAction = async (path: string) => {
    if (demoMode) {
      Message.success("演示状态：操作已完成");
      return;
    }

    try {
      const response = await postJson<{ state: WebUiState }>(path);
      if (response.state) setState(response.state);
      Message.success("操作已完成");
    } catch (error) {
      Message.error(error instanceof Error ? error.message : "操作失败");
    }
  };

  const openDocument = (path: string) => {
    if (localPreview) {
      Message.info("本地预览没有连接 Mod 服务，帮助页面无法打开。");
      return;
    }

    const opened = window.open(localDocumentUrl(path), "_blank", "noopener,noreferrer");
    if (!opened) {
      Message.error("浏览器阻止了帮助页面，请允许当前页面打开新标签页。");
    }
  };

  const activePage = pageItems.find((item) => item.key === page) ?? pageItems[0];
  const visibleRender = page === "render";
  const connectionLabel = localPreview
    ? "本地预览"
    : connected
      ? "本机服务已连接"
      : reconnecting
        ? "连接断开，正在重连…"
        : "正在连接 Mod 服务…";

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand-lockup">
          <span className="brand-mark"><IconApps /></span>
          <span className="brand-name">EDITOR <strong>TWEAKS</strong></span>
          <span className="brand-divider">/</span>
          <span className="brand-page">设置</span>
        </div>
        <div className="topbar-actions">
          <span className={`connection-dot ${connected ? "is-online" : ""}`} />
          <span className="connection-label">{connectionLabel}</span>
          {!localPreview && !connected ? <button className="connection-retry" type="button" onClick={() => setConnectionReload((current) => current + 1)} title="重新连接 Mod 服务"><IconRefresh /> 重连</button> : null}
        </div>
      </header>

      {!localPreview && everConnected && !connected ? <div className="connection-lost-banner" role="alert">
        <IconCloseCircle />
        <div>
          <strong>{lostDuringRender ? "与游戏的连接已断开，渲染被中断" : "与游戏的连接已断开"}</strong>
          <span>{lostDuringRender
            ? "游戏可能已经关闭或崩溃。这次渲染没有完成，导出文件夹里不会有可用的视频（可能残留 .part 文件，可以直接删除）。"
            : "游戏可能已经关闭或崩溃。页面会自动尝试重连。"}
            {" "}如果游戏已经重新启动，这个页面无法再连接，请在游戏里按设置页快捷键重新打开。</span>
        </div>
      </div> : null}
      <div className="workspace">
        <aside className="sidebar">
          <div className="sidebar-title">功能导航</div>
          <nav>
            {pageItems.map((item) => (
              <button
                className={`nav-item ${page === item.key ? "is-active" : ""}`}
                key={item.key}
                onClick={() => setPage(item.key)}
              >
                <span className="nav-icon">{item.icon}</span>
                <span className="nav-copy">
                  <span className="nav-label">{item.label}</span>
                  <span className="nav-description">{item.description}</span>
                </span>
              </button>
            ))}
          </nav>
        </aside>

        <main className="main-content">
          {loading ? <div className="loading-state"><Spin size={32} /><span>正在连接 Mod 服务…</span></div> : null}
          {!loading && visibleRender ? (
            <RenderPage state={state} offline={!demoMode && !connected} onSettings={updateSettings} onOpenDocument={openDocument} onStart={() => renderAction("start")} onCancel={() => renderAction("cancel")} />
          ) : null}
          {!loading && !visibleRender && page === "overview" ? (
            <Overview state={state} onNavigate={setPage} onSettings={updateSettings} onAction={runAction} disabled={state.render.active} />
          ) : null}
          {!loading && !visibleRender && page === "tools" ? <ToolsPage onAction={runAction} onOpenDocument={openDocument} disabled={state.render.active} /> : null}
        </main>
      </div>

      <footer className="statusbar">
        <span className="status-running"><span className="status-led" />运行中</span>
        <span className="status-divider" />
        <button onClick={() => setPage("tools")} className="status-link"><IconSettings /> 工具</button>
        <span className="status-divider" />
        <span className="status-version">EDITOR TWEAKS {state.compatibility.modVersion}</span>
      </footer>
    </div>
  );
}

function Overview({ state, onNavigate, onSettings, onAction, disabled }: { state: WebUiState; onNavigate: (page: PageKey) => void; onSettings: (changes: Partial<SettingsState>) => void; onAction: (path: string) => void; disabled: boolean }) {
  const allAvailable = state.patchSummary.active === state.patchSummary.total && !state.patchSummary.registrationError;
  return (
    <>
      <PageHeading title="兼容性概览" description="查看当前游戏版本和所有功能组状态。" />
      <section className="compatibility-panel angular-panel">
        <div className="compatibility-item"><IconDesktop /><span><small>游戏版本</small><strong>{state.compatibility.gameVersion}</strong></span></div>
        <div className="compatibility-item"><IconCode /><span><small>编辑器版本</small><strong>{state.compatibility.editorVersion}</strong></span></div>
        <div className="compatibility-item"><IconApps /><span><small>Editor Tweaks 版本</small><strong>{state.compatibility.modVersion}</strong></span></div>
        <div className="compatibility-item"><IconCheckCircle /><span><small>兼容状态</small><strong className={allAvailable ? "text-accent" : "text-warning"}>{allAvailable ? "完全兼容" : "部分可用"}</strong></span></div>
      </section>

      <section className="section-block">
        <SectionHeading title="补丁状态" hint={`${state.patchSummary.active}/${state.patchSummary.total} 个功能组可用`} />
        <div className="patch-list">
          {state.patches.map((patch) => <PatchRow key={patch.id} patch={patch} />)}
        </div>
      </section>

      <EncoderDetection encoders={state.encoders} disabled={disabled} onDetect={() => onAction("/api/encoders/detect")} />

      <section className="section-block angular-panel global-settings">
        <div className="settings-note">本 Mod 只提供谱面视频渲染。编辑器优化和压缩包处理分别由独立 Mod 提供。</div>
      </section>
      <div className="page-actions"><Button className="secondary-button" icon={<IconUndo />} disabled={disabled} onClick={() => onAction("/api/settings/reset")}>恢复默认</Button><Button className="primary-button" onClick={() => onNavigate("render")}>打开谱面渲染</Button></div>
    </>
  );
}

function RenderPage({ state, offline, onSettings, onOpenDocument, onStart, onCancel }: { state: WebUiState; offline: boolean; onSettings: (changes: Partial<SettingsState>) => void; onOpenDocument: (path: string) => void; onStart: () => void; onCancel: () => void }) {
  const progress = state.render.progress;
  const percent = Math.round(Math.max(0, Math.min(1, progress.value)) * 1000) / 10;
  const settingsLocked = state.render.active;
  const terminalResult = getRenderResult(state.render);
  const [advancedOpen, setAdvancedOpen] = React.useState(false);
  const [professionalOpen, setProfessionalOpen] = React.useState(false);
  const isGameView = state.settings.ChartRenderCaptureSource.toLowerCase() === "game-view";
  const isCustomEncoder = state.settings.ChartRenderEncoderMode.toLowerCase() === "custom";
  const videoCodec = state.settings.ChartRenderVideoCodec.toLowerCase();
  const codecNote = isCustomEncoder && videoCodec !== "h264"
    ? "编码器为“自定义参数”时固定使用 H.264，视频编码格式的选择不会生效。"
    : videoCodec === "prores"
      ? "ProRes 文件非常大，适合导入剪辑软件；只能保存为 MOV，音频不能用 FLAC。"
      : videoCodec === "vvc"
        ? "H.266/VVC 是实验性格式：只有 CPU 编码，1080p60 大约每秒数帧，且大多数播放器、剪辑软件和网站不支持。仅供尝鲜。"
        : null;
  // Settings saved before these rules existed may still pair ProRes with MP4 or MOV with FLAC; the
  // fields show what the render will really use.
  const isProRes = videoCodec === "prores";
  const isVvc = videoCodec === "vvc";
  const constantBitrate = state.settings.ChartRenderConstantBitrate;
  const cbrNote = constantBitrate
    ? isProRes
      ? "ProRes 的码率由 profile 决定，CBR 对它不生效。"
      : isVvc
        ? "H.266/VVC 不支持恒定比特率，渲染时会自动改用 VBR。"
        : "恒定比特率会按“视频码率”输出，画质参数不再生效；H.264/HEVC 是受 VBV 限制的近似 CBR，不是逐段完全恒定。"
    : null;
  const shownVideoFormat = isProRes ? "mov" : state.settings.ChartRenderVideoFormat;
  const outputsMov = shownVideoFormat.toLowerCase() === "mov";
  const shownAudioFormat = outputsMov && state.settings.ChartRenderAudioFormat.toLowerCase() === "flac" ? "alac" : state.settings.ChartRenderAudioFormat;
  const disabledVideoFormats = isProRes ? { mp4: proResOnlyMov, mkv: proResOnlyMov } : undefined;
  const disabledAudioFormats = outputsMov ? { flac: movNoFlac } : undefined;
  // Encoder settings come in two views. Simple: the encoder mode alone (presets are ignored by the
  // mod in this view). Detailed: pick the encoder, then only that encoder's preset is shown.
  const detailedEncoder = state.settings.ChartRenderDetailedEncoderSettings;
  const encoderMode = state.settings.ChartRenderEncoderMode.toLowerCase();
  const encoderDevice = isVvc ? "cpu" : encoderMode === "custom" ? "custom" : encoderMode === "cpu-compatibility" ? "cpu" : "gpu";
  const modeDefaults = encoderPresetDefaults[encoderMode] ?? encoderPresetDefaults["auto-balanced"];
  const shownHardwarePreset = state.settings.ChartRenderHardwarePreset.toLowerCase() === "auto" ? modeDefaults.hardware : state.settings.ChartRenderHardwarePreset;
  const shownCpuPreset = state.settings.ChartRenderCpuPreset.toLowerCase() === "auto" ? modeDefaults.cpu : state.settings.ChartRenderCpuPreset;
  // Entering the detailed view fills in the presets the current mode was using, so nothing changes.
  const setDetailedEncoder = (detailed: boolean) => onSettings(detailed
    ? { ChartRenderDetailedEncoderSettings: true, ChartRenderHardwarePreset: shownHardwarePreset, ChartRenderCpuPreset: shownCpuPreset }
    : { ChartRenderDetailedEncoderSettings: false, ...(encoderMode === "custom" ? { ChartRenderEncoderMode: "auto-balanced" } : {}) });
  const setEncoderDevice = (device: string) => onSettings({ ChartRenderEncoderMode: device === "cpu" ? "cpu-compatibility" : device === "custom" ? "custom" : "auto-balanced" });
  const slowCpuPresets = ["medium", "slow", "slower", "veryslow"];
  // What this computer will really encode with, from the encoder detection.
  const detected = Object.keys(state.encoders.results).length > 0;
  const predictedEncoder = predictEncoder(state.settings, state.encoders);
  const hardwareOptions = hardwareCandidates(videoCodec);
  const hardwareCodec = !isProRes && !isVvc;
  const hasHardware = hardwareCodec && hardwareOptions.some((name) => state.encoders.results[name]);
  const wantsGpu = encoderMode !== "cpu-compatibility" && encoderMode !== "custom" && !isProRes && !isVvc;
  const selectedHardwareEncoder = state.settings.ChartRenderHardwareEncoder.toLowerCase();
  const selectedHardwareSuffix = hardwareSuffix(selectedHardwareEncoder);
  const selectedHardwareAvailable = selectedHardwareEncoder === "auto"
    ? hasHardware
    : hardwareOptions.some((name) => name.endsWith(selectedHardwareSuffix) && state.encoders.results[name]);
  const fellBackToCpu = predictedEncoder !== null && predictedEncoder !== "none" && wantsGpu && predictedEncoder.startsWith("lib");
  const hardwareUnavailable = wantsGpu && selectedHardwareEncoder !== "auto" && !selectedHardwareAvailable;
  const encoderNote = predictedEncoder === null
    ? (state.encoders.status === "detecting" ? "正在检测这台电脑能用的编码器…" : null)
    : predictedEncoder === "none"
      ? `这台电脑没有可用的 ${isVvc ? "H.266/VVC" : "AV1"} 编码器，渲染会失败，请换一种视频编码格式。`
      : `这台电脑将使用：${encoderLabels[predictedEncoder] ?? predictedEncoder}${hardwareUnavailable ? "。所选硬件编码器不可用，会改用 CPU 编码，速度明显变慢。" : fellBackToCpu ? "。没有可用的硬件编码器，会改用 CPU 编码，速度明显变慢。" : "。"}`;
  const disabledEncoderDevices = {
    ...(detected && hardwareCodec && !hasHardware ? { gpu: "没有检测到可用的硬件编码器" } : {}),
    ...(isVvc ? { gpu: "H.266/VVC 只有 CPU 编码", custom: "H.266/VVC 不能使用自定义参数" } : {}),
  };
  const disabledHardwareEncoders: Record<string, string> = {};
  if (detected && hardwareCodec) {
    if (!hardwareOptions.some((name) => name.endsWith("_nvenc") && state.encoders.results[name])) disabledHardwareEncoders.nvidia = "没有检测到可用的 NVIDIA 编码器";
    if (!hardwareOptions.some((name) => name.endsWith("_amf") && state.encoders.results[name])) disabledHardwareEncoders.amd = "没有检测到可用的 AMD 编码器";
    if (!hardwareOptions.some((name) => name.endsWith("_qsv") && state.encoders.results[name])) disabledHardwareEncoders.intel = "没有检测到可用的 Intel 编码器";
  }
  const hardwareNote = wantsGpu && selectedHardwareEncoder !== "auto" && !selectedHardwareAvailable
    ? "所选硬件编码器不可用；渲染会改用 CPU。可以改选“自动”或 CPU。"
    : null;
  const presetNote = !detailedEncoder
    ? null
    : isProRes && encoderDevice !== "custom"
      ? "ProRes 没有速度预设，编码器和预设的选择不适用。"
      : isVvc
        ? "H.266/VVC 只有 CPU 编码，速度极慢；较慢的预设会让渲染时间长到无法接受，建议先用 10 秒片段测试。"
        : encoderDevice === "gpu"
        ? `没有可用的硬件编码器、或显卡不支持所选编码格式时，会自动改用 CPU 编码，届时使用 CPU 预设 ${shownCpuPreset}。NVIDIA 使用 p1–p7；AMD/Intel 会把相同的速度档位换算成 AMF/QSV 预设。`
        : encoderDevice === "custom"
          ? "自定义编码参数只尝试 NVENC（H.264）；AMD / Intel 显卡请使用“显卡”模式。"
          : encoderDevice === "cpu" && slowCpuPresets.includes(shownCpuPreset.toLowerCase())
            ? "注意：较慢的 CPU 预设会明显拖慢整个渲染，因为每一帧都要等编码器处理完。1080p60 下 slow 大约只有每秒几帧。"
            : null;
  return <>
    <PageHeading title="谱面渲染" description="实时查看渲染阶段、编码状态和队列压力。" />
    {terminalResult ? <div className={`render-result-banner ${terminalResult.kind}`}><span className="render-result-icon">{terminalResult.kind === "success" ? <IconCheckCircle /> : terminalResult.kind === "failed" ? <IconCloseCircle /> : <IconInfoCircle />}</span><div><strong>{terminalResult.title}</strong><span>{terminalResult.detail}</span>{state.render.outputPath && terminalResult.kind === "success" ? <code title={state.render.outputPath}>{state.render.outputPath}</code> : null}</div></div> : null}
    <section className="render-panel angular-panel">
      <div className="render-head"><div><span className="eyebrow">{state.render.active ? "正在处理" : "渲染状态"}</span><h2>{progress.stage || "等待渲染"}</h2><p>{progress.detail || "从页面开始渲染后，全部进度会在这里实时更新。"}</p></div><div className="render-percent">{percent}<small>%</small></div></div>
      <div className="progress-track"><div className="progress-fill" style={{ width: `${percent}%` }} /></div>
      {state.render.active && state.render.writingOutput ? <div className="render-lock-note"><IconInfoCircle /><span>画面已经渲染完成，正在写入视频文件。请不要关闭游戏，等进度到 100% 并显示“渲染完成”后视频才可用。</span></div> : null}
      {state.render.active && state.render.quitRefused ? <div className="settings-note warning-note">刚才有一次关闭游戏的操作被拦下了，因为渲染还没完成。10 秒内再次关闭游戏会强制退出，视频将丢失。</div> : null}
      <div className="render-metrics">
        <Metric icon={<IconImage />} label="写入帧" value={`${progress.writtenFrames.toLocaleString()} / ${progress.totalFrames.toLocaleString()}`} />
        <Metric icon={<IconPlayArrow />} label="处理速度" value={`${progress.processingFramesPerSecond.toFixed(1)} 帧/秒`} />
        <Metric icon={<IconRefresh />} label="预计剩余" value={state.render.active && state.render.writingOutput ? "写入文件中" : progress.estimatedRemaining || "00:00:00"} />
        <Metric icon={<IconInfoCircle />} label="重复帧" value={`${progress.duplicateFrames.toLocaleString()} (${(progress.duplicateRatio * 100).toFixed(2)}%)`} />
      </div>
      <div className="render-details">
        <DetailLine label="模式" value={`离线定帧 / ${state.render.captureSource.toLowerCase().includes("game") ? "游戏画面" : "摄像机"}`} />
        <DetailLine label="编码器" value={progress.encoderName || "等待编码器"} />
        <DetailLine label="内存预算" value={progress.memoryBudget || "—"} />
        <DetailLine label="队列预算" value={progress.queueBudget || "—"} />
        <DetailLine label="输出路径" value={state.render.outputPath || "—"} />
      </div>
      {state.render.active ? <Button className="danger-button" disabled={state.render.cancelRequested || offline} icon={<IconCloseCircle />} onClick={onCancel}>{offline ? "连接已断开" : state.render.cancelRequested ? "正在取消…" : "取消渲染"}</Button> : <div className="render-action-row"><div className="render-idle"><IconCheckCircle /> {state.render.message || "当前没有正在运行的渲染任务"}</div><Button className="primary-button" disabled={offline} icon={<IconPlayArrow />} onClick={onStart}>开始渲染</Button></div>}
    </section>
    <section className={`render-settings angular-panel ${settingsLocked ? "is-locked" : ""}`}>
      <SectionHeading title="渲染设置" hint={settingsLocked ? "渲染进行中，设置已锁定" : "修改后保存，下一次渲染使用"} />
      {settingsLocked ? <div className="render-lock-note"><IconInfoCircle /><span>当前任务已经读取了渲染设置。为避免中途修改影响输出，渲染结束前暂时不能编辑这些选项。</span></div> : null}
      <div className="settings-note">推荐默认：1080p、60 帧，并在可用时优先使用 GPU 硬编码。</div>
      <div className="settings-grid">
        <SettingText label="导出目录" value={state.settings.ChartRenderExportDirectory} onChange={(value) => onSettings({ ChartRenderExportDirectory: value })} disabled={settingsLocked} />
        <SettingSelect label="画面捕获方式" group="ChartRenderCaptureSource" value={state.settings.ChartRenderCaptureSource} options={["camera", "game-view"]} onChange={(value) => onSettings({ ChartRenderCaptureSource: value })} disabled={settingsLocked} />
        {isGameView ? <InfoBlock text="游戏画面模式会在后台按下面设置的分辨率渲染完整画面（包括界面），可以超过显示器分辨率；屏幕上只显示预览。分辨率的宽高比建议和游戏窗口一致，否则界面元素的位置可能与游戏中略有不同。" /> : null}
        <PresetSetting label="分辨率预设" options={["1080p", "2K", "4K", "9:16", "21:9"]} active={getResolutionPreset(state.settings.ChartRenderWidth, state.settings.ChartRenderHeight)} onSelect={(preset) => onSettings(resolutionPresets[preset])} disabled={settingsLocked} />
        <SettingNumber label="视频宽度" value={state.settings.ChartRenderWidth} min={16} max={7680} step={2} onChange={(value) => onSettings({ ChartRenderWidth: value })} suffix="px" disabled={settingsLocked} />
        <SettingNumber label="视频高度" value={state.settings.ChartRenderHeight} min={16} max={4320} step={2} onChange={(value) => onSettings({ ChartRenderHeight: value })} suffix="px" disabled={settingsLocked} />
        <PresetSetting label="帧率预设" options={["30", "60", "120"]} active={String(state.settings.ChartRenderFps)} onSelect={(preset) => onSettings({ ChartRenderFps: Number(preset) })} disabled={settingsLocked} />
        <SettingNumber label="帧率" value={state.settings.ChartRenderFps} min={1} max={240} step={1} onChange={(value) => onSettings({ ChartRenderFps: value })} suffix="帧/秒" disabled={settingsLocked} />
        <SettingNumber label="结束后延迟停止" value={state.settings.ChartRenderCompletionTailSeconds} min={0} step={0.1} onChange={(value) => onSettings({ ChartRenderCompletionTailSeconds: value })} suffix="秒" disabled={settingsLocked} />
        <SettingSelect label="视频输出格式" group="ChartRenderVideoFormat" value={shownVideoFormat} options={["mp4", "mkv", "mov"]} disabledOptions={disabledVideoFormats} onChange={(value) => onSettings({ ChartRenderVideoFormat: value })} disabled={settingsLocked} />
        <SettingSelect label="视频编码格式" group="ChartRenderVideoCodec" value={state.settings.ChartRenderVideoCodec} options={["h264", "hevc", "av1", "vvc", "prores"]} onChange={(value) => onSettings({ ChartRenderVideoCodec: value })} disabled={settingsLocked} />
        {codecNote ? <InfoBlock text={codecNote} /> : null}
        {encoderNote ? <InfoBlock text={encoderNote} /> : null}
        <SettingSwitch label="渲染时显示判定文字" description="控制成品视频中是否显示 Perfect、Early、Late 等判定文字。" value={state.settings.ChartRenderShowHitJudgments} onChange={(value) => onSettings({ ChartRenderShowHitJudgments: value })} disabled={settingsLocked} />
        <SettingSwitch label="仅渲染选中段落" description="开启后需要在编辑器中框选至少两个连续砖块。" value={state.settings.ChartRenderUseSelectedRange} onChange={(value) => onSettings({ ChartRenderUseSelectedRange: value })} disabled={settingsLocked} />
      </div>
      <DisclosureButton label={advancedOpen ? "隐藏高级设置" : "显示高级设置"} open={advancedOpen} onClick={() => setAdvancedOpen((open) => !open)} disabled={false} />
      {advancedOpen ? <div className="settings-section">
        <div className="settings-note warning-note">高级设置只适合排查问题或有特殊导出需求时使用。错误的编码参数可能导致渲染失败或输出无法播放。</div>
        <div className="settings-grid">
          <SettingText label="工作区目录" value={state.settings.ChartRenderWorkspaceDirectory} onChange={(value) => onSettings({ ChartRenderWorkspaceDirectory: value })} disabled={settingsLocked} />
          <SettingSwitch label="详细编码设置" description="关闭时只选编码档位；开启后可以指定编码器和它的速度预设。" value={detailedEncoder} onChange={setDetailedEncoder} disabled={settingsLocked} />
          {detailedEncoder ? <>
            <SettingSelect label="编码器" group="ChartRenderEncoderDevice" value={encoderDevice} options={["gpu", "cpu", "custom"]} disabledOptions={disabledEncoderDevices} onChange={setEncoderDevice} disabled={settingsLocked} />
            {isProRes && encoderDevice !== "custom" ? null
              : encoderDevice === "gpu" ? <>
                <SettingSelect label="硬件编码器" group="ChartRenderHardwareEncoder" value={state.settings.ChartRenderHardwareEncoder} options={["auto", "nvidia", "intel", "amd"]} disabledOptions={disabledHardwareEncoders} onChange={(value) => onSettings({ ChartRenderHardwareEncoder: value })} disabled={settingsLocked} />
                <SettingSelect label="显卡编码预设" group="ChartRenderHardwarePreset" value={shownHardwarePreset} options={["p1", "p2", "p3", "p4", "p5", "p6", "p7"]} onChange={(value) => onSettings({ ChartRenderHardwarePreset: value })} disabled={settingsLocked} />
                {hardwareNote ? <InfoBlock text={hardwareNote} /> : null}
              </>
              : encoderDevice === "cpu" ? <SettingSelect label="CPU 编码预设" group="ChartRenderCpuPreset" value={shownCpuPreset} options={["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"]} onChange={(value) => onSettings({ ChartRenderCpuPreset: value })} disabled={settingsLocked} />
              : <SettingText label="编码方式" value={state.settings.ChartRenderPreset} onChange={(value) => onSettings({ ChartRenderPreset: value })} disabled={settingsLocked} />}
          </> : <SettingSelect label="编码档位" group="ChartRenderEncoderMode" value={encoderMode === "custom" ? "auto-balanced" : state.settings.ChartRenderEncoderMode} options={["auto-balanced", "fastest", "balanced", "quality", "cpu-compatibility"]} onChange={(value) => onSettings({ ChartRenderEncoderMode: value })} disabled={settingsLocked} />}
          {presetNote ? <InfoBlock text={presetNote} /> : null}
          <SettingNumber label="画质参数（CRF，越小越清晰、文件越大）" value={state.settings.ChartRenderCrf} min={0} max={51} step={1} onChange={(value) => onSettings({ ChartRenderCrf: value })} suffix="" disabled={settingsLocked || constantBitrate} />
          <SettingNumber label="视频码率" value={state.settings.ChartRenderBitrateMbps} min={0} max={300} step={1} onChange={(value) => onSettings({ ChartRenderBitrateMbps: value })} suffix="Mbps" disabled={settingsLocked} />
          <SettingSwitch label="恒定比特率（CBR）" description="按“视频码率”输出，画质参数不再生效；码率为 0 时使用自动推荐码率。" value={constantBitrate} onChange={(value) => onSettings({ ChartRenderConstantBitrate: value })} disabled={settingsLocked} />
          {cbrNote ? <InfoBlock text={cbrNote} /> : null}
          <SettingSelect label="回读格式" group="ChartRenderCaptureFormat" value={state.settings.ChartRenderCaptureFormat} options={["rgba", "bgra"]} onChange={(value) => onSettings({ ChartRenderCaptureFormat: value })} disabled={settingsLocked} />
          <SettingSelect label="音频格式" group="ChartRenderAudioFormat" value={shownAudioFormat} options={["aac", "flac", "alac"]} disabledOptions={disabledAudioFormats} onChange={(value) => onSettings({ ChartRenderAudioFormat: value })} disabled={settingsLocked} />
          <SettingSelect label="渲染预览" group="ChartRenderPreviewMode" value={state.settings.ChartRenderPreviewMode} options={["full", "dim", "minimal"]} onChange={(value) => onSettings({ ChartRenderPreviewMode: value })} disabled={settingsLocked} />
          <SettingNumber label="音频同步偏移" value={state.settings.ChartRenderAudioSyncOffsetMs} min={-5000} max={5000} step={1} onChange={(value) => onSettings({ ChartRenderAudioSyncOffsetMs: value })} suffix="毫秒" disabled={settingsLocked} />
        </div>
        <DisclosureButton label={professionalOpen ? "隐藏专业 FFmpeg 设置" : "显示专业 FFmpeg 设置"} open={professionalOpen} onClick={() => setProfessionalOpen((open) => !open)} disabled={false} />
        {professionalOpen ? <div className="professional-settings"><div className="settings-note">自定义 FFmpeg 程序路径：填写 ffmpeg.exe 的完整路径，留空则使用 Mod 自带的 FFmpeg。路径无效时会自动回退到自带版本。</div><SettingText label="自定义 FFmpeg 路径" value={state.settings.FfmpegCustomPath} onChange={(value) => onSettings({ FfmpegCustomPath: value })} disabled={settingsLocked} /><div className="settings-note">输入完整的自定义 FFmpeg 合成参数字符串。留空则使用自动生成参数。</div><SettingText label="自定义 FFmpeg 合成参数" value={state.settings.ChartRenderCustomMuxArgs} onChange={(value) => onSettings({ ChartRenderCustomMuxArgs: value })} disabled={settingsLocked} /><Button className="secondary-button" onClick={() => onOpenDocument("/docs/ffmpeg")}>打开 FFmpeg 参数参考帮助</Button></div> : null}
      </div> : null}
    </section>
  </>;
}

const resolutionPresets: Record<string, Partial<SettingsState>> = {
  "1080p": { ChartRenderWidth: 1920, ChartRenderHeight: 1080 },
  "2K": { ChartRenderWidth: 2560, ChartRenderHeight: 1440 },
  "4K": { ChartRenderWidth: 3840, ChartRenderHeight: 2160 },
  "9:16": { ChartRenderWidth: 1080, ChartRenderHeight: 1920 },
  "21:9": { ChartRenderWidth: 2560, ChartRenderHeight: 1080 },
};

function getResolutionPreset(width: number, height: number): string {
  const entry = Object.entries(resolutionPresets).find(([, value]) => value.ChartRenderWidth === width && value.ChartRenderHeight === height);
  return entry?.[0] ?? "";
}

function getRenderResult(render: RenderState): { kind: "success" | "failed" | "canceled"; title: string; detail: string } | null {
  if (render.active) return null;
  if (render.state === "Completed") return { kind: "success", title: "渲染已完成", detail: "视频已成功导出。" };
  if (render.state === "Failed") return { kind: "failed", title: "渲染失败", detail: render.message || render.progress.detail || "渲染任务失败，请查看日志。" };
  if (render.state === "Canceled") return { kind: "canceled", title: "渲染已取消", detail: render.message || "渲染任务已取消。" };
  return null;
}

function PresetSetting({ label, options, active, onSelect, disabled }: { label: string; options: string[]; active: string; onSelect: (value: string) => void; disabled?: boolean }) {
  return <div className="setting-row preset-setting"><div><strong>{label}</strong></div><div className="preset-options">{options.map((option) => <button className={`preset-button ${active === option ? "is-active" : ""}`} key={option} type="button" disabled={disabled} onClick={() => onSelect(option)}>{option}</button>)}</div></div>;
}

function DisclosureButton({ label, open, onClick, disabled }: { label: string; open: boolean; onClick: () => void; disabled?: boolean }) {
  return <button className={`disclosure-button ${open ? "is-open" : ""}`} type="button" disabled={disabled} onClick={onClick}><span>{label}</span><IconDown /></button>;
}

function ToolsPage({ onAction, onOpenDocument, disabled }: { onAction: (path: string) => void; onOpenDocument: (path: string) => void; disabled: boolean }) {
  return <FeaturePage title="工具" description="帮助、诊断和设置维护入口.">
    <div className="tool-row"><IconFile /><div><strong>打开使用手册</strong><span>在当前 Web 设置服务中打开，不依赖系统文件关联。</span></div><Button className="secondary-button" onClick={() => onOpenDocument("/docs/manual")}>打开</Button></div>
    <div className="tool-row"><IconImage /><div><strong>打开 FFmpeg 参数参考</strong><span>在当前 Web 设置服务中打开视频、音频和合成参数说明。</span></div><Button className="secondary-button" onClick={() => onOpenDocument("/docs/ffmpeg")}>打开</Button></div>
    <div className="tool-row"><IconUndo /><div><strong>恢复全部默认设置</strong><span>清除自定义配置并恢复 Mod 默认值。</span></div><Button className="danger-outline" disabled={disabled} onClick={() => onAction("/api/settings/reset")}>恢复默认</Button></div>
  </FeaturePage>;
}

function PatchRow({ patch }: { patch: PatchStatus }) {
  const stateName = patch.state === "active" ? "active" : patch.state === "failed" ? "failed" : "inactive";
  const icon = patch.name.includes("谱面") ? <IconImage /> : <IconTool />;
  return <div className="patch-row"><span className={`patch-icon ${stateName}`}>{icon}</span><div className="patch-name"><strong>{patch.name}</strong><span>{patchDescriptions[patch.name] ?? patch.description}</span></div><span className={`patch-state ${patch.state}`}><span className="patch-state-dot" />{statusLabel[patch.state] ?? patch.state}</span></div>;
}

function FeaturePage({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return <><PageHeading title={title} description={description} /><section className="feature-panel angular-panel">{children}</section></>;
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
  const [menuStyle, setMenuStyle] = React.useState<React.CSSProperties>({});
  const controlRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const updatePosition = () => {
      const rect = controlRef.current?.getBoundingClientRect();
      if (!rect) return;
      setMenuStyle({ left: rect.left, top: rect.bottom + 6, width: rect.width });
    };
    const closeOnOutsideClick = (event: MouseEvent) => {
      const target = event.target as Node;
      if (controlRef.current && !controlRef.current.contains(target) && !menuRef.current?.contains(target)) setOpen(false);
    };
    updatePosition();
    document.addEventListener("mousedown", closeOnOutsideClick);
    window.addEventListener("resize", updatePosition);
    window.addEventListener("scroll", updatePosition, true);
    return () => {
      document.removeEventListener("mousedown", closeOnOutsideClick);
      window.removeEventListener("resize", updatePosition);
      window.removeEventListener("scroll", updatePosition, true);
    };
  }, [open]);

  const menu = open ? createPortal(
    <div ref={menuRef} className="select-menu" role="listbox" style={menuStyle}>
      {options.map((option) => {
        const disabledReason = disabledOptions?.[option];
        return <button className={`select-option ${option === value ? "is-selected" : ""}`} key={option} type="button" role="option" aria-selected={option === value} aria-disabled={disabledReason ? true : undefined} disabled={Boolean(disabledReason)} title={disabledReason} onClick={() => { onChange(option); setOpen(false); }}><span className="select-option-check">{option === value ? <IconCheck /> : null}</span><span>{labelFor(group ?? "", option)}</span></button>;
      })}
    </div>,
    document.body,
  ) : null;

  return <div className="select-field"><button ref={controlRef} className={`select-control ${open ? "is-open" : ""}`} type="button" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen((current) => !current)}><span className="select-value">{labelFor(group ?? "", value)}</span><IconDown className="select-chevron" /></button>{menu}</div>;
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

function EncoderDetection({ encoders, disabled, onDetect }: { encoders: EncoderState; disabled: boolean; onDetect: () => void }) {
  const detecting = encoders.status === "detecting";
  const hasResults = Object.keys(encoders.results).length > 0;
  const hint = detecting ? "正在检测…" : encoders.status === "ready" ? `检测于 ${encoders.detectedAt}` : "尚未检测";
  return <section className="section-block">
    <SectionHeading title="编码器检测" hint={hint} />
    <div className="settings-note">检测这台电脑能用哪些视频编码器。首次运行、更换显卡或更换 FFmpeg 后会自动检测；更新显卡驱动后可以手动重新检测。{encoders.gpu ? `当前显卡：${encoders.gpu}。` : ""}</div>
    {hasResults ? <div className="patch-list">
      {encoderOrder.filter((name) => name in encoders.results).map((name) => <div className="patch-row" key={name}>
        <span className={`patch-icon ${encoders.results[name] ? "active" : "inactive"}`}>{encoders.results[name] ? <IconCheckCircle /> : <IconCloseCircle />}</span>
        <div className="patch-name"><strong>{encoderLabels[name] ?? name}</strong><span>{name}</span></div>
        <span className={`patch-state ${encoders.results[name] ? "active" : "inactive"}`}><span className="patch-state-dot" />{encoders.results[name] ? "可用" : "不可用"}</span>
      </div>)}
    </div> : null}
    <div className="page-actions"><Button className="secondary-button" icon={<IconRefresh />} disabled={disabled || detecting} onClick={onDetect}>{detecting ? "正在检测…" : "重新检测"}</Button></div>
  </section>;
}
function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) { return <div className="metric"><span className="metric-icon">{icon}</span><span><small>{label}</small><strong>{value}</strong></span></div>; }
function DetailLine({ label, value }: { label: string; value: string }) { return <div className="detail-line"><span>{label}</span><strong title={value}>{value}</strong></div>; }

export default App;
