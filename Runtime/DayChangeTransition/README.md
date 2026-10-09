# DayChangeTransition（运行时）

## 说明

此目录存放昼夜变化过渡系统的**运行时脚本**，需放置在非 Editor 目录下（如 `HotUpdate/ArtCode/Helper/`），确保 PlayMode 下可正常执行。

## 文件

| 文件 | 说明 |
|------|------|
| `DayChangeTransitionController.cs` | 昼夜过渡控制器，驱动场景光照/环境随时间变化 |

## 配套 Editor 工具

对应的编辑器工具位于 `../../DayChangeTransitionTool/`（Editor 目录），提供可视化配置界面。
