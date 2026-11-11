"""Compare complete material packages and metadata alongside independent FBX evidence.

Visual review and target-software reopen remain separate, explicit acceptance
gates. Every texture is compared at its original dimensions, without resizing.
"""
import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def compare(reference, actual, fbx_report, normal_report, output):
    errors, textures, metadata = [], [], []
    geometry, shape_normals = load(fbx_report), load(normal_report)
    if not geometry['Passed'] or not shape_normals['Passed']:
        errors.append('Independent complete FBX or sparse shape-normal comparison failed.')

    def same(a, b, path):
        if isinstance(a, bool) or isinstance(b, bool):
            if a != b:
                errors.append(path+': boolean differs.')
        elif isinstance(a, (float, int)) and isinstance(b, (float, int)):
            difference = abs(a-b)
            if not math.isfinite(a) or not math.isfinite(b) or difference > .00001:
                errors.append(path+': numeric value differs by '+str(difference))
        elif isinstance(a, list) and isinstance(b, list):
            if len(a) != len(b):
                errors.append(path+': array size differs.')
            for i, (x, y) in enumerate(zip(a, b)):
                same(x, y, path+'/'+str(i))
        elif isinstance(a, dict) and isinstance(b, dict):
            if a.keys() != b.keys():
                errors.append(path+': property names differ.')
            for key in a.keys() & b.keys():
                same(a[key], b[key], path+'/'+key)
        elif a != b:
            errors.append(path+': value differs.')

    before, after = load(reference/'model-result.json'), load(actual/'model-result.json')
    for name in ['SchemaVersion', 'EditRevision', 'Meshes', 'Vertices', 'Triangles', 'Bones', 'BlendShapes', 'Materials']:
        same(before[name], after[name], 'model-result/'+name)
        metadata.append(dict(Name=name, Reference=before[name], Actual=after[name]))
    expected_geometry = {row['Node']: row for row in before['Geometry']}
    actual_geometry = {row['Node']: row for row in after['Geometry']}
    if expected_geometry.keys() != actual_geometry.keys():
        errors.append('Reference mesh metadata identities differ.')
    for node in expected_geometry.keys() & actual_geometry.keys():
        a, b = expected_geometry[node], actual_geometry[node]
        for name in ['Vertices', 'SampleIndices', 'ReferencePositions', 'BlendShapeNames', 'BlendShapeMaximumDisplacements', 'SourceDefaultWeights', 'DefaultWeights', 'NeutralResetShape']:
            same(a[name], b[name], 'geometry/'+node+'/'+name)
    a, b = load(reference/'骨骼映射.json'), load(actual/'骨骼映射.json')
    same(a, b, 'bone-map')
    a, b = load(reference/'materials.json'), load(actual/'materials.json')
    same(a, b, 'materials')
    expected_files = {f.relative_to(reference).as_posix(): f for f in (reference/'Textures').glob('*.png')}
    actual_files = {f.relative_to(actual).as_posix(): f for f in (actual/'Textures').glob('*.png')}
    if expected_files.keys() != actual_files.keys():
        errors.append('The complete exported texture file set differs.')
    for name in expected_files.keys() & actual_files.keys():
        a = np.asarray(Image.open(expected_files[name]).convert('RGBA'), dtype=np.int16)
        b = np.asarray(Image.open(actual_files[name]).convert('RGBA'), dtype=np.int16)
        if a.shape != b.shape:
            errors.append('Texture dimensions changed: '+name)
            continue
        difference = np.abs(a-b)
        mean = difference.mean(axis=(0, 1))
        passed = bool(mean.max() <= 2)
        textures.append(dict(Name=name, Width=a.shape[1], Height=a.shape[0], MeanChannelError255=mean.tolist(),
                             MaximumError255=int(difference.max()), PixelExact=not bool(difference.any()), Passed=passed))
        if not passed:
            errors.append('Texture numeric threshold failed: '+name)
    scene_a, scene_b = load(reference/'scene.json'), load(actual/'scene.json')
    # Capture order is not semantic; the environment contains named light objects.
    for scene in [scene_a, scene_b]:
        scene.pop('Warnings', None)
        scene['Lights'] = sorted(scene['Lights'], key=lambda row: row['Name'])
    same(scene_a, scene_b, 'scene')
    for folder in [reference, actual]:
        for name in ['character.fbx', 'materials.json', '骨骼映射.json', 'scene.json', '预览参考.png', 'Environment/lighting.hdr', 'Environment/reflection.hdr']:
            if not (folder/name).is_file():
                errors.append('Missing package artifact: '+str(folder/name))
    report = dict(SchemaVersion=1, Reference=str(reference), Actual=str(actual), GeometryReport=str(fbx_report),
                  ShapeNormalReport=str(normal_report), Metadata=metadata, Textures=sorted(textures, key=lambda r: r['Name']), Errors=errors,
                  NumericPass=not errors and bool(textures), VisualReviewPassed=False, VisualParityVerified=False,
                  RemainingGates=['HDR/LUT image comparison', 'reference screenshot visual review', 'target software conversion and independent reopen'])
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf8')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['reference', 'actual', 'fbx_report', 'normal_report', 'output']:
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    result = compare(args.reference, args.actual, args.fbx_report, args.normal_report, args.output)
    print(json.dumps(dict(NumericPass=result['NumericPass'], Textures=len(result['Textures']), Errors=result['Errors']), ensure_ascii=False))
    raise SystemExit(0 if result['NumericPass'] else 1)
