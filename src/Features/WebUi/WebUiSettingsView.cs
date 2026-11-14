using UnityEngine;

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
