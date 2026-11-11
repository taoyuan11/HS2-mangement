"""Inspect one shader invocation in an owned GPU capture."""
import json,math,os,traceback
from pathlib import Path
import renderdoc as rd
output=Path(os.environ['HS2_GPU_REPORT']);capture=controller=trace=None
try:
    capture=rd.OpenCaptureFile();status=capture.OpenFile(os.environ['HS2_GPU_FRAME'],'',None)
    if status!=rd.ResultCode.Succeeded:raise RuntimeError(str(status))
    status,controller=capture.OpenCapture(rd.ReplayOptions(),None)
    if status!=rd.ResultCode.Succeeded:raise RuntimeError(str(status))
    event=int(os.environ['HS2_GPU_EVENT']);controller.SetFrameEvent(event,True)
    x,y=map(int,os.environ['HS2_GPU_PIXEL'].split(','));inputs=rd.DebugPixelInputs()
    if os.environ.get('HS2_GPU_PRIMITIVE'):
        inputs.primitive=int(os.environ['HS2_GPU_PRIMITIVE'])
    trace=controller.DebugPixel(x,y,inputs)
    if trace.debugger is None:raise RuntimeError('No shader debugger returned.')
    def value(v):return dict(name=v.name,rows=v.rows,columns=v.columns,type=str(v.type),float=list(v.value.f32v),uint=list(v.value.u32v))
    initial=[value(v)for v in trace.inputs];steps=[]
    while True:
        states=controller.ContinueDebug(trace.debugger)
        if not states:break
        for state in states:
            changes=[dict(before=value(c.before),after=value(c.after))for c in state.changes]
            if len(steps)<450:steps.append(dict(step=state.stepIndex,instruction=state.nextInstruction,changes=changes))
    output.write_text(json.dumps(dict(inputs=initial,steps=steps),indent=2),encoding='utf8')
except BaseException:output.with_suffix('.error.txt').write_text(traceback.format_exc(),encoding='utf8')
finally:
    if trace and trace.debugger:controller.FreeTrace(trace)
    if controller:controller.Shutdown()
    if capture:capture.Shutdown()
    output.with_suffix('.complete').write_text('complete')
