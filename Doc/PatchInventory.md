# ChartRendering 补丁清单

| 功能组 | 主要内容 |
| --- | --- |
| RenderInputGuard | 渲染时拦截编辑器缩放、暂停、玩家输入和 Unity UI 输入。 |
| ChartRendering | 固定视觉时间、自动补打、屏蔽界面音、判定文字、帧率画面、视频背景同步。 |

渲染任务还通过 `ChartRenderTimeScalePatch` 对运行时程序集的时间读取做渲染专用处理；该修改只属于 ChartRendering。
