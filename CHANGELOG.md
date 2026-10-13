# Changelog


## [v0.7.0] — 2026-10-13

相对 v0.6.0，新增 4 个 Shader，更新建筑面片工具的排序、相机补偿和保存流程，以及地形、河流和城墙渲染。此次共同步 11 个源码文件（4 个新增、7 个修改），未新增编辑器工具。

### 新增 Shader（4 个）

- **Spine_Skeleton_LineClip.shader**：Spine 建筑双线裁剪。两条裁剪线分别设置开关、高度和角度，可组合出 V 形裁剪；使用相对 Renderer 枢轴的世界单位坐标，避免裁剪线随动画 Bounds 或图集 UV 变化。增加可见贴图 Alpha 阈值，可裁掉烘焙阴影等低透明度像素；可见与投影 Pass 使用相同裁剪线。依赖项目中的 spine-unity `Spine-Common.cginc`。
- **ShadowDecal_PlantWind_URP.shader**：让地面树影随植物风动变化。通过阴影纹理 UV 扭曲保持地面网格水平，无需细分；可设置固定根部、根到树冠过渡、风向/强度/速度/频率/相位，以及树冠到阴影的 X/Z 投影偏移；保留渐变与天空染色控制。
- **WallWorldVariation_Opaque_URP.shader**：世界空间花纹城墙的不透明版本，使用 Geometry 队列并写入深度，保留基础贴图、花纹法线、分轴投影及共享 ShaderGUI。
- **UnlitReceiveShadow_URP.shader**：保留贴图原色、同时接收主光源阴影的 Unlit 材质；可设置阴影颜色和强度、Alpha 裁剪及剔除方式，包含投影、深度及深度法线 Pass。

### BuildingCardTool 改进

- **统一建筑排序基准**：以 BoxCollider 底面中心作为 Root 枢轴，在根节点使用 `SortingGroup.sortAtRoot`，普通面片与 Spine 按落地点排序；重新计算真实 Mesh Bounds，不再用修改 Bounds 中心模拟排序枢轴。校正 Root 时保持可见子物体及碰撞盒的世界位置。
- **增加“仅修复排序”**：批量修复现有 Prefab 的排序和旧 Visual SortingGroup；失败项在列表中标记并显示原因。
- **增加底面中心补偿**：默认关闭。启用后将 Visual 的 X 旋转归零，补偿 Y 缩放以维持原相机画面高度，再沿指定正交相机方向对齐碰撞盒底面中心线；保持 X 缩放。记录原始变换与偏移，重复应用不累加，关闭后再应用可恢复原始变换；无法满足投影条件的对象会跳过并报告。
- **明确保存方式**：编辑现有图片 Prefab 默认覆盖当前 Prefab；按钮区分“覆盖当前 Prefab”和“另存为新 Prefab”。覆盖时复用原 Mesh，另存时复制 Mesh，保留原材质及图集数据。
- **图集与编辑数据同步**：图集更新时应用新的排序规则，并同步 Root 调整和相机补偿记录，避免后续编辑重复偏移。

### 已有 Shader 改进

- **BakedLightMap_PlantWind.shader**：风力为零时跳过顶点风动；法线强度为零、金属/光滑强度均为零或自发光关闭时，跳过对应贴图采样及计算，使用材质统一分支而不增加 Shader 变体。
- **TerrainBlender.shadergraph**：增加世界 XZ 矩形裁剪，分别设置中心 X/Z、宽度 X、长度 Z 和遮罩翻转；默认关闭。启用 Alpha Clip，原边缘渐隐改为边缘裁剪，仍保留 X/Y 独立宽度与硬度；圆角过渡改用径向组合，消除四角的对角接缝。
- **SLG_TerrainBlend_Mobile_Transparent.shader**：Unlit 模式也可接收主光源阴影，增加 `Shadow Strength`；从表面的世界坐标采样阴影，并补充不同软阴影质量变体。
- **StylizedRiverIntersection.shader**：移除镜头中心强制最低高光，避免反射角较弱时仍保留人为亮斑。
- **WallWorldVariation_URP.shader**：透明版关闭深度写入，继续 Alpha 混合；需要不透明遮挡时可选新增的 Opaque 版本。

