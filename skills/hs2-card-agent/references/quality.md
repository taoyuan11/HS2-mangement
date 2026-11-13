# 参考视图与逐项评估

先查看原图，按实际边界标注，不自动等分多视图排版。`rect` 是相对于整张原图左上角的归一化 `[x,y,width,height]`，不能用右下角坐标替代宽高。每个视图最多一个区域；省略原图未展示的视图。正侧脸区域应包含眉眼、鼻唇和下颌，尽量减少头冠及背景占比。

```json
{
  "session_id": "会话标识",
  "expected_revision": 0,
  "regions": [
    {"view": "front", "rect": [0.04, 0.02, 0.31, 0.94]},
    {"view": "face", "rect": [0.17, 0.10, 0.08, 0.12]}
  ]
}
```

这些数值仅示例，应针对实际图片填写。`hs2_set_reference_regions` 返回裁剪图、原始像素范围和 `detail_limited`。查看裁剪是否完整，不能把放大的模糊人脸当成高精度五官依据。重新标注不会改动人物卡，但会使旧渲染评估失效。

## 看图后修正

`hs2_render` 默认 768×1024、25° 视角、浅灰背景、`side: "left"`，输出 `front/side/back/face/face_side`。侧面方向与参考相反时传 `side: "right"`，不镜像参考图来掩盖发型和配饰的不对称。需要三分之四对照时将 `three_quarter` 加到 views。`background: "game"` 可另外检查游戏背景；默认浅灰只方便看轮廓和透光，不改变素材本身。

每个已标注视图的 `comparison_images` 左侧是参考裁剪，右侧是原生渲染，保持长宽比，不拉伸图像。它们提供比较依据，不输出虚构相似度分数。仍需考虑参考的姿态、透视、光照差异，不能把全图像素差当成角色差异。

脸部默认 `face_zoom: 1.4`、`face_vertical_offset: -0.12`，减少头冠、后脑和颈部占据画面。特殊头型可调整：zoom 为放大倍数，offset 为头部高度的比例，负值向下移动取景中心。先确认五官和下颌完整且在参考中占比相近，再比较滑条效果。灰色背景仍经过游戏光照和后处理，不保证与原图背景亮度相同。

`hs2_probe_fields` 可以用 `delta: 0.03` 做细调，数值是字段范围的比例。脸部默认同时看正脸和侧脸；其他字段默认看全身正侧面。每个字段返回一张对照板，行从上到下为负向、当前、正向，列对应 `columns`；`rows` 记录实际数值。最多六个字段，不消耗修改轮数。存在骨骼／插件修正时必须以实际画面为准。

看图优先比较：

- 脸：脸长宽、下颌宽度、眼睛大小和间距、眉眼位置、鼻梁／鼻尖与嘴唇侧面关系。避免把模板的大眼、小尖下巴当作目标特征。
- 发型：发际、分缝、刘海厚薄、鬓发轮廓、扎发位置、发长和背面层次。硬边大面片不能代表细软发束。
- 身体：头身比、肩宽、腰胯及腿长，在同类取景下判断；姿态不同需说明。
- 衣服：领口、腰线、裙摆开合与长度、布料层次和贴合。正面像不意味着侧面和背面成立。
- 配饰：相对头宽／肩宽的尺度、位置、粗细和垂坠方向，先定位再加花纹。
- 材质：头发的透明边缘、薄纱透光、绸缎／金属／宝石差异、纹样密度与穿插。

## 保存评估

查看当前渲染后调用 `hs2_review_render`，传入该次结果的 `render_id` 和最新 `expected_revision`：

```json
{
  "session_id": "会话标识",
  "expected_revision": 4,
  "render_id": "当前渲染返回的标识",
  "mark_best": true,
  "unseen_regions": "鞋底为补全；其余所述特征在参考中可见",
  "areas": [
    {"area": "face", "status": "needs_work", "reference": "细长杏眼，柔和下颌", "observed": "眼高偏大、下颌过尖", "correction": "先探测眼高和下颌宽度，再小幅调整"},
    {"area": "hair", "status": "needs_work", "reference": "轻薄刘海和分层长发", "observed": "刘海边缘成整块直线", "correction": "更换轻薄刘海候选并比较正侧面"},
    {"area": "body", "status": "matched", "reference": "肩腰比例匀称", "observed": "同类取景下肩腰和腿长接近参考"},
    {"area": "outfit", "status": "needs_work", "reference": "短前裙、飘散长后摆", "observed": "后摆过直", "correction": "调整裙摆宽度、层次和垂坠曲线"},
    {"area": "accessories", "status": "needs_work", "reference": "细小花冠", "observed": "冠宽超过参考相对头宽", "correction": "缩小冠饰并调整挂点"},
    {"area": "materials", "status": "needs_work", "reference": "半透明披袖", "observed": "披袖接近不透明", "correction": "更换透明材质模板并调整贴图 alpha"}
  ]
}
```

每项必须有具体 `reference` 和 `observed`，`needs_work` 还要有可执行 `correction`。`not_visible` 仅表示原图没展示该项，并在说明中注明原因；不能用它跳过做不出来的细节。`matched` 表示达到本次任务的外观目标，不等于像素一致。仍可记录细小差异。

全部项目完成评估后可保存最佳中间稿，哪怕仍有待修项；这使失败尝试可以回退。但任一 `needs_work` 都不会通过最终导出。服务记录评估历史，绑定角色版本、参考区域版本与本次渲染标识，旧结论不能沿用到新画面。
