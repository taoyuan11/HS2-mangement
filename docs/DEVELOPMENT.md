# 项目结构与代码规范

## 目录职责

根目录保留解决方案、统一配置以及启动和发布命令；构建实现统一放在 `scripts`。生产代码按模块划分，每个模块内部再按业务职责组织；同一功能的头文件和实现文件放在一起。

```text
HS2-mangement/
    src/
        HS2Manager.NativeApp/          WinUI 界面与交互
            Application/               程序入口、主窗口和人物卡操作
            Editor/                    编辑状态、素材选择和备份
            Export/                    人物卡与模型导出交互
            Infrastructure/            数据位置、布局、事件及绑定支持
            Preview/                   预览界面与渲染工作线程
            Views/                     XAML 视图和共享样式
            Assets/                    图标等界面资源
        HS2Manager.NativeAgent/        独立 C++ MCP 服务、生成会话、渲染与素材构建
        HS2Manager.NativeAssetBuild/   原生素材配方、模型导入与 UnityFS 资源包写入
            ThirdParty/                保持上游原样的 cgltf 依赖
        HS2Manager.NativeCore/         人物卡管理与数据服务
            Common/                    公共类型和基础工具
            Editing/                   编辑规则、字段与组件
            Export/                    人物卡及关联依赖导出
            Library/                   资料库与文件操作
            Mods/                      MOD 和插件索引
            Preview/                   临时会话、资源上下文及目录租约
            Serialization/             人物卡、MessagePack 和元数据解析
            third_party/               保持上游原样的 JSON 依赖
        HS2Manager.NativeScene/        资源解析、角色装配与运行状态
            Animation/                 动画、骨架及姿态控制
            Archive/                   ZIP 资源读取及 7-Zip 安全流式解压
            Assets/                    游戏资源读取和结构解析
            Character/                 角色目录、扩展与装配准备
            Environment/               环境定义和读取
            Runtime/                   动态骨骼、布料和运行状态
            Scene/                     场景节点、数值类型与 XML
            Resources/                 类型数据库
            ThirdParty/                保持上游原样的解压依赖
        HS2Manager.NativeRendering/    Direct3D 11 渲染
            Common/                    渲染数学工具
            Device/                    图形设备与 GPU 资源
            Geometry/                  GPU 网格形变
            Lighting/                  天空盒和阴影
            Materials/                 材质通道与角色外观合成
            Pipeline/                  帧绑定、前向及延迟管线
            PostProcessing/            抗锯齿、动态及屏幕后处理
            Shaders/                   着色器读取和元数据
        HS2Manager.NativeExport/       模型导出与转换
            Baking/                    GPU 烘焙与 PBR 着色器
            Fbx/                       FBX SDK 调用封装
            Images/                    图像编码与写入
            Model/                     模型、角色和场景导出
            Tools/                     转换工具与受控进程
            Resources/ModelExport/     Blender 和 Unreal 转换脚本
    native/                            独立 FBX 与 PhysX 适配层
    scripts/
        assets/                        品牌资源生成
        build/                         C++ 应用与原生适配层构建
        development/                   源码启动与格式化
        package/                       运行库复制、发布与包检查
        release/                       版本提交、标签推送与 GitHub Release 发布
    tests/
        HS2Manager.Cpp*Checks/         按模块划分的 C++ 回归检查
        NativeFbxCompare/              独立 FBX 文件对照
        export/                        Blender、Unreal 与导出验证
        native/                        原生渲染、布料和采集诊断
        performance/                   性能对比
        ui/                            WinUI 启动和退出检查
        unity/                         历史参考画面与基准工具
        fixtures/                      冻结数值基准，不随格式化改写
    docs/                              使用说明、设计及验证记录
    licenses/                          第三方许可原文
    .tools/                            本机 SDK 和临时工具，不入库
    artifacts/                         构建与发布产物，不入库
```

CMake 与 Visual Studio 的模块名称及库输出位置保持稳定。公共头文件通过模块根目录引入，并写明职责路径，例如 `Character/CharacterDefinition.h`、`Editing/CharacterEditComponents.h`。新增文件时同步维护对应的 `CMakeLists.txt` 或 `HS2Manager.NativeApp.vcxproj`，避免依赖递归源码扫描。

## 排版与注释

- 所有自有代码均使用空格缩进，每级 **4 个空格**；Tab 显示宽度同样为 4。
- C++ 的命名空间、类、函数及代码块使用多行大括号；函数和类型定义之间保留空行，不将多个语句压在同一行。
- Python 函数和类遵循 Black 排版；PowerShell 函数独立分行，普通语句及哈希表条目不使用分号压缩。
- 自有代码注释与文档字符串使用中文，说明原因、约束及兼容行为。标识符、协议名称、公式和文档链接可保留原文。
- 常规代码按 120 列排版；长路径、资源标识和不能拆分的字符串可以超出。
- 文本统一采用 UTF-8；PowerShell 使用带 BOM 的 UTF-8，兼容 `start.cmd` 调用的 Windows PowerShell 5.1。普通文本采用 LF，`.cmd` 和 `.sln` 采用 CRLF。
- 第三方源码、许可原文和冻结测试数据不参与自动格式化，保留上游及基准内容。

编辑器默认规则位于根目录 `.editorconfig`。C++、CMake、Python、PowerShell 的规则分别位于 `.clang-format`、`.cmake-format.json`、`pyproject.toml` 和 `PSScriptAnalyzerSettings.psd1`。

## 统一格式化