### 同步范围与检查

- 扫描全项目代码 Git 历史、作者及迁移记录，并对比上次发布源码哈希；沿用原目录映射，未改动本地项目源码。
- `BuildingCard_URP.shader` 当前内容与 v0.6.0 一致；项目中新增后又删除的 `BuildingCard_ContourClip_URP.shader` 不收录。`MainCityViewTool` 的添加记录属于其他作者，本次没有将其作为个人新增工具上传。
- 更新 README、操作说明和源码位置索引；发布 v0.7.0 标签。
- 没有新增模型、贴图、材质、场景、Prefab、`.meta` 或生成配置。Spine 等第三方依赖需由使用项目自行安装，不复制第三方库。
- 已核对源码内容、ShaderGraph JSON 对象、include 依赖和 Git 提交范围；本次上传未进行 Unity 编译或场景效果验证。

## [v0.6.0] — 2026-10-09

相对 v0.5.0，新增 6 个编辑器工具、14 个 Shader / Shader Graph，并更新建筑面片、路径生成、程序化摆放、地块切割和战争迷雾等现有功能。

### 新增工具

- **CardGroundBaker（面片落地烘焙）**：沿指定相机方向，将建筑/山脉等面片投影到世界水平面或 Collider 表面；支持临时预览，烘焙输出新 Mesh 和场景物体。
- **RockTools（岩石生成）**：使用内置形状或选中的 Mesh、FBX、Prefab 作为来源，生成并烘焙岩石；分离 Editor 与 Runtime 程序集。
- **SLGCityEditor（城市网格编辑）**：网格绘制、擦除、框选填充/擦除和城门设置；提取城市边界，生成模块化城墙及 Quad/Terrain 地面，支持建筑占地中心对齐网格。
- **RandomMaterial（随机材质）**：批量随机分配材质，可包含子物体并指定材质槽；可选随机位置、旋转和缩放，分别设置 X/Y/Z 范围。
- **OverlapSpreader（重叠物体散开）**：以父物体的直接子节点或多选场景物体为整体，检测 XZ 占地重叠并围绕各重叠组中心散开；可调占用范围和间距，支持 Undo。
- **TransparentQueueSorter（半透队列排序）**：按手动树状列表顺序设置透明材质 Render Queue，可按根节点共用队列或手动指定；临时材质预览，烘焙为独立材质。

### 新增 Shader / Shader Graph（14 个）

- **BakedLightMap.shadergraph**：使用烘焙光照贴图的材质。
- **BakedLightMap_PlantWind.shader**：烘焙阴影、AO 与补光材质增加植物风动；支持上下/左右四种风力渐变方向，光照、阴影和深度 Pass 使用一致的顶点变形。
- **BuildingCard_FogDepth_URP.shader**：建筑面片的雾与深度处理版本。
- **Character Color Adjustment.shadergraph**：角色材质的颜色、色相与饱和度调整。
- **LitTransparentDepth.shader**：保留 Lit 材质功能的透明深度版本，附带专用 ShaderGUI、Input 与 ForwardPass HLSL。
- **SLG_TerrainBlend_Mobile_Transparent.shader**：移动版地形混合的透明版本。
- **SimpleLit_BottomFade.shader**：SimpleLit 根部渐隐版本。
- **SimpleLit_BottomFade_Shadow.shader**：根部渐隐的阴影支持版本。
- **SimpleTopBlend.shader**：按坡度混合顶部覆盖贴图与法线，支持世界空间平铺和上下投影方向。
- **StylizedRiverIntersection.shader**：风格化河流，支持深浅水、岸边/交界泡沫、坡面流纹、顶点波浪和 UV 边缘透明渐变。
- **StylizedWaterfall.shader**：风格化瀑布，支持双色渐变、双层流纹与法线、底部及交界泡沫。
- **TerrainBlender.shadergraph**：地形混合与边缘渐隐，X/Y 渐隐宽度和硬度分别控制。
- **WallWorldVariation_URP.shader**：城墙材质增加世界空间花纹及法线，分别控制 X/Y/Z 投影方向、平铺和强度；附带 ShaderGUI。
- **Manual Cloud Fog.shader**：战争迷雾配套的手动云雾材质，位于 `Runtime/FogOfWar/`。

