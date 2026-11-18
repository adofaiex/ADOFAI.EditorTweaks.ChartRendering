using UnityEngine;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    internal static class WebUiSettingsView
    {
        private static bool capturing;

        public static void Draw(Settings settings)
        {
            if (capturing && WebUiHotkey.TryCapture(Event.current, out string hotkey))
            {
                settings.WebUiOpenHotkey = hotkey;
                settings.Save(Main.Mod!);
                capturing = false;
                Event.current.Use();
            }

            // UMM's mod list has no free-form status text, so the FFmpeg state is shown in this panel.
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("FFmpeg：" + ChartFfmpegInstaller.Describe());
            GUILayout.BeginHorizontal();
            if (!ChartFfmpegInstaller.IsReady && !ChartFfmpegInstaller.IsBusy
                && GUILayout.Button("安装定制版 FFmpeg", GUILayout.Width(180f)))
            {
                ChartFfmpegInstaller.Start("requested from the UMM settings panel");
            }

            if (!ChartFfmpegInstaller.IsReady && !ChartFfmpegInstaller.IsBusy
                && GUILayout.Button("安装完整版 FFmpeg", GUILayout.Width(180f)))
            {
                ChartFfmpegInstaller.Start("requested from the UMM settings panel", preferFull: true);
            }

            if (GUILayout.Button("打开 Web 设置页", GUILayout.Width(160f)))
            {
                WebUiHost.OpenFromModMenu();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            if (ChartUpdateChecker.IsAvailable)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("有新版本 " + ChartUpdateChecker.Latest + "：请用 UMM 或 Aurora 更新。");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("在 Aurora 中更新", GUILayout.Width(180f)))
                {
                    Application.OpenURL(ChartUpdateChecker.AuroraUrl);
                }

                if (GUILayout.Button("打开下载页", GUILayout.Width(160f)))
                {
                    Application.OpenURL(ChartUpdateChecker.DownloadUrl);
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Web 设置页面");
            GUILayout.BeginHorizontal();
            GUILayout.Label("打开 Web 设置页面快捷键", GUILayout.Width(220f));
            if (capturing)
            {
                GUILayout.Label("请按下组合键…", GUILayout.Width(160f));
            }
            else if (GUILayout.Button("录入快捷键", GUILayout.Width(120f)))
            {
                capturing = true;
            }

            GUILayout.Label(settings.WebUiOpenHotkey, GUILayout.Width(140f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label("默认 F8；也可以用 F9、Ctrl+Shift+U 等，避免和编辑器快捷键冲突。", GUI.skin.label);
            bool allowLan = GUILayout.Toggle(settings.WebUiAllowLan, "允许局域网访问（地址含控制令牌，请勿公开）");
            if (allowLan != settings.WebUiAllowLan)
            {
                settings.WebUiAllowLan = allowLan;
                settings.Save(Main.Mod!);
            }

            GUILayout.Label("默认只监听本机；开启局域网访问后，同一局域网的手机/平板可以打开 Web 设置页。", GUI.skin.label);
            GUILayout.EndVertical();
        }
    }
}
