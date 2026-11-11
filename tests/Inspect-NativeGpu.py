"""Run through qrenderdoc --python to inspect an owned offline reference capture."""
import json
import os
import pathlib
import traceback
import renderdoc as rd

output = pathlib.Path(os.environ["HS2_GPU_REPORT"])
capture = controller = None

def props(value):
    return {n: (getattr(value, n).Name() if n == "format" else str(getattr(value, n))) for n in dir(value)
            if not n.startswith("_") and not callable(getattr(value, n))}

def variable(value):
    return dict(name=value.name, type=str(value.type), rows=value.rows,
                columns=value.columns, value=list(value.value.f32v),
                members=[variable(v) for v in value.members])

def flatten(actions):
    for action in actions:
        yield action
        yield from flatten(action.children)

try:
    output.parent.mkdir(parents=True, exist_ok=True)
    output.with_suffix(".progress").write_text("opening capture", encoding="utf-8")
    capture = rd.OpenCaptureFile()
    status = capture.OpenFile(os.environ["HS2_GPU_FRAME"], "", None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError(str(status))
    status, controller = capture.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError(str(status))
    output.with_suffix(".progress").write_text("replay ready", encoding="utf-8")
    resources = {r.resourceId: r.name for r in controller.GetResources()}
    textures = {t.resourceId: dict(name=resources.get(t.resourceId, ""),
                 width=t.width, height=t.height, depth=t.depth, array=t.arraysize,
                 mips=t.mips, format=t.format.Name()) for t in controller.GetTextures()}
    diagnostic_textures=[]
    for resource,info in textures.items():
        if (info["width"],info["height"],info["depth"]) in [(128,2,1),(33,33,33),(4,4,16),(256,64,1)]:
            file=output.parent/("resource-"+str(resource).replace("ResourceId::","")+".bin")
            file.write_bytes(controller.GetTextureData(resource,rd.Subresource()))
            diagnostic_textures.append(dict(info,file=str(file)))
    actions, details = [], []
    selected_events={int(v)for v in os.environ.get('HS2_GPU_EVENTS','').split(',')if v}
    structured = controller.GetStructuredFile()
    for action in flatten(controller.GetRootActions()):
        if not action.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch):
            continue
        if selected_events and action.eventId not in selected_events:
            continue
        controller.SetFrameEvent(action.eventId, False)
        pipeline = controller.GetPipelineState()
        state = controller.GetD3D11PipelineState()
        names = [resources.get(s.resourceId, "") for s in
                 [state.vertexShader, state.pixelShader, state.computeShader]]
        active_names = names[2:] if action.flags & rd.ActionFlags.Dispatch else names[:2]
        record = dict(event=action.eventId, name=action.GetName(structured),
                      shaders=names, indices=action.numIndices,
                      shaderIds=[str(s.resourceId) for s in [state.vertexShader,state.pixelShader,state.computeShader]],
                      vertexOffset=action.vertexOffset,indexOffset=action.indexOffset,baseVertex=action.baseVertex,
                      targets=[dict(id=str(v.resource), viewFormat=v.format.Name(), info=textures.get(v.resource))
                               for v in pipeline.GetOutputTargets()],
                      depth=dict(id=str(pipeline.GetDepthTarget().resource),
                                 info=textures.get(pipeline.GetDepthTarget().resource)))
        actions.append(record)
        tokens=os.environ.get('HS2_GPU_FILTER','').split('|') if os.environ.get('HS2_GPU_FILTER') else ["Skin True Face", "hair_07", "Next-Gen Hair", "Clothes True Alpha", "Kermo", "MdgqShader", "Alloy", "Fur", "Item Meka", "ScreenSpaceShadows", "DeferredShading", "DeferredReflections", "MotionVectors", "MotionBlur", "ScreenSpaceReflections", "MultiScale", "CTAA", "FinalPass", "Lut3D", "Skybox", "Uber", "Bloom", "SubpixelMorphological"]
        if not any(token in " ".join(active_names) for token in tokens):
            continue
        detail = dict(record, constants=[], raster=props(state.rasterizer.state),
                      viewports=[props(v) for v in state.rasterizer.viewports],
                      depthState=props(state.outputMerger.depthStencilState),
                      inputs=[props(v) for v in state.inputAssembly.layouts])
        detail["blendState"]=props(state.outputMerger.blendState)
        detail["blendTargets"]=[dict(props(v),colorBlend=props(v.colorBlend),alphaBlend=props(v.alphaBlend)) for v in state.outputMerger.blendState.blends]
        if selected_events or "hair_07" in names[1] or "Next-Gen Hair" in names[1] or "CTAA" in names[1]:
            detail["samplers"]=[dict(props(v),access=props(v.access),descriptor=props(v.descriptor),sampler=dict(props(v.sampler),filter=props(v.sampler.filter))) for v in pipeline.GetSamplers(rd.ShaderStage.Pixel)]
        if selected_events or any(token in names[1] for token in ["Clothes True Alpha", "Kermo", "CTAA", "MotionBlur", "ScreenSpaceReflections", "MultiScaleVO", "ScreenSpaceShadows", "Next-Gen Hair"]) or (os.environ.get('HS2_GPU_FILTER')=='AIT/Skin True' and names[1]=='AIT/Skin True') or ("hair_07" in names[1] and action.numIndices==15000 and record["targets"][0]["info"]):
            detail["resources"]=[dict(props(binding),descriptor=props(binding.descriptor),sampler=props(binding.sampler),info=textures.get(binding.descriptor.resource)) for binding in pipeline.GetReadOnlyResources(rd.ShaderStage.Pixel)]
            for index,binding in enumerate(pipeline.GetReadOnlyResources(rd.ShaderStage.Pixel)):
                info=textures.get(binding.descriptor.resource)
                if info and info["depth"]==1:
                    file=output.parent/("event-%d-resource-%d.bin" % (action.eventId,index))
                    file.write_bytes(controller.GetTextureData(binding.descriptor.resource,rd.Subresource()))
                    detail["resources"][index]["file"]=str(file)
                    requested_mips=[int(v) for v in os.environ.get('HS2_GPU_MIPS','').split(',') if v]
                    if requested_mips:
                        detail["resources"][index]["mip_files"]=[]
                        for mip in requested_mips:
                            if mip>=info['mips']:
                                continue
                            subresource=rd.Subresource();subresource.mip=mip
                            mip_file=output.parent/("event-%d-resource-%d-mip-%d.bin" % (action.eventId,index,mip))
                            mip_file.write_bytes(controller.GetTextureData(binding.descriptor.resource,subresource))
                            detail["resources"][index]["mip_files"].append(dict(mip=mip,file=str(mip_file)))
        if action.flags & rd.ActionFlags.Dispatch and "MultiScale" in names[2]:
            detail["resources"]=[]
            for index,binding in enumerate(pipeline.GetReadWriteResources(rd.ShaderStage.Compute)):
                info=textures.get(binding.descriptor.resource)
                entry=dict(props(binding),descriptor=props(binding.descriptor),info=info)
                if info:
                    file=output.parent/("event-%d-uav-%d.bin" % (action.eventId,index))
                    file.write_bytes(controller.GetTextureData(binding.descriptor.resource,rd.Subresource()))
                    entry["file"]=str(file)
                detail["resources"].append(entry)
        if (selected_events or any(token in names[1] for token in ["Skybox", "Uber", "SubpixelMorphological", "MotionBlur", "FinalPass", "CTAA", "Bloom", "DeferredShading", "DeferredReflections", "ScreenSpaceReflections", "ScreenSpaceShadows"])) and action.flags & rd.ActionFlags.Drawcall:
            detail["target_files"]=[]
            for index,target in enumerate(pipeline.GetOutputTargets()):
                if target.resource == rd.ResourceId.Null():
                    continue
                file=output.parent/("event-%d-output-%d.bin" % (action.eventId,index))
                file.write_bytes(controller.GetTextureData(target.resource,rd.Subresource()))
                detail["target_files"].append(dict(index=index,file=str(file)))
        if selected_events or ((names[1] == "AIT/Skin True Face" or "hair_07" in names[1] or "Next-Gen Hair" in names[1] or "MdgqShader" in names[1] or "Clothes True Alpha" in names[1]) and any(t["info"] and t["info"]["format"] in ["R16G16B16A16_TYPELESS","R16G16B16A16_FLOAT"] for t in record["targets"])) or (names[1] == "Hidden/Internal-MotionVectors" and action.flags & rd.ActionFlags.Drawcall):
            target=pipeline.GetOutputTargets()[0].resource
            (output.parent/("event-%d-target.bin" % action.eventId)).write_bytes(controller.GetTextureData(target,rd.Subresource()))
            detail["vertex_buffers"]=[]
            index_buffer=state.inputAssembly.indexBuffer
            if index_buffer.resourceId != rd.ResourceId.Null():
                index_file=output.parent/("event-%d-index.bin" % action.eventId)
                index_file.write_bytes(controller.GetBufferData(index_buffer.resourceId,0,0))
                detail["index_buffer"]=dict(props(index_buffer),file=str(index_file))
            for index, buffer in enumerate(state.inputAssembly.vertexBuffers):
                if buffer.resourceId == rd.ResourceId.Null():
                    continue
                raw=controller.GetBufferData(buffer.resourceId,0,0)
                file=output.parent/("event-%d-vertex-%d.bin" % (action.eventId,index))
                file.write_bytes(raw)
                detail["vertex_buffers"].append(dict(props(buffer),file=str(file)))
            saved_resources=detail.get("resources",[])
            detail["resources"]=[]
            for index,binding in enumerate(pipeline.GetReadOnlyResources(rd.ShaderStage.Pixel)):
                detail["resources"].append(saved_resources[index] if index<len(saved_resources) else dict(props(binding),descriptor=props(binding.descriptor),sampler=props(binding.sampler),info=textures.get(binding.descriptor.resource)))
                info=textures.get(binding.descriptor.resource)
                if info and info["width"]<=4 and info["height"]<=4 and info["depth"]==1:
                    detail["resources"][-1]["bytes"]=list(controller.GetTextureData(binding.descriptor.resource,rd.Subresource()))
        for stage in [rd.ShaderStage.Vertex, rd.ShaderStage.Pixel, rd.ShaderStage.Compute]:
            reflection = pipeline.GetShaderReflection(stage)
            if not reflection:
                continue
            shader = dict(stage=str(stage), constants=[])
            for i, block in enumerate(reflection.constantBlocks):
                binding = pipeline.GetConstantBlock(stage, i, 0)
                values = controller.GetCBufferVariableContents(
                    pipeline.GetGraphicsPipelineObject(), reflection.resourceId, stage,
                    pipeline.GetShaderEntryPoint(stage), i, binding.descriptor.resource,
                    binding.descriptor.byteOffset, binding.descriptor.byteSize)
                shader["constants"].append(dict(name=block.name, values=[variable(v) for v in values]))
            shader["disassembly"] = controller.DisassembleShader(pipeline.GetGraphicsPipelineObject(), reflection, "")
            detail["constants"].append(shader)
        details.append(detail)
    output.write_text(json.dumps(dict(actions=actions, details=details,diagnostic_textures=diagnostic_textures,
                      textures={str(k): v for k, v in textures.items()}), indent=2), encoding="utf-8")
except BaseException:
    output.with_suffix(".error.txt").write_text(traceback.format_exc(), encoding="utf-8")
finally:
    if controller:
        controller.Shutdown()
    if capture:
        capture.Shutdown()
    output.with_suffix(".complete").write_text("complete", encoding="utf-8")
