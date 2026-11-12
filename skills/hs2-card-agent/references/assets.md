# 新素材制作

调用 `hs2_prepare_asset(session_id, expected_revision)` 导出当前人物的 FBX、Blender 参考、骨架名称、网格尺寸和 UV PNG。该步骤需要本机 FBX 适配层和 Blender。

所有 `hs2_build_asset` 请求含 `session_id`、`expected_revision` 和 `spec`。成功应用后版本递增，需要再次渲染。`asset_key` 标识同一待制作素材，三次尝试共享计数。

## 覆盖贴图

```json
{
    "kind": "texture",
    "asset_key": "reference-face-makeup",
    "target": "face",
    "source_image": "C:/Work/face-overlay.png"
}
```

`target` 支持 `face`、`body`、`clothes`。用导出的 UV 模板作为绘图参考，生成透明背景 PNG。图片最大 32 MiB、单边 8192、总像素 3200 万。

服装覆盖还需 `clothing_name`，取值为 `ct_clothesTop`、`ct_clothesBot`、`ct_inner_t`、`ct_inner_b`、`ct_gloves`、`ct_panst`、`ct_socks`、`ct_shoes`；`replace` 控制替换原贴图或叠加。贴图写入人物卡 KSOX/KCOX 数据。目标游戏仍需相应 Overlay 插件。

`hs2_search_assets` 的 `category: -1` 返回此前生成的覆盖贴图、用途和本地路径，可作为新构建的 `source_image` 复用。

## 配饰、发型与服装

```json
{
    "kind": "hair",
    "asset_key": "reference-back-hair",
    "name": "参考图后发",
    "target_path": "hair.parts[0].id",
    "source_script": "C:/Work/create-hair.py",
    "template_material": "Assets/Materials/CompatibleHair.mat",
    "textures": [{"property": "_MainTex", "path": "C:/Work/hair.png"}],
    "dynamic_roots": ["agent_hair_root"]
}
```

`kind` 支持 `accessory`、`hair`、`clothes`。`target_path` 使用编辑 Schema 中该类型的素材字段；配饰槽位先通过参数编辑选择正确类别。

内容来源可为 `source_model`（FBX、GLB/GLTF、OBJ），或 `source_script`。脚本由受管 Blender 执行，预置全局变量：

- `HS2_SPEC`：本次构建描述，包括 `work_directory`。
- `HS2_REFERENCE_BODIES`：目标身体网格对象列表。
- `HS2_REFERENCE_ARMATURE`：目标骨架对象。

脚本创建新网格并配置 UV，可向参考骨架增加发束或裙摆骨骼并设置权重。新网格与参考模型区分后自动导出；不要删除参考骨架。未绑定发型默认绑定头骨，未绑定服装按最近表面转移身体权重。自动限制每点四权重并归一化；缺少骨骼、UV、顶点绑定或超过面数预算会失败。

参考场景采用 Blender 的米制世界坐标和 Z 轴向上；一个 HS2 数值单位对应 0.1 米。用对象的 `matrix_world` 计算尺寸和骨骼位置。参考骨架的导入变换已烘焙到骨骼数据；不要额外乘以厘米换算。配饰按挂点局部空间制作，发型和服装按人物参考世界空间制作。导出会保留网格的世界变换，Unity 再恢复游戏的数值单位。

`template_material` 必须指向固定 ModdingTool 工程内的 HS2 兼容材质；先在本机工程内检查实际资源，不使用通用 Standard 材质替代游戏 Shader。`textures` 按 Shader 属性绑定。可选 `shader` 显式选择工程中存在的 Shader；`position`、`rotation`、`scale` 是三分量数组，配饰另设 `parent` 挂点。服装要求 `clothing_name`，对应目标服装部件名。动态根使用 `dynamic_roots` 指定的真实骨骼名称。

服装可用 `masks: [{"field": "OverBodyMask", "path": "C:/Work/body-mask.png"}]` 提供遮罩。支持字段为 `OverBodyMask`、`OverBraMask`、`OverBodyBMask`、`OverInnerTBMask`、`OverInnerBMask` 和 `OverPanstMask`。完整与半脱状态默认复用相同几何，视觉评估必须记录这一限制；复杂的独立半脱造型需要进一步制作对应网格。

Unity 在独立工程副本内创建对应 Cmp 组件、Prefab、资源包及 CSV。原生试加载后才登记 `.zipmod`。构建日志与中间模型留在 Agent 数据目录的 `Builds`，失败结果中提供具体日志路径。

ModdingTool 基线为 `67b460fc880e8c46164781967d2bb06a5d10896d`，Unity 为 `2018.4.11f1`。工具与参考游戏资源由本机提供；生成结果不包含整个制作工程。
