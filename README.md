# FFXIV 服装骨架工作台

为 **FFXIV 国服服装替换 Mod** 添加自定义骨骼：导出装备附加骨架，在 Blender 编辑新骨链，再回导为 SKLB，通过 Penumbra 预览或安装。

Blender 中会同时显示服装原骨与完整的人体挂点。你可以手动给新骨链指定父级，未用挂点会在导出时自动忽略；原场景保留完整骨架，方便继续编辑。

当前版本：**0.4.0 开发预览版**。已通过 102 项核心检查、真实 Blender 往返和游戏内 Havok 文件转换。多挂点的角色动作随动、外部动画/物理引用和服装加权仍需实际验收。

## 通过 Dalamud 仓库安装

需要已经能够加载 Dalamud 的国服启动器环境。在游戏中输入 `/xlplugins`，打开插件安装器：

1. 打开 **设置 → 实验性 → 自定义插件仓库**。
2. 添加以下地址，并确认已启用该仓库：

   ```text
   https://raw.githubusercontent.com/qq1299235059/FFxiv_sklb_edit/main/repo.json
   ```

3. 保存设置，回到插件列表并刷新，搜索 **服装骨架工作台** 或 **Clothing Skeleton**。
4. 点击安装。无需启用测试版本开关；当前仓库条目就是开发预览版。
5. 输入 `/csw`，按首次指引选择本机 `blender.exe`，保存后进入工作台。

游戏插件已经包含 Blender 工具，通过工作台打开 Blender 时自动加载，通常不需要再手动安装 Blender 插件。

已有开发版的用户请先关闭旧副本，同一时间只启用一份。仓库地址必须使用上面的原始 JSON 地址，不能填写 GitHub 首页或带 `blob` 的文件页面。

## 环境与下载

| 项目 | 当前适配范围 |
| --- | --- |
| 平台 | Windows x64 |
| FFXIV | 国服 `2026.10.06.0000.0000` |
| Dalamud | API 15，已验证 15.0.3.7 |
| Blender | 4.5.3 LTS |
| 预览与实装 | Penumbra，已验证 1.7.2.3 |
| 骨架 | 已识别的简单 `top` 上衣附加骨架，1300/1301 挂点编辑 |

客户端与 API 不匹配时，插件可能不出现在可安装列表中；原生转换也会检查客户端版本。国际服与其他客户端构建尚未适配。

