# Transparent Screenshot 运行时辅助

这两个组件给 Unity 编辑器的 Game View 录屏与 UI 叠加预览使用。将此目录保留在 Editor 目录之外；录屏工具仍属于编辑器工具。

- GameViewVideoRunner：在 Play Mode 每帧结束时向编辑器录屏器回调。
- GameViewUIOverlayPreview：在 Play Mode 临时显示 UI 图片，供 Game View 预览。
- 原项目位置：Assets/Scripts/HotUpdate/ArtCode/TransparentScreenshot/。组件由工具临时创建，无需手动挂到场景。
- Windows 录屏可选 FFmpeg 硬件编码；未配置或探测失败时回退 Unity H.264，不需要分发 FFmpeg。
