# FogOfWar（运行时）

## 说明

此目录存放战争迷雾系统的**运行时脚本和 Shader**，需放置在非 Editor 目录下（如 `HotUpdate/ArtCode/Helper/`），确保 PlayMode 下可正常执行。

## 文件

| 文件 | 说明 |
|------|------|
| `FogGridClickSystem.cs` | 迷雾网格点击交互系统 |
| `Fog Of War.shader` | 战争迷雾渲染 Shader |
| `Manual Cloud Fog.shader` | 手动云雾渲染 Shader |

## 配套 Editor 工具

对应的编辑器工具位于 `../../FogOfWarTool/`（Editor 目录），提供迷雾参数配置与调试功能。