- [Dalamud 插件 ZIP](https://raw.githubusercontent.com/qq1299235059/FFxiv_sklb_edit/main/downloads/0.4.0/ClothingSkeleton-0.4.0.zip)
- [单独的 Blender 插件 ZIP](https://raw.githubusercontent.com/qq1299235059/FFxiv_sklb_edit/main/downloads/0.4.0/ClothingSkeleton-Blender-0.4.0.zip)
- [文件 SHA256](downloads/0.4.0/SHA256SUMS.txt) · [分发检查](downloads/0.4.0/verification.json)

手动安装作为备用：完整解压 Dalamud ZIP，将 `ClothingSkeleton.dll` 添加为开发插件，保留同目录下的核心程序与 `BlenderBridge` 文件夹。

## 三步使用

### 1. 准备骨架

输入 `/csw`，在“准备骨架”中选择来源。首次建议使用 **原版中原女 · 奥丁神铠**，点击 **创建项目并打开 Blender**。也可以选择其他游戏骨架，或本地 SKLB 文件并填写对应的游戏替换路径。

人体挂点来自同种族性别的原版基础骨架，不读取人物 Mod 附带的基础骨架。游戏资源由使用者在自己的设备上读取，仓库与下载包不附带游戏骨架。

### 2. 在 Blender 中编辑

在 3D 视图按 **N**，打开右侧 **服装骨架** 面板。选择 `服装骨架_编辑此对象`，按 **Tab** 进入编辑模式。

- **直接创建**：选中目标父骨，例如左手 `j_te_l` 或右手 `j_te_r`，点击 **添加三段骨链**。工具生成三个骨段和末端节点，第一段挂在活动骨下面。
- **手动改父级**：取消其他选择，只选择新链第一根；按住 Shift 最后选择目标挂点，使其成为活动骨，执行 **Ctrl+P → 保持偏移**。后续骨段继续保留链式父级。
- 不同骨链可以分别挂到左右手，多条链也可共用同一个挂点。只编辑新增骨骼，保留原服装骨骼与人体挂点的名称和静止姿态，不要变换整个骨架对象。

点击 **返回游戏**。只有实际作为新骨父级的挂点才进入返回文件；其他挂点及辅助祖先自动忽略，Blender 场景保持完整。中原女样本提供 106 个可选人体骨骼。

当前已验证的格式最多支持 **4 个不同的实际人体连接，原装备已有连接也计入**。奥丁模板已占 1 个，可再用 3 个不同挂点；多条链共用一个挂点只占一个连接。

### 3. 回导、检查与实装

切回游戏，点击 **回导并检查**，核对原骨、新增骨和所用挂点。通过后，穿上对应装备，使用 **在当前角色上预览**，并可随时 **撤销预览并恢复**。预览使用当前角色的 Penumbra 集合，该集合的其他使用者也会受到影响。

确认后可以 **发送到 Penumbra** 或 **仅保存 Mod 安装包**。默认奥丁模板会生成中原女对应的骨架关联；其他模板需确保装备原有的附加骨架关联正确。使用 **检查游戏内是否已加载** 可核对实际资源和新增骨名。

骨架检查通过不代表服装已经加权。网格需要绑定到相应新骨，才会跟随新骨变形；本插件当前不写回 MDL、不自动制作权重、动画或物理链。试用多挂点时需分别检查左右臂和手腕，以及原服装骨链的行为。

## 单独安装 Blender 工具

推荐使用游戏工作台的“打开 Blender”，工具会自动加载。若需要在已有 Blender 窗口中工作，可下载上面的 Blender ZIP，在 **偏好设置 → 插件** 中从磁盘安装并启用 **Clothing Skeleton Bridge**。

在“服装骨架”面板选择 **手动打开项目**，打开游戏插件生成的 `Projects/项目/skeleton.gltf`，保留项目与游戏插件目录的相对关系。单独的 Blender 工具负责编辑和返回，原生 SKLB 转换仍由游戏插件完成。

## 返回位置、更新与常见问题

返回文件固定为 **当前游戏插件目录下的 `BlenderReturn/edited.gltf`**，无需手动设置。所有项目共用该位置，插件会检查项目身份和完成哈希。本机设置、Projects、BlenderSession 均由当前安装生成，不在下载包中。

- **列表中找不到插件**：确认仓库已启用，使用原始 JSON 地址，网络能访问 GitHub 与 raw.githubusercontent.com；再核对客户端及 Dalamud API 版本。
- **提示 Blender 地址无效**：在工作台的“使用指引与 Blender 设置”重新选择本机程序。换设备后需要重新设置。
- **看不到新挂点流程**：用 0.4 新建项目并打开新的 Blender 窗口，旧窗口不会自动更新工具。
- **回导被拒绝**：恢复误改的原骨或挂点，为新链第一根指定父级，并使用面板“返回游戏”重新导出。
- **升级或移动安装目录**：先备份需要的 Projects。不同版本可能使用不同安装目录；在新安装中重新设置 Blender 并新建项目。不要把用过的整个插件目录重新打包分发。

完整步骤见 [使用指南](docs/分发使用指南.md)。问题反馈请使用本仓库 [Issues](https://github.com/qq1299235059/FFxiv_sklb_edit/issues)，附客户端、Dalamud、Blender 版本和可复现步骤；分享截图或日志前移除个人目录信息。

## 开发与资料

| 目录 | 内容 |
| --- | --- |
| `src/ClothingSkeleton.Core` | SKLB、glTF、身份校验、多挂点计划和 Havok XML |
| `src/ClothingSkeleton.Plugin` | Dalamud 界面、原生转换和 Penumbra 集成 |
| `blender/clothing_skeleton_bridge` | Blender 工具源码 |
| `tests/ClothingSkeleton.Tests` | 102 项核心检查 |
| `tools` | 本地构建、打包、发布准备与验证工具 |
| `downloads/0.4.0` | 清理过的可安装文件、指南和哈希 |

构建需要匹配的国服 Dalamud 开发程序集。`python tools/bootstrap_sdk.py` 准备项目本地 .NET SDK，再运行 `./tools/build.ps1 -Test`、`./tools/build.ps1 -Release` 和 `python tools/package_release.py`。原生测试需要对应客户端；开发文件指令默认关闭。

[开发交接](docs/开发交接.md) · [技术参考](docs/技术参考.md) · [仓库更新说明](docs/仓库维护.md) · [Dalamud 自定义仓库格式](https://dalamud.dev/plugin-publishing/custom-repositories/)
