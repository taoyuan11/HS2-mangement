# 项目结构与代码规范

## 目录职责

根目录保留解决方案、统一配置以及常用构建和启动入口。生产代码按模块划分，每个模块内部再按业务职责组织；同一功能的头文件和实现文件放在一起。

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
        HS2Manager.NativeCore/         人物卡管理与数据服务
            Common/                    公共类型和基础工具
            Editing/                   编辑规则、字段与组件
            Export/                    人物卡及关联依赖导出
            Library/                   资料库与文件操作
            Mods/                      MOD 和插件索引
            Serialization/             人物卡、MessagePack 和元数据解析
            third_party/               保持上游原样的 JSON 依赖
        HS2Manager.NativeScene/        资源解析、角色装配与运行状态
            Animation/                 动画、骨架及姿态控制
            Archive/                   ZIP 读取
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
        build/                         原生适配层构建
        development/                   源码启动与格式化
        package/                       运行库复制、发布与包检查
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

常用入口仍为根目录 `build-cpp.ps1`、`build.ps1`、`build-desktop.ps1` 和 `start.cmd`。

```powershell
# 编译原生模块、运行 CTest、构建 WinUI 并检查窗口启动和退出
.\build-cpp.ps1

# 仅构建和验证底层模块
.\build-cpp.ps1 -SkipUi
```

真实游戏资源、GPU、FBX 或 PhysX 相关检查需要相应的本机依赖。历史画面对照和外部软件验证脚本已按职责放入 `tests` 的子目录，具体参数和样本范围见各验证文档。测试和工具必须等待其启动的子进程退出，并在失败或取消时完成清理。
