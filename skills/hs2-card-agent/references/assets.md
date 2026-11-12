# 新素材制作

`hs2_prepare_asset(session_id, expected_revision)` 原生导出当前人物的 `reference_model`（FBX）、`reference_scene`（JSON）、`bone_definitions`、网格 `bounds`、UV PNG `images` 和按类别分组的 `templates`。不生成 `.blend`，不调用 Unity／Blender。参考场景使用 HS2 左手坐标，Y 向上，每单位 0.1 米；骨骼位置、四元数和完整绑定矩阵均相对于人物根，矩阵保留骨骼缩放。

`hs2_build_asset` 请求包含 `session_id`、最新 `expected_revision` 和 `spec`。成功后版本递增；重新渲染并确认外观。每个 `asset_key` 标识同一待制作素材，最多三次尝试，不通过改名绕过预算。耗时工具返回 `job_id`，用 `hs2_get_job` 查询。

## 覆盖贴图

```json
{
    "kind": "texture",
    "asset_key": "reference-face-makeup",
    "target": "face",
    "source_image": "C:/Work/face-overlay.png"
}
```

`target` 支持 `face`、`body`、`clothes`。根据 UV PNG 生成带透明度的 PNG，最大 32 MiB、单边 8192、总像素 3200 万。服装覆盖还需 `clothing_name`：`ct_clothesTop`、`ct_clothesBot`、`ct_inner_t`、`ct_inner_b`、`ct_gloves`、`ct_panst`、`ct_socks`、`ct_shoes`。`replace` 控制替换或叠加。结果写入人物卡 KSOX/KCOX，目标游戏仍需对应 Overlay 插件。

`hs2_search_assets` 的 `category: -1` 返回已生成覆盖贴图，可将其路径作为 `source_image` 复用。

## 配饰、发型与服装

```json
{
    "schema_version": 2,
    "kind": "accessory",
    "asset_key": "reference-head-accessory",
    "name": "头部配饰",
    "target_path": "accessory.parts[0].id",
    "template_asset_id": "从搜索或参考结果选择的标识",
    "parent": "N_Head",
    "recipe": {
        "operations": [
            {"op": "primitive", "primitive": "sphere", "name": "ornament",
             "radius": 0.4, "segments": 24, "rings": 16, "position": [1, 1.2, 0]}
        ]
    }
}
```

`kind` 为 `accessory`、`hair` 或 `clothes`。`target_path` 必须来自人物编辑 Schema；配饰先用参数编辑选好槽位类别。`template_asset_id` 使用搜索返回的完整标识，类别须与槽位一致；省略时使用当前槽位素材。无可用组件／材质模板时返回 `ASSET_TEMPLATE_REQUIRED` 和候选标识，不回退到不兼容 Shader。

`recipe` 和 `source_model` 必须且只能提供一种。`source_model` 支持 FBX、OBJ、GLB、GLTF，单文件最多 256 MiB。FBX 根据文件轴向及单位转换；GLTF 和 OBJ 使用右手 Y 向上、米制输入。GLTF 外部缓冲区须位于模型目录内，不支持 Draco 压缩。对象变换与镜像会烘焙到顶点、法线和形变。素材材质使用所选游戏模板，输入模型的材质槽保留，外部编辑器 Shader 不导入。

配饰使用 `parent` 挂点的局部空间；发型和服装使用人物参考空间。可用全局 `position`、`rotation`、`scale` 三分量数组变换素材；旋转为角度，按 Z、X、Y 顺序应用。负缩放自动调整绕序，零缩放会失败。

## 建模配方

`recipe.operations` 顺序执行，每个新网格的 `name` 必须唯一：

