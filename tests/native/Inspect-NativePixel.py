"""通过 qrenderdoc --python 读取当前任务拥有的采集像素历史。"""

import json
import os
from pathlib import Path
import traceback
import renderdoc as rd

output = Path(os.environ["HS2_GPU_REPORT"])
capture = controller = None
try:
    capture = rd.OpenCaptureFile()
    status = capture.OpenFile(os.environ["HS2_GPU_FRAME"], "", None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError(str(status))
    status, controller = capture.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError(str(status))
    resource = next(
        t.resourceId
        for t in controller.GetTextures()
        if str(t.resourceId) == "ResourceId::" + os.environ["HS2_GPU_RESOURCE"]
    )
    x, y = map(int, os.environ.get("HS2_GPU_PIXEL", "630,375").split(","))
    history = controller.PixelHistory(resource, x, y, rd.Subresource(), rd.CompType.Float)
    resources = {r.resourceId: r.name for r in controller.GetResources()}
    rows = []
    for modification in history:
        controller.SetFrameEvent(modification.eventId, False)
        pipeline = controller.GetPipelineState()

        def color(value):
            return list(value.col.floatValue)

        flags = {
            n: str(getattr(modification, n))
            for n in dir(modification)
            if not n.startswith("_")
            and not callable(getattr(modification, n))
            and isinstance(getattr(modification, n), (bool, int))
        }
        rows.append(
            dict(
                event=modification.eventId,
                shader=resources.get(pipeline.GetShader(rd.ShaderStage.Pixel), ""),
                pre=color(modification.preMod),
                post=color(modification.postMod),
                shaderOut=color(modification.shaderOut),
                preDepth=modification.preMod.depth,
                postDepth=modification.postMod.depth,
                shaderDepth=modification.shaderOut.depth,
                flags=flags,
            )
        )
    output.write_text(json.dumps(rows, indent=2), encoding="utf8")
except BaseException:
    output.with_suffix(".error.txt").write_text(traceback.format_exc(), encoding="utf8")
finally:
    if controller:
        controller.Shutdown()
    if capture:
        capture.Shutdown()
    output.with_suffix(".complete").write_text("complete", encoding="utf8")
