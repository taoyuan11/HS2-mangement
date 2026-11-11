# C++ 业务核心迁移覆盖

本目录已完成业务核心的 C++20 移植，覆盖卡库、编辑、依赖索引和四级人物卡导出。以下记录本模块的实际测试范围；应用界面、人物组装、渲染和模型导出的验收由对应模块及项目验证文档记录。

| 原模块 | 原生实现 | 当前验证 |
| --- | --- | --- |
| Models / AppSettings / AtomicFile | Core.h / Core.cpp | 原 JSON 字段与数值枚举、原子写入、SHA-256 参考向量、设置往返 |
| BoundedMessagePack / EditableMessagePack | MessagePack.cpp | 精确字节往返、整数宽度、UTF-8、未知扩展、重复键、越界与深度上限 |
| CardParser | CardData.cpp | PNG/损坏/普通图片分类、卡内姓名/性别、Sideloader 二进制嵌套引用；真实人物卡 |
| CharacterDocument | CardData.cpp | 未编辑原样输出，封面/未知块/块间空隙/块尾与文件尾保留，局部修改重编码 |
| CardRepository | Repository.cpp | 原 `library.db` Cards/Metadata 表与 JSON、WAL、备份、扫描保留用户元数据；拒绝网络数据库 |
| CardLibraryService | Repository.cpp | 增量、递归、隔离排除、离线保留、浅层扫描保留子目录记录、取消 |
| CardFileService / CardFileDeletion | FileService.cpp | 完整 PNG 导入导出及哈希、移动/改名、隔离/恢复、版本核对与排他删除、恢复日志 |
| CardEditService | EditService.cpp | 另存、备份覆盖、替换冲突检测、恢复备份及提交后补齐索引 |
| CharacterEditSession / Rules / AssetReferences | CardData.cpp / EditRules.cpp | 撤销重做、手势合并、联动规则、Sideloader 资源引用与恢复；组件默认值和能力规则另由下项覆盖 |
| InitializeAsset / CharacterFieldCapabilities | CharacterEditComponents.cpp | 发型骨骼默认比率、范围裁剪、配饰颜色默认值、按组件能力显示参数 |
| CharacterEditSchema | EditSchema.cpp | 逐项移植原字段与范围，真实卡生成 974 个字段并验证存在 |
| ModDependencyService / NativeModSelection / NativeModCategories | ModIndex.cpp | 真实 ZIPMOD、DTD 拒绝、版本选择、迁移类别/slot、未知与离线状态 |
| PluginFileIndex | PluginIndex.cpp / CliMetadata.h | 只读 ECMA-335，真实 HS2API 插件 GUID/版本及 19 项程序集依赖；不执行 DLL |
| CardExportService / Dependencies / Resources | CardExport.cpp / CardExportDependencies.cpp | 四级计划、依赖、输出隔离/冲突、清单与恢复复制；离线四级输出、源版本冲突、取消与报告测试通过 |

验证包含合成夹具和真实人物卡、ZIPMOD、HS2API 插件输入，共 1,062 项断言通过。四级导出测试覆盖离线资源、源文件变更、冲突、取消和报告；实际资源默认值与编辑界面的端到端检查属于应用层测试。该计数不代表已对全部 84 张历史基线执行本模块的全部操作，也不作为完整 NAS 性能基准。

当前实现无托管运行时依赖。运行时使用 Windows `winsqlite3`、BCrypt、XmlLite 和系统文件 API；ZIP 读取使用 `NativeScene/ZipArchive`。人物文件解析采用 512 MiB 上限的有界内存，与旧扫描器逐块读取方式不同。NAS 数据位置迁移由应用层负责。

独立构建测试：

```powershell
cmake -S src/HS2Manager.NativeCore -B <本机构建目录> -G 'Visual Studio 17 2022' -A x64
cmake --build <本机构建目录> --config Release -- /m:2 /nr:false
ctest --test-dir <本机构建目录> -C Release --output-on-failure
```

`hs2_core_checks.exe` 可附加人物 PNG、ZIPMOD、插件 DLL 三个路径，均只读真实输入。所有写入使用独立临时夹具，不修改本机游戏。

第三方 JSON：`third_party/json.hpp` 固定为 nlohmann/json v3.12.0，来自 https://github.com/nlohmann/json/tree/v3.12.0 ，MIT 许可证保存在 `third_party/json.LICENSE.MIT`。
