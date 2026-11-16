# 选中网格批量导出 FBX

收集选中 Transform 及子级中的网格，每个网格输出一个同名 FBX，并显示成功/失败结果。依赖 Maya 自带 Python API 和 FBX 导出插件 fbxmaya。源码头标注 Maya 2020+，本次未逐版本运行验证。

## 使用

先在 Maya 插件管理器加载 fbxmaya，将本目录加入 Python 搜索路径，然后在 Script Editor 的 Python 页执行：

```python
import ExportSelectedMeshesToFBX
ExportSelectedMeshesToFBX.register_menu()
```

通过 Tools > Export Meshes to FBX... 选择输出目录。也可直接调用 `export_selected_meshes_to_fbx()`。

拖入视口或执行 main() 会将脚本复制到用户 scripts 目录，并向 userSetup.py 追加启动项；仅注册菜单不安装启动项。本仓库不提供或覆盖个人 userSetup.py。

## 原有行为与限制

- 导出使用 force=True，同名目标文件会被覆盖；请选用独立的输出目录。
- 会修改当前 Maya FBX 导出选项，不自动恢复旧选项；Tangents 明确设为 false。
- 不适合直接导出 NormalTangentTool 所需的切线数据；此类资产应另用 Maya FBX 导出并开启切线/副切线。
- 使用对象短名称，未额外处理同名对象或带命名空间的文件名。
- register_menu() 会重建 arx_tools_menu；若还需法线工具菜单，应先注册 FBX 菜单再加载法线工具菜单，或直接用各自 Python 入口。

归档源码来自 D:/WorkFile，与 Maya 通用 scripts 目录副本 SHA256 一致。Maya 2020 专属 scripts 目录还存在另一份较简化副本，未重复收录。此次只规范发布副本的行尾空白，不改功能逻辑；未实际执行安装或导出。