- `primitive`：`primitive` 为 `plane`、`box`、`sphere`、`cylinder`；平面／方盒用 `size: [x,y,z]`，球／圆柱用 `radius`、`rings`、`segments`，圆柱另设 `height`。自动生成 UV。
- `curve`：`points` 控制点，`mode` 为 `ribbon` 发片或 `tube` 管束；`width` 或逐点 `widths`，`subdivisions`，管束 `sides`。可用与控制点同数量的 `bones` 插值绑定。
- `body_surface`：用 `reference_mesh` 指定参考网格，默认身体；`minimum`、`maximum` 裁剪完全位于范围内的三角面，`offset` 沿法线偏移。保留 UV，默认重新从身体转移权重；`transfer_weights: false` 保留原权重。
- `transform`：`mesh` 指定已有网格，加 `position`、`rotation`、`scale`。
- `mirror`：`mesh`、新 `name`、`axis`（X／Y／Z），创建镜像副本。
- `merge`：`meshes` 名称数组与新 `name`，合并并移除输入网格。带形变的网格需直接提供合并后的形变通道。

基本体、曲线和身体表面操作还可设 `anchor` 为参考骨骼名，将局部几何放到该骨骼空间；`bone` 将全部顶点绑定到指定骨骼。

也可在 `recipe.meshes` 直接提供网格：`name`、`positions` 三分量数组、三角形 `indices`、`uv` 二分量数组；可选 `normals`、`tangents` 四分量、`uv1`、RGBA `colors`、逐三角形 `materials` 槽号、逐顶点 `weights: [[{"bone":"cf_J_Head","weight":1}], ...]`。`shapes` 含 `name`、满权重 `weight`（默认 100）、与顶点等长的位移增量 `positions`，可选法线／切线增量。

新增骨骼用 `recipe.bones: [{"name":"strand","parent":"cf_J_Head","position":[0,0.3,0],"rotation":[0,0,0,1]}]`，位置和四元数相对父骨骼。`spec.dynamic_roots` 指定动态链根名称。所有新增骨骼必须连到人物参考骨架；命名不可冲突，父级不可循环。

包含新增骨骼的资源包会声明 BonesFramework 依赖并写入骨骼清单，目标游戏需要该插件。发型的新骨骼分支在写包时接到 `N_hair_Root`，保留参考空间绑定姿态，供游戏发型装配与动态组件正确解析。

未绑定发型默认绑定头骨；服装按身体最近三角面及重心插值转移权重。每顶点合并同名权重、保留最强四项并归一化，重建有效骨骼索引和绑定矩阵。缺少 UV、非法索引、无效骨骼、非有限数或超出预算时失败；`max_triangles` 可设 1–250000，上限一百万顶点。

## 材质、遮罩与交付

模板材质与 Shader 从本机兼容资源克隆，依赖一并复制并重映射引用；Unity 内置资源保留游戏原有的外部引用。`textures: [{"property":"_MainTex","path":"C:/Work/hair.png","material_slot":0}]` 替换实际存在的 Shader 贴图属性；省略 `material_slot` 应用到所有槽。支持 PNG／JPEG，可用 `srgb` 指定颜色空间。

服装 `masks: [{"field":"OverBodyMask","path":"C:/Work/body-mask.png"}]` 支持 `OverBodyMask`、`OverBraMask`、`OverBodyBMask`、`OverInnerTBMask`、`OverInnerBMask`、`OverPanstMask`。半脱默认复用完整服装几何，视觉评估中说明此限制。

构建原生生成网格、贴图、Prefab、Cmp 组件、DynamicBone、LZ4 UnityFS、Sideloader CSV、manifest 和 `.zipmod`。候选包经过回读、引用检查、原生渲染及适配采样后才登记。`Builds` 保存构建描述和 `build-result.json`。取消或失败不提交草稿。

`source_script`、`template_material`、`shader`、`unity_project`、`modding_project` 属于旧管线，返回迁移提示；改用版本 2 配方或模型与素材模板标识。原有草稿和已生成素材仍可使用。原生回读与真实 HS2 验证分别记录，未运行游戏时 `game_validation` 保持 `not_run`。