### 已有工具改进

- **BuildingCardTool**：增加现有 Prefab 编辑与批量重构，兼容图片面片和 Spine；按现有 BoxCollider 的 X/Z 尺寸重算 Visual 缩放与底面位置，保留碰撞盒、Mesh、材质、UV、骨骼及动画；补充编辑数据保存、底面拟合和源贴图信息修复。
- **PathBuilder**：增加山脉面片批量烘焙和压平预览，可按相机方向落到世界水平面或 Collider；支持场景物体组输出与共享生成材质；增加 X/Y 翻转、单实例位置/缩放覆盖、场景编辑，并为复制的工作区分离预览资源。
- **ProcGen**：增加独立的原点物体组预览、确认/取消流程及随机/固定翻转、分轴缩放；整个 Prefab 列表支持批量拖入；摆放避让纳入现有 Renderer/Collider，可读网格按三角形范围登记占用。
- **SLGMapTileCutter**：增加只提取封闭区域的选项，排除触及图像边界的区域；读取原始线稿像素用于识别，改善轮廓提取精度。
- **SelfShadowBaker**：将额外遮挡物与烘焙目标分别管理，多个目标共同参与遮挡；批量输出名称去重。
- **FogGridClickSystem / Fog Of War.shader**：增加区域显露、重置与恢复调试，以及世界原点和可见目标设置接口；调整迷雾边缘渐变宽度与透明度控制。
- **LightCookieScroller**：增加 Cookie 显示开关；脚本、Shader 和说明统一移到 `Runtime/LightCookieScroller/`。
- **ZXXToolBox**：注册上述 6 个新工具入口。

### 已有 Shader 改进

- **SLG_TerrainBlend_Mobile**：四类地形分别控制色相与饱和度，增加默认关闭的 Unlit 模式。
- **SimpleLit_BottomFade_Wave**：移除独立深度预写 Pass，改为 Forward Pass 写入深度，并调整风动计算。
- **ToonOutline_URP / ToonOutline_URP_Packed**：增加死亡灰化与渐隐支持，补齐所需的 `MiniGameDeathFade.hlsl`。

### 仓库整理与验证范围

- 根据全项目 Git 作者与文件迁移历史同步源码，补齐移动到 ZXXTools 目录外的个人工具及配套文件；原项目路径记录在 [SOURCE_INDEX.md](SOURCE_INDEX.md)，操作说明见 [工具使用说明](Docs/工具使用说明.md)。
- RockTools 运行时类型归入 `Runtime/RockTools/`，Editor 程序集按名称引用 Runtime；保持 `.meta` 不入库，并补齐运行时目录 README。
- 本次上传工具、Shader 和必要依赖源码，不包含模型、贴图、场景、Prefab 或生成资产。
- 已检查源码对应关系、必要 include、程序集引用和目录规则；本次同步未进行 Unity 编译及场景效果验证。

## [v0.5.0] — 2026-08-24

### 新增工具

- **FBXMeshEditor**：FBX 网格编辑器，场景中直接选择并移动静态模型顶点，支持 Undo（V1 第一阶段）
- **ToonMaterialConverter**：ToonOutline 普通/打包 Shader 双向转换，自动输出 BC 与 N_M_R 贴图
- **GeneratedAssetsManager**：按工具、生命周期和资源类型整理、审计并清理 Assets/Generated 产物

### 新增 Shader

- **ToonOutline_URP_Packed**：卡通描边打包版（BC + N_M_R 合并贴图）
- **ToonTextureConversion**：ToonOutline 贴图转换（Hidden 辅助 Shader）

### 变更

