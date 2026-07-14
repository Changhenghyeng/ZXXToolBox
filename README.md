# ZXX ToolBox

Unity 编辑器工具与 Shader 集合，适用于 **Unity 2022.3+ · URP 14+**。

---

## 安装

将仓库 clone 到 Unity 项目的 `Assets/` 或 `Packages/` 目录下：

```bash
git clone https://github.com/Changhenghyeng/ZXXToolBox.git Assets/ZXXToolBox
```

或通过 Unity Package Manager 以 git URL 导入：

```
https://github.com/Changhenghyeng/ZXXToolBox.git#v0.1.0
```

---

## 工具列表

### 🧰 编辑器工具

| 工具 | 功能 | 入口 |
|------|------|------|
| **ZXXToolBox** | 工具箱主面板，可自定义工作流串联多个工具 | `Tools > ZXX ToolBox` |
| **BatchRenameTool** | 批量重命名 Hierarchy 中的 GameObject，支持查找替换/前缀后缀/正则 | EditorWindow |
| **BoxColliderGenerator** | 根据 Renderer 包围盒自动生成 BoxCollider | EditorWindow |
| **BuildingCardTool** | 建筑面片预制体批量生成，用于快速搭建主城建筑白模 | EditorWindow |
| **ConvertVariantToNormalPrefab** | 将 Prefab Variant 转换为普通 Prefab | 右键菜单 |
| **DayChangeTransitionTool** | 昼夜切换材质过渡编辑与预览 | EditorWindow |
| **FogOfWarTool** | 战争迷雾系统设置与生成 | EditorWindow |
| **GitPathNavigator** | 在 Unity 内快速跳转到 Git 仓库路径 | EditorWindow |
| **LabelTool** | 场景视图名称标签（Billboard 模式），含竹简气泡框样式 | `Window > 名称标签` |
| **MeasureTool** | 场景视图距离/角度测量 | EditorWindow |
| **OutlineTool** | 卡通描边效果设置 | EditorWindow |
| **PrefabBatchReplacer** | 批量替换场景中的 Prefab 实例 | EditorWindow |
| **PrefabExtractorTool** | 从场景中提取选中物体为 Prefab（支持 Undo/Backup） | EditorWindow |
| **PrefabNameSyncTool** | 将 Prefab 实例名称同步为 Prefab 资源名称 | 静态方法 |
| **PrefabRootNormalizer** | 规范化 Prefab 根节点（包裹/批量处理） | EditorWindow + 静态方法 |
| **ProcGen** | 程序化生成系统，基于 SpatialHash 的摆放与去重 | EditorWindow |
| **SelfShadowBaker** | 自阴影（SDF 投影）烘焙 | `Tools > Self Shadow Baker` |
| **ShadingModeToggle** | 一键切换场景视图着色模式（Lit/Wireframe/Unlit 等） | EditorWindow |
| **ShadowBakerTool** | 阴影贴图烘焙 + 贴花生成，含 Blur/Silhouette Shader | EditorWindow |
| **ShadowCastOffTool** | 批量关闭选中物体的阴影投射 | 静态方法 |
| **TransparentScreenshot** | 透明背景截图 + 粒子序列渲染 | EditorWindow |
| **WallBuilderTool** | 场景墙体快速搭建 | EditorWindow |

### 🏃 运行时组件

| 组件 | 功能 | 目录 |
|------|------|------|
| **DayChangeTransitionController** | 昼夜切换运行时过渡（材质属性动画） | `Runtime/DayChangeTransition/` |
| **FogGridClickSystem** | 战争迷雾点击交互，正方形网格显露/遮蔽 | `Runtime/FogOfWar/` |

### 🎨 Shader

| Shader | 类型 | 说明 |
|--------|------|------|
| **ShadowDecal_Gradient_URP** | 阴影/贴花 | 渐变方向/角度/Lp-norm 距离/SkyTint 天空着色 |
| **SimpleLit_BottomFade_Wave** | 植被 | SimpleLit 光照 + 根部渐隐 + 风力波动 + 深度预写 |
| **StylizedRiver** | 水面 | 双层法线混合 + 焦散 + 泡沫 + 速度线 + 深浅渐变 |
| **ToonOutline_URP** | 描边 | 卡通风格外描边 |
| **SelfShadowBaker** | 阴影 | SDF 自阴影投射 |
| **Blur_URP** | 后处理 | 模糊 |
| **Silhouette_URP** | 剪影 | 剪影效果 |
| **Fog Of War** | 迷雾 | 网格战争迷雾 |
| **LightCookieScroller** | 光照 | Light Cookie UV 滚动 |

---

## 依赖

| 依赖 | 版本 |
|------|------|
| Unity | 2022.3+ |
| Render Pipeline | URP 14.x |

---

## 目录结构

```
ZXXToolBox/
├── Editor/     ← 待整理：大部分编辑器工具
├── Runtime/     ← 运行时组件
│   ├── DayChangeTransition/
│   └── FogOfWar/
├── Shaders/     ← 通用 Shader
├── ProcGen/     ← 程序化生成系统
├── LabelTool/   ← 场景名称标签
├── ...          ← 其他独立工具目录
└── ZXXToolBox/  ← 工具箱主入口 & 配置
```

> 目录结构目前按功能平铺，计划逐步迁移至 `Editor/` + `Runtime/` 分离结构。

---

## 管理规则

### 目录分类

| 目录 | 用途 | 说明 |
|------|------|------|
| 根目录各工具文件夹 | **Editor 工具** | 仅 Editor 下运行，入口在 `Tools > ZXX ToolBox` |
| `Runtime/` | **运行时组件** | 需要 PlayMode 执行，对应公司项目 `HotUpdate/` 路径 |
| `Shaders/` | **通用 Shader** | 独立于具体工具的渲染 Shader |
| `ZXXToolBox/` | **工具箱主入口** | 面板配置 `.asset` + 注册逻辑 |

### 为什么有些文件不在 Editor 目录？

Unity 中 `Editor` 文件夹下的代码**无法在 PlayMode 运行时访问**。以下组件因运行时需求被移到 `Runtime/`：

| 组件 | 原 Editor 位置 | 原因 |
|------|---------------|------|
| DayChangeTransitionController | DayChangeTransitionTool | 昼夜过渡需在运行时驱动材质属性动画 |
| FogGridClickSystem + Fog Of War.shader | FogOfWarTool | 战争迷雾需运行时响应点击交互和渲染 |
| LightCookieScroller | LightCookieScroller | Light Cookie UV 滚动需在 PlayMode 下持续更新 |

> 每个 Runtime 组件目录下都有 `README.md` 说明其作用和放置路径。

---

## 贡献

个人工具仓库，欢迎提 Issue 反馈 bug 或工具需求。
