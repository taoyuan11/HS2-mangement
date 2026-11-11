"""在独立应用中解码导出的 HDR 和 LUT 图像，并报告线性空间误差。

运行方式：Blender --background --factory-startup --python SCRIPT -- REFERENCE ACTUAL REPORT。
像素测量不能替代截图或目标场景的视觉审查。"""

import json
import sys
from pathlib import Path

import bpy
import numpy as np

reference, actual, output = map(Path, sys.argv[sys.argv.index("--") + 1 :])
rows = []
errors = []
for name in ["Environment/lighting.hdr", "Environment/reflection.hdr", "Environment/grading.exr", "预览参考.png"]:
    images = []
    try:
        for folder in [reference, actual]:
            image = bpy.data.images.load(str((folder / name).resolve()), check_existing=False)
            images.append(image)
            # 直接在文件数据空间比较数值，不应用显示变换。
            image.colorspace_settings.name = "Non-Color"
        if tuple(images[0].size) != tuple(images[1].size):
            raise ValueError("Image dimensions changed: " + name)
        values = []
        for image in images:
            pixels = np.empty(len(image.pixels), dtype=np.float32)
            image.pixels.foreach_get(pixels)
            pixels = pixels.reshape(image.size[1], image.size[0], 4)
            if not np.isfinite(pixels).all():
                raise ValueError("Nonfinite exported image: " + name)
            values.append(pixels[:, :, :3])
        difference = np.abs(values[0] - values[1])
        rows.append(
            {
                "Name": name,
                "Width": images[0].size[0],
                "Height": images[0].size[1],
                "PixelExact": not bool(difference.any()),
                "MeanChannelError": difference.mean(axis=(0, 1)).tolist(),
                "MaximumChannelError": difference.max(axis=(0, 1)).tolist(),
                "ReferenceRange": [float(values[0].min()), float(values[0].max())],
                "ActualRange": [float(values[1].min()), float(values[1].max())],
            }
        )
    except (RuntimeError, OSError, ValueError) as error:
        errors.append(str(error))
    finally:
        for image in images:
            bpy.data.images.remove(image)
report = {
    "SchemaVersion": 1,
    "Decoder": "Blender " + bpy.app.version_string,
    "Reference": str(reference),
    "Actual": str(actual),
    "Images": rows,
    "Errors": errors,
    "DecodedAll": len(rows) == 4 and not errors,
    "VisualReviewPassed": False,
    "VisualParityVerified": False,
}
output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf8")
print(json.dumps(report, ensure_ascii=False))
if not report["DecodedAll"]:
    raise RuntimeError("Environment image evidence is incomplete.")
