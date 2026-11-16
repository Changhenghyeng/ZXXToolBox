# Normal Tangent Tool

将模型的平滑法线投影到切线空间，XY 从 [-1,1] 映射到 [0,1] 后写入 `outlineData` UV 集；用于配合 Shader 恢复方向并驱动描边。当前源码使用固定坐标量化分组、切线正交化和逐面顶点 UV 指派。

## 来源与环境

收录桌面 WorkFiles 中的可编辑源码。文件头标注 Maya 2018–2024，依赖 Maya Python API 1.0、maya.cmds、maya.mel，以及 Maya 提供的 PySide2（代码含 PySide6 回退）。这不是已经逐版本验证的兼容性承诺。

Maya scripts 目录里的同名 .py 实际是二进制字节码，因此未纳入源码发布；尚未证明桌面源码与该安装版本完全等同。

## 运行

将本目录加入 Maya 的 Python 搜索路径后，在 Script Editor 的 Python 页执行：

```python
import NormalTangentTool
NormalTangentTool.show()
```

选中多边形模型，点击 Refresh / From Sel 加载，再点击 Process。原脚本也提供拖入视口的安装入口；该入口会复制脚本到用户 scripts 目录并追加 userSetup.py 启动项。仅调用 show() 不安装启动项。Tools 菜单项依赖名为 arx_tools_menu 的菜单已存在，单独使用时可直接调用 show()。

## UV 与导出约定

- 操作会创建/覆盖 outlineData，并可能创建 _outlinePad 垫位 UV；并非只读操作。请先保存模型副本。
- TARGET_IDX=2 只用于不足三套 UV 时补位；已有更多 UV 集时，不能保证新建 outlineData 一定位于 UV3。以日志显示的实际索引和导入结果为准。
- Shader 需先把存储 XY 还原为 [-1,1]，再按相应约定恢复 Z；本次未加入或验证配套 Shader。
- 原界面要求 FBX 导出勾选 Tangents and Binormals，Unity Tangents 设为 Import。不能直接使用本仓库当前关闭切线导出的批量 FBX 工具完成此流程。
- 源码不等于已验证无缝描边效果；本次未测试蒙皮/历史、UV 接缝或不同 Maya 版本。
