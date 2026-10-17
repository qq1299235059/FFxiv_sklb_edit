# Clothing Skeleton Bridge 0.4

这是 FFXIV 服装骨架工作台配套的 Blender 工具。通常无需单独安装：在游戏工作台选择“创建项目并打开 Blender”，工具会在新的 Blender 窗口自动加载。

## 手动安装

下载 `ClothingSkeleton-Blender-0.4.0.zip`，在 Blender 4.5 的“偏好设置 → 插件”中从磁盘安装 ZIP，启用 Clothing Skeleton Bridge。

按 N 打开 3D 视图右侧“服装骨架”面板，点击“手动打开项目”，选择游戏工作台生成的 `Projects/项目/skeleton.gltf`。请保留项目的配套文件，以及项目和游戏插件安装目录的相对关系。

## 编辑与返回

服装原骨与完整人体挂点位于同一骨架。选挂点后可添加三段骨链；已有新链先选第一根，Shift 最后选目标父骨，再 Ctrl+P → 保持偏移。只编辑新增骨骼。

“返回游戏”自动忽略未使用挂点，场景仍保持完整。返回文件固定写入关联游戏插件目录的 `BlenderReturn/edited.gltf`，之后在游戏工作台“回导并检查”。当前格式最多 4 个实际连接，原装备已有连接计入。

该工具需要游戏插件生成的身份与原版参考资料。它不提供独立的 SKLB 转换，也不会为服装自动生成权重、动画或物理。

[完整安装与使用说明](https://github.com/qq1299235059/FFxiv_sklb_edit)
