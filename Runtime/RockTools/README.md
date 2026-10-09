# RockTools（运行时）

保存岩石生成器、网格构造、生成逻辑和属性定义，以及 RockTools.Runtime 程序集。

- 仓库路径：`Runtime/RockTools/Scripts/`，保持在非 Editor 目录下。
- 原项目路径：`Assets/Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/`；由 asmdef 划分程序集。
- 编辑器窗口、Inspector 和菜单位于 `../../RockTools/Scripts/Editor/`，通过程序集名称引用运行时类型。
- 各 Unity 项目自行生成 .meta，无需复制项目 GUID。