准备 Python 3.10 或以上、clang-format 22，以及下列固定版本的格式工具：

```powershell
python -m pip install black==26.10.0 cmakelang==0.6.13
Install-Module PSScriptAnalyzer -RequiredVersion 1.24.0 -Scope CurrentUser
```

在仓库根目录执行：

```powershell
# 修改自有代码的排版
.\scripts\development\Format-Code.ps1

# 只检查，发现未格式化文件时返回失败
.\scripts\development\Format-Code.ps1 -Check

# 使用独立环境或已下载的工具
.\scripts\development\Format-Code.ps1 -Check `
    -Python 'C:\Tools\format-env\Scripts\python.exe' `
    -ClangFormat 'C:\Program Files\LLVM\bin\clang-format.exe' `
    -AnalyzerModule 'C:\Tools\PSScriptAnalyzer\PSScriptAnalyzer.psd1'
```

格式工具仅处理 Git 管理或尚未忽略的源码文件，不遍历本机 SDK、构建输出或游戏数据。C++ 内嵌 HLSL 和 XAML/XML 也统一排版。

## 构建与验证

根目录提供 `start.cmd`（构建并启动）和 `push-releases.cmd <版本号>`（提交全部未忽略改动并推送正式版标签）。本地构建和打包使用下面的脚本；不再提供桌面部署功能。

```powershell
# 并行编译原生模块、运行 CTest、构建 WinUI 和审计运行库，不启动 GUI 测试
.\scripts\build\Build-Cpp.ps1 -Jobs 4

# 仅构建和验证底层模块
.\scripts\build\Build-Cpp.ps1 -SkipUi

# 检查并行发布的文件内容、清单和失败处理
.\tests\package\Test-ParallelPackage.ps1
```

GUI 启动和退出检查通过 `tests/ui/Invoke-CppWinUiSmoke.ps1 -Executable <程序路径> -FixtureTool <hs2_ui_fixtures.exe 路径>` 单独执行，不在构建或打包中运行。

进程退出策略位于 `Infrastructure/ProcessLifetime`，必须先于 COM／XAML 初始化。正常退出先停止自有线程、关闭 SQLite 并释放 UI 对象，最后结束本进程，避免第三方 `DLL_PROCESS_DETACH` 在 Windows 已停止其线程后再次等待。不得直接在业务操作中调用 `FinishProcess`；只有清理完成或致命异常／退出超时时才能使用。30 秒期限不覆盖用户选择保存／放弃／取消的等待，关闭被否决时必须撤销。该策略只修改本进程的 WER 标志，不修改系统注册表或结束其他进程。

`process_exit_lifetime` 使用独立进程检查正常结束、卸载会阻塞的 DLL、重复关闭期限、取消期限、原生异常、绕过异常过滤器的快速失败，以及主进程被强制结束后的模型转换子进程回收。检查同时验证进程及 WER 克隆不再存在、程序和 DLL 目录能完整删除。GUI 冒烟检查另覆盖强制结束／重启、首次扫描中关闭、80 张人物卡资料库重启，并完整删除独立的运行库副本。

便携目录和 ZIP 只携带 Windows App SDK 的 `zh-CN` 简体中文 MUI 资源及 `en-US` 英文回退资源，其他语言的独立 MUI 文件在发布复制时排除；开发构建目录仍可包含 SDK 的完整语言资源。`Test-CppPackage.ps1` 会检查必需的中英文资源，并拒绝发布包中多余的语言文件。公共 PRI、XAML 和运行库保留完整。

真实游戏资源、GPU、FBX 或 PhysX 相关检查需要相应的本机依赖。历史画面对照和外部软件验证脚本已按职责放入 `tests` 的子目录，具体参数和样本范围见各验证文档。测试和工具必须等待其启动的子进程退出，并在失败或取消时完成清理。

## 依赖与旧产物清理

`packages.lock.json` 只锁定当前构建导入及其传递依赖。WebView2 仍由 WinUI 的元数据和构建目标引用；独立的 `Microsoft.WindowsAppSDK.DWrite` 开发包未被导入，已移除。界面所需的 `DWriteCore.dll` 仍由 Windows App SDK 自包含运行库提供，不应从发布目录删除。

VC++ 运行库按应用、适配层及 Windows App SDK 的普通／延迟导入核对，程序目录只复制 `msvcp140.dll`、`msvcp140_atomic_wait.dll`、`vcruntime140.dll` 和 `vcruntime140_1.dll`；适配层目录保留所需的三个非 `atomic_wait` 文件。更新编译器或 SDK 后需重新核对导入。运行库复制同时清理旧输出中的六个无引用 VC++ DLL 和独立的 64 像素 PNG，包检查拒绝这些文件回流。64 像素图像仍保留在多尺寸 ICO 内。

旧 C# / Unity 工具链、参考工程的编译缓存及对应许可文本已退出当前依赖集合，可在 Git 历史中查看原实现和许可。清理本机旧输出时保留其中的 `Data`、备份和操作日志。`tests/fixtures` 的冻结样本、`tests/unity` 的画面对比脚本及历史验证记录仍用于回归对照；PhysX、原生 FBX SDK、RenderDoc 和 Python 格式化／图像比较工具也继续保留。

原生素材构建也不再依赖旧 Agent 的 Unity 2018.4 编辑器及安装包。`.tools/agent-tools` 中的旧编辑器可清理；素材工程、已生成素材、游戏验收数据与本机配置需分别核对用途，不能随工具链一并删除。
