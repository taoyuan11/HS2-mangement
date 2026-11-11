"""Replay owned cloth inputs through the standalone bridge and compare a frozen GPU vertex stream."""
import argparse
import ctypes as ct
import json
import os
from pathlib import Path

import numpy as np


def run(args):
    search = os.add_dll_directory(str(args.library.resolve()))
    dependencies = [ct.CDLL(str(args.library / name)) for name in
                    ['PxFoundation_x64.dll', 'PhysX3Common_x64.dll', 'PxPvdSDK_x64.dll', 'PhysX3_x64.dll']]
    dll = ct.CDLL(str(args.library / 'HS2Manager.Physics.dll'))
    pointer, floats, uints = ct.c_void_p, ct.POINTER(ct.c_float), ct.POINTER(ct.c_uint)
    signatures = {
        'create': (pointer, []), 'destroy': (None, [pointer]), 'error': (ct.c_char_p, []),
        'add': (ct.c_int, [pointer, ct.c_int, floats, ct.c_int, uints, floats, floats, floats]),
        'add_cooking': (ct.c_int, [pointer, ct.c_int, floats, ct.c_int, uints, floats, floats, floats, ct.c_int]),
        'update': (ct.c_int, [pointer, ct.c_int, floats, floats, floats, floats, floats, ct.c_int]),
        'particles': (ct.c_int, [pointer, ct.c_int, ct.c_int, uints, ct.c_int, uints, ct.c_int, floats]),
        'step': (ct.c_int, [pointer, ct.c_float]), 'read': (ct.c_int, [pointer, ct.c_int, ct.c_int, floats]),
    }
    for name, (result, arguments) in signatures.items():
        function = getattr(dll, 'hs2_cloth_' + name)
        function.restype, function.argtypes = result, arguments
    def checked(name, *arguments):
        value = getattr(dll, 'hs2_cloth_' + name)(*arguments)
        if value < 0:
            raise RuntimeError(dll.hs2_cloth_error().decode('utf8'))
        return value
    f = lambda values: (ct.c_float * len(values))(*values)
    u = lambda values: (ct.c_uint * len(values))(*values)
    records = []
    steps = 0
    for line in args.trace.read_text(encoding='utf8').splitlines():
        record = json.loads(line)
        records.append(record)
        if record['Kind'] == 'step':
            steps += 1
        if steps == args.step and record['Kind'] == 'read':
            break
    if steps != args.step or records[-1]['Kind'] != 'read':
        raise ValueError('Requested cloth sample is incomplete.')
    captured = records[-1]['Value']
    detail = next(d for d in json.loads(args.reference.read_text(encoding='utf8'))['details'] if d['event'] == args.event)
    buffer = detail['vertex_buffers'][0]
    raw = np.fromfile(buffer['file'], np.uint8)[int(buffer['byteOffset']):]
    expected = raw.view(np.float32).reshape(-1, int(buffer['byteStride']) // 4)[:, :3]
    mapping = np.array(captured['ParticleForVertex'])
    def rendered(values):
        points = np.array(values).reshape(-1, 3)[mapping]
        pose = np.array(captured['Pose'])
        q, w = pose[3:6], pose[6]
        points = points + 2 * w * np.cross(q, points) + 2 * np.cross(q, np.cross(q, points)) + pose[:3]
        return (np.c_[points, np.ones(len(points))] @ np.array(captured['World']).reshape(4, 4))[:, :3]
    def difference(values):
        distance = np.linalg.norm(rendered(values) - expected, axis=1)
        return dict(mean=float(distance.mean()), p95=float(np.quantile(distance, .95)), maximum=float(distance.max()))
    def root_pose(read):
        matrix=np.linalg.inv(np.array(read['World']).reshape(4,4))
        r=matrix[:3,:3];w=np.sqrt(1+np.trace(r))*.5
        q=np.array([r[1,2]-r[2,1],r[2,0]-r[0,2],r[0,1]-r[1,0]])/(4*w)
        return np.r_[matrix[3,:3],q,w].tolist()
    def in_root(values,pose,read,normal=False):
        v=np.array(values).reshape(-1,3);pose=np.array(pose);q=pose[3:6];w=pose[6]
        v=v+2*w*np.cross(q,v)+2*np.cross(q,np.cross(q,v))
        if not normal:v+=pose[:3]
        return (np.c_[v,np.zeros(len(v)) if normal else np.ones(len(v))]@np.array(read['World']).reshape(4,4))[:,:3].ravel().tolist()
    rows = []
    configurations=[('source-vector',args.source_scale)]if args.source_vector else [('captured',1),('source',1),('source',args.source_scale)]
    for rest,scale,geodesic in [(r,s,g) for r,s in configurations for g in [True,False]]:
        for mode in args.modes.split(','):
            root=mode.endswith('-root');clock=mode.removesuffix('-root')
            scene = dll.hs2_cloth_create()
            if not scene:
                raise RuntimeError(dll.hs2_cloth_error().decode('utf8'))
            pending = 0
            try:
                count = None
                for row_index,row in enumerate(records):
                    kind, v = row['Kind'], row['Value']
                    if kind == 'add':
                        count = v['Count']
                        read=next(r['Value']for r in records[row_index:]if r['Kind']=='read')
                        points=[p*scale for p in (v.get('Cooked',v['Positions'])if rest=='captured' else v['Rest'])]
                        if rest=='source-vector':points=(np.array(v['Rest']).reshape(-1,3)*np.array([float(s)for s in args.source_vector.split(',')])).ravel().tolist()
                        checked('add_cooking', scene, count, f(in_root(points,v['Pose'],read)if root else points),
                                len(v['Triangles']), u(v['Triangles']), f([p*scale for p in v['Maximum']]), f(v['Parameters']), f(root_pose(read)if root else v['Pose']), int(geodesic))
                    elif kind == 'particles':
                        checked('particles', scene, v['Index'], len(v['Self']), u(v['Self']), len(v['VirtualIndices']) // 4,
                                u(v['VirtualIndices']), len(v['VirtualWeights']) // 3, f(v['VirtualWeights']))
                    elif kind == 'update':
                        read=next(r['Value']for r in records[row_index:]if r['Kind']=='read')
                        checked('update', scene, v['Index'], f(in_root(v['Positions'],v['Pose'],read)if root else v['Positions']), f(in_root(v['Normals'],v['Pose'],read,True)if root else v['Normals']), f(v['Separation']),
                                f(root_pose(read)if root else v['Pose']), f(v['Acceleration']), int(v['Reset']))
                    elif kind == 'step':
                        seconds = v['Seconds']
                        if clock == 'fixed-50hz':
                            pending += seconds
                            while pending >= .02:
                                checked('step', scene, .02)
                                pending -= .02
                        else:
                            for _ in range(2 if clock == 'substep-60hz' else 1):
                                checked('step', scene, seconds / (2 if clock == 'substep-60hz' else 1))
                result = f([0] * (count * 3))
                checked('read', scene, 0, count, result)
                distance=np.linalg.norm(np.array(result).reshape(-1,3)[mapping]-expected,axis=1)if root else None
                metric=dict(mean=float(distance.mean()),p95=float(np.quantile(distance,.95)),maximum=float(distance.max()))if root else difference(result)
                rows.append(dict(rest=rest, scale=scale, geodesic=geodesic, mode=mode, **metric,
                                 replayDifferenceFromCaptured=float(np.max(np.abs(np.array(result) - captured['Positions'])))))
            finally:
                dll.hs2_cloth_destroy(scene)
    search.close()
    result = dict(step=args.step, captured=difference(captured['Positions']), candidates=rows, acceptance=False)
    args.output.write_text(json.dumps(result, indent=2), encoding='utf8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trace', type=Path, required=True)
    parser.add_argument('--library', type=Path, required=True)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--event', type=int, required=True)
    parser.add_argument('--step', type=int, default=422)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--source-scale',type=float,default=1)
    parser.add_argument('--modes',default='frame,fixed-50hz,substep-60hz')
    parser.add_argument('--source-vector')
    run(parser.parse_args())
