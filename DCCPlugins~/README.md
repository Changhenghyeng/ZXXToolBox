# DCC 插件

面向 Maya 等内容制作软件的插件，与 Unity 开发模块、通用桌面工具分开管理。保留目录名末尾的 `~`，避免被 Unity 当作工程资源导入。

| 软件 | 工具 | 功能 |
|---|---|---|
| Maya | [NormalTangentTool](Maya/NormalTangentTool/README.md) | 将切线空间平滑法线 XY 编码到 outlineData UV 集，用于描边渲染 |
| Maya | [ExportSelectedMeshesToFBX](Maya/ExportSelectedMeshesToFBX/README.md) | 将选中对象及子级中的网格分别导出为 FBX |

本次收录找到的可编辑源码，不包括本机 userSetup.py、偏好设置、场景、模型和编译字节码。来源和版本核对见 [源码索引](SOURCE_INDEX.md)。工具没有独立版本标识，此批源码归档随仓库 v0.10.0 发布。仅完成静态语法、依赖及哈希检查，未在 Maya 中执行安装、导出或烘焙。

这两个工具并非已验证的一键联动流程：法线工具要求 FBX 包含切线/副切线，但目前 FBX 批量导出工具明确设置 FBXExportTangents 为 false。用于描边资产时请采用 Maya 自带 FBX 导出并启用 Tangents and Binormals，且核对 Unity 的切线导入和 UV 通道。原始算法与导出设置此次不变。