- **ToonOutline_URP**：从 OutlineTool 移至通用 Shaders/ 目录
- **PathBuilder**：新增山脉/河流生成与贴花系统（MountainDecalSet、PathBuilderAuthoringStorage），Spline 系统重构
- **BuildingCardTool**：引入 Spine 骨骼动画支持与预览，图集构建改为延迟调用
- **SLGMapTileCutter**：产物输出目录结构优化（分类到 Models/Materials/Textures/Settings 子目录）
- **ZXXToolBox**：面板注册 3 个新工具

---

## [v0.4.0] — 2026-07-30

### 新增

- **PathBuilder**：路径生成工具，含 PathSpline 曲线编辑子系统，替代 WallBuilderTool
- **SLGMapTileCutter**：沙盘地图地块切割工具，含内描边 Shader（5248 行）
- **SLGTerrainBaker**：沙盘地形烘焙工具，合成贴图生成

### 新增 Shader

- **BuildingCard_URP**：建筑面片图集 URP Shader
- **SLG_TerrainBlend_Lite**：SLG 地形混合（轻量版）
- **SLG_TerrainBlend_Mobile**：SLG 地形混合（移动版）
- **SLGMapTileInnerOutline**：沙盘地块内描边
- **SLG_TerrainComposite_Baker**：地形合成烘焙

### 变更

- **FogOfWarTool**：战争迷雾边缘控制优化
- **MeasureTool**：测量工具功能增强
- **Fog Of War.shader**：迷雾 Shader 边缘控制
- **FogGridClickSystem**：迷雾点击交互优化
- **ZXXToolBox**：面板配置更新，注册新工具

### 移除

- **WallBuilderTool**：被 PathBuilder 替代

---

## [v0.3.1] — 2026-07-15

### 新增

- **面板优化**：ZXXToolBox 面板交互细节增强
- **工具截图参考**：新增 19 个工具面板操作截图

### 变更

- **.gitignore**：忽略 ProcGenSettings.asset（工具自动生成配置）

---

## [v0.3.0] — 2026-07-15

### 新增

- **BuildingCardTool 图集面板**：AtlasPanel 批量管理多张建筑面片，渲染顺序优化
- **全工具 Tooltip**：Content() 双语提示，所有按钮/标签带中文说明

### 变更

- **ZXXToolBox 分类重构**：生成工具 → 场景搭建 / 资源制作 / 视觉烘焙
- **英文注释中文化**：BuildingCardTool / SelfShadowBaker / ShadowBaker 英文 XML doc 改为中文

---

## [v0.2.0] — 2026-07-14

### 新增

- **DayChangeTransitionTool**：昼夜变化过渡编辑器工具 + Runtime Controller
- **FogOfWarTool**：战争迷雾编辑器工具 + Runtime ClickSystem + Fog Shader
- **BuildingCardTool**：建筑面片预制体批量生成

### 变更

- **OutlineTool**：`Outline_URP` → `ToonOutline_URP`，升级为卡通渲染描边
- **ZXXToolBox**：统一所有工具入口到面板，移除分散的 `[MenuItem]`
- **GitPathNavigator**：新增 `Ctrl+G` 快捷键
- **ShadowCastOffTool**：新增 `GameObject/Cast Shadows OFF` 右键菜单
- **PrefabRootNormalizer**：新增 `Ctrl+Shift+N` 快捷键
- **TransparentScreenshot**：新增 `Ctrl+Shift+X` 快捷键
- **ZXXToolBoxConfig**：移除 `CreateAssetMenu`
- **SimpleLit_BottomFade_Wave**：水体/地面优化

### 新增 Shader

- **StylizedRiver**：风格化河流渲染
- **ToonOutline_URP**：卡通渲染描边（替代 `Outline_URP`）

---

## [v0.1.0] — 2026-07-02

### 首次发布

- **21 个编辑器工具**：ZXXToolBox（主面板）及全套工具集
- **9 个 Shader**：阴影、植被、水面、描边、模糊、剪影、迷雾、Light Cookie 等
- **2 个运行时组件**：DayChangeTransitionController、FogGridClickSystem
