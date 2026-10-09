# ZXX ToolBox


## v0.6.0 — 2026-10-09

新增 6 个工具（面片落地烘焙、岩石生成、城市网格编辑、随机材质、重叠物体散开、半透队列排序）和 14 个 Shader / Shader Graph；完善建筑面片编辑、PathBuilder 烘焙、ProcGen 组装与避让、地块切割、阴影烘焙和战争迷雾。逐项变化见 [v0.6.0 更新日志](CHANGELOG.md#v060--2026-10-09)，原项目路径见 [源码位置索引](SOURCE_INDEX.md)，操作见 [工具使用说明](Docs/工具使用说明.md)。

Unity 编辑器工具与 Shader 集合，适用于 **Unity 2022.3+ · URP 14+**。

## 安装

将仓库 clone 到 Unity 项目的 `Assets/` 或 `Packages/` 目录下：

```bash
git clone https://github.com/Changhenghyeng/ZXXToolBox.git Assets/ZXXToolBox
```

或通过 Unity Package Manager 以 git URL 导入：

```
https://github.com/Changhenghyeng/ZXXToolBox.git#v0.6.0
```

---

## 工具列表

### 🧰 编辑器工具

| 工具 | 功能 | 入口 |
|------|------|------|
| **CardGroundBaker** | 按相机方向将面片投影至水平面或 Collider，预览并烘焙新 Mesh | 工具箱面板 |
| **RockTools** | 使用内置形状或自定义 Mesh/FBX/Prefab 生成并烘焙岩石 | 工具箱面板 |
| **SLGCityEditor** | 网格绘制城市区域、生成城墙和地面、建筑对齐网格 | 工具箱面板 |
| **RandomMaterial** | 随机分配材质，可选分轴随机位置、旋转和缩放 | 工具箱面板 |
| **OverlapSpreader** | 将 XZ 占地重叠的场景物体组散开，可调范围和间距 | 工具箱面板 |
| **TransparentQueueSorter** | 手动树状顺序设置透明队列，临时预览并烘焙独立材质 | 工具箱面板 |
| **ZXXToolBox** | 工具箱主面板，可自定义工作流串联多个工具 | `ArtTools > BIGBRO ToolBox` |
| **BatchRenameTool** | 批量重命名 Hierarchy 中的 GameObject，支持查找替换/前缀后缀/正则 | 工具箱面板 |
| **BoxColliderGenerator** | 根据 Renderer 包围盒自动生成 BoxCollider | 工具箱面板 |
| **BuildingCardTool** | 建筑面片预制体批量生成，用于快速搭建主城建筑白模 | 工具箱面板 |
| **ConvertVariantToNormalPrefab** | 将 Prefab Variant 转换为普通 Prefab | 工具箱面板 |
| **DayChangeTransitionTool** | 昼夜切换材质过渡编辑与预览 | 工具箱面板 |
| **FBXMeshEditor** | 场景中直接选择并移动静态模型顶点，支持 Undo | 工具箱面板 |
| **FogOfWarTool** | 战争迷雾系统设置与生成 | 工具箱面板 |
| **GeneratedAssetsManager** | 整理、审计并清理 Assets/Generated 工具产物 | 工具箱面板 |
| **GitPathNavigator** | 在 Unity 内快速跳转到 Git 仓库路径 | 工具箱面板 / `Ctrl+G` |
| **LabelTool** | 场景视图名称标签（Billboard 模式），含竹简气泡框样式 | 工具箱面板 |
| **LightCookieScroller** | 灯光 Cookie 纹理滚动动画（云影/焦散/斑驳光影） | 工具箱面板 |
| **MeasureTool** | 场景视图距离/角度测量 | 工具箱面板 |
| **OutlineTool** | 卡通描边效果设置 | 工具箱面板 |
| **PathBuilder** | 路径生成工具，含 PathSpline 曲线编辑子系统 | 工具箱面板 |
| **PrefabBatchReplacer** | 批量替换场景中的 Prefab 实例 | 工具箱面板 |
| **PrefabExtractorTool** | 从场景中提取选中物体为 Prefab（支持 Undo/Backup） | 工具箱面板 |
| **PrefabNameSyncTool** | 将 Prefab 实例名称同步为 Prefab 资源名称 | 工具箱面板 |
| **PrefabRootNormalizer** | 规范化 Prefab 根节点（包裹/批量处理） | 工具箱面板 / `Ctrl+Shift+N` |
| **ProcGen** | 程序化生成系统，基于 SpatialHash 的摆放与去重 | 工具箱面板 |
| **SelfShadowBaker** | 自阴影（SDF 投影）烘焙 | 工具箱面板 |
| **ShadingModeToggle** | 一键切换场景视图着色模式（Lit/Wireframe/Unlit 等） | 工具箱面板 |
| **SLGMapTileCutter** | 沙盘地图地块切割，支持内描边渲染 | 工具箱面板 |
| **SLGTerrainBaker** | 沙盘地形烘焙，合成贴图生成 | 工具箱面板 |
| **ShadowBakerTool** | 阴影贴图烘焙 + 贴花生成，含 Blur/Silhouette Shader | 工具箱面板 |
| **ShadowCastOffTool** | 批量关闭选中物体的阴影投射 | 工具箱面板 |
| **ToonMaterialConverter** | ToonOutline 普通/打包 Shader 双向转换，输出 BC 与 N_M_R 贴图 | 工具箱面板 |
| **TransparentScreenshot** | 透明背景截图 + 粒子序列渲染 | 工具箱面板 / `Ctrl+Shift+X` |

### 🏃 运行时组件

| 组件 | 功能 | 目录 |
|------|------|------|
| **DayChangeTransitionController** | 昼夜切换运行时过渡（材质属性动画） | `Runtime/DayChangeTransition/` |
| **FogGridClickSystem** | 战争迷雾点击交互，正方形网格显露/遮蔽 | `Runtime/FogOfWar/` |
| **LightCookieScroller** | Light Cookie UV 滚动（需 PlayMode） | `Runtime/LightCookieScroller/` |
| **RockTools** | 岩石生成、逻辑与烘焙运行时类型 | `Runtime/RockTools/` |

### 🎨 Shader

#### 最终效果 Shader（运行时渲染，赋给最终材质）

| Shader | 类型 | 说明 |
|--------|------|------|
| **BakedLightMap / BakedLightMap_PlantWind** | 光照/植被 | 烘焙阴影、AO、补光及植物风动 |
| **BuildingCard_FogDepth_URP** | 建筑 | 建筑面片雾与深度版本 |
| **Character Color Adjustment** | 角色 | 角色颜色调整 Shader Graph |
| **LitTransparentDepth** | 光照/透明 | Lit 透明深度版本及专用 Inspector |
| **SLG_TerrainBlend_Mobile_Transparent** | 地形 | 移动版地形混合透明版本 |
| **SimpleLit_BottomFade / SimpleLit_BottomFade_Shadow** | 植被 | 根部渐隐及阴影支持版本 |
| **SimpleTopBlend** | 混合 | 顶部混合材质 |
| **StylizedRiverIntersection** | 水面 | 深浅水、岸边泡沫、坡面流纹及边缘透明 |
| **StylizedWaterfall** | 水面 | 双层流纹/法线、底部与交界泡沫 |
| **TerrainBlender** | 地形 | 地形混合，X/Y 边缘渐隐宽度与硬度分别控制 |
| **WallWorldVariation_URP** | 建筑 | 世界空间花纹及法线，分轴投影 |
| **Manual Cloud Fog** | 迷雾 | 战争迷雾配套云雾材质 |
| **ShadowDecal_Gradient_URP** | 阴影/贴花 | 渐变方向/角度/Lp-norm 距离/SkyTint 天空着色 |
| **SimpleLit_BottomFade_Wave** | 植被 | SimpleLit 光照 + 根部渐隐 + 风力波动，Forward Pass 写入深度 |
| **StylizedRiver** | 水面 | 双层法线混合 + 焦散 + 泡沫 + 速度线 + 深浅渐变 |
| **ToonOutline_URP** | 描边 | 卡通风格外描边 |
| **ToonOutline_URP_Packed** | 描边 | 卡通描边打包版（BC + N_M_R 合并贴图） |
| **SLGMapTileInnerOutline** | 描边 | 沙盘地块内描边 |
| **SLG_TerrainBlend_Lite** | 地形 | SLG 地形混合（轻量版） |
| **SLG_TerrainBlend_Mobile** | 地形 | SLG 地形混合（移动版） |
| **BuildingCard_URP** | 建筑 | 建筑面片图集 URP |
| **Fog Of War** | 迷雾 | 网格战争迷雾 |
| **LightCookieScroller** | 光照 | Light Cookie UV 滚动 |

#### 工具过程 Shader（Hidden，配合工具工作的中间产物，非最终效果）

| Shader | 所属工具 | 说明 |
|--------|----------|------|
| **SelfShadowBaker** | SelfShadowBaker | SDF 自阴影烘焙，生成阴影遮罩贴图 |
| **SLG_TerrainComposite_Baker** | SLGTerrainBaker | 地形合成贴图烘焙 |
| **Blur_URP** | ShadowBakerTool | 阴影贴图模糊 |
| **Silhouette_URP** | ShadowBakerTool | 阴影剪影生成 |
| **FogGridDebugOverlay** | FogOfWarTool | 迷雾网格调试覆盖层 |
| **ToonTextureConversion** | ToonMaterialConverter | ToonOutline 贴图转换（BC/N_M_R） |

---

## 依赖

| 依赖 | 版本 |
|------|------|
| Unity | 2022.3+ |
| Render Pipeline | URP 14.x |
| Spine (spine-unity) | 4.2.x |

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
| 根目录各工具文件夹 | **Editor 工具** | 仅 Editor 下运行，入口在 `ArtTools > BIGBRO ToolBox` |
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
| RockTools 运行时部分 | RockTools/Scripts | 与编辑器程序集分离，位于 Runtime/RockTools |

> 每个 Runtime 组件目录下都有 `README.md` 说明其作用和放置路径。

### 版本发布

| 版本位 | 触发条件 | 示例 |
|--------|----------|------|
| **MAJOR** (`x.0.0`) | 不兼容的 API 改动、工具大改导致旧配置失效 | `v1.0.0`：API 稳定，正式版 |
| **MINOR** (`0.x.0`) | 新增工具 / Shader / 运行时组件，向下兼容 | `v0.2.0`：新增 3 个工具 + 架构重构 |
| **PATCH** (`0.0.x`) | Bug 修复、小优化，无新功能 | `v0.2.1`：修了个渲染问题 |

- 当前 `v0.x` 表示早期开发阶段，工具数量和结构还在快速迭代
- 每次同步前检查是否达到了 bump 条件，达标则打 tag
- Tag 可通过 Unity Package Manager 以 git URL 导入固定版本：`https://github.com/Changhenghyeng/ZXXToolBox.git#v0.2.0`

### 提交规范

- `feat:` — 新增工具或 Shader
- `refactor:` — 重构（如入口整理）
- `docs:` — README 及文档更新
- `chore:` — 格式/配置类变更

---

## 贡献

个人工具仓库，欢迎提 Issue 反馈 bug 或工具需求。
