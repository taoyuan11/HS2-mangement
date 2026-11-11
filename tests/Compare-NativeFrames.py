"""Frozen visual gate for native captures; every missing image or pending review fails acceptance."""
import argparse
import importlib.util
import json
from pathlib import Path

import numpy as np
from PIL import Image

spec = importlib.util.spec_from_file_location('legacy_visual', Path(__file__).with_name('Compare-UnityVisual.py'))
legacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legacy)


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def pixels(path):
    with Image.open(path) as image:
        if image.size != (1280, 720):
            raise ValueError(f'Capture resolution changed: {path}')
        return np.asarray(image.convert('RGB'))


def compare(reference, actual, output):
    result = {'schemaVersion': 1, 'reference': str(reference), 'actual': str(actual),
              'thresholds': {'ssim': .99, 'meanChannelError255': 2},
              'views': [], 'dynamic': [], 'errors': [], 'numericPass': False,
              'visualReviewPassed': False, 'visualParityVerified': False}
    try:
        if load(reference / 'fixture.json') != load(actual / 'fixture.json'):
            raise ValueError('Camera, clock, warmup or reset fixture differs.')
        scope = load(actual / 'render-scope.json')
        result['renderScope'] = scope
        material_state = load(actual / 'material-state.json')
        if not material_state['Passed']:
            result['errors'].extend(material_state['Errors'])
        if scope.get('CapturedAmbientProbe', True):
            result['errors'].append('Native ambient probe has not been generated.')
        if set(scope.get('CapturedViews', [])) != {'front', 'side', 'back', 'face'}:
            result['errors'].append('All four static views are required.')
        if not scope.get('DynamicCaptured'):
            result['errors'].append('Dynamic captures are required.')
        if not scope.get('EffectsExecuted') or scope.get('UnsupportedEnabledEffects') != []:
            result['errors'].append('Explicit execution coverage for every enabled effect is required.')
        region = load(reference / 'face-region.json')
        backgrounds = {}
        for view in ['front', 'side', 'back', 'face']:
            try:
                a, b = pixels(reference / (view + '.png')), pixels(actual / (view + '.png'))
                ba, bb = pixels(reference / (view + '-background.png')), pixels(actual / (view + '-background.png'))
                backgrounds[view] = (ba, bb)
                regions, _, _ = legacy.measures(a, b, ba, bb, region if view == 'face' else None)
                result['views'].append({'view': view, 'regions': regions})
            except (OSError, ValueError) as error:
                result['errors'].append(f'{view}: {error}')
        for frame in range(15, 361, 15):
            try:
                view = 'front' if frame <= 180 else 'face'
                a = pixels(reference / 'dynamic' / f'frame-{frame:03}.png')
                b = pixels(actual / 'dynamic' / f'frame-{frame:03}.png')
                regions, _, _ = legacy.measures(a, b, *backgrounds[view], region if view == 'face' else None)
                result['dynamic'].append({'frame': frame, 'timeSeconds': frame / 30, 'view': view, 'regions': regions})
            except (OSError, ValueError, KeyError) as error:
                result['errors'].append(f'frame {frame}: {error}')
        for directory in [reference, actual]:
            if not (directory / 'dynamic' / 'complete.txt').is_file():
                result['errors'].append(f'Dynamic completion evidence is missing: {directory}')
        result['numericPass'] = (not result['errors'] and len(result['views']) == 4 and len(result['dynamic']) == 24
                                 and all(r['numericPass'] for group in result['views'] + result['dynamic'] for r in group['regions'].values()))
    except (OSError, ValueError, KeyError) as error:
        result['errors'].append(str(error))
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reference', type=Path)
    parser.add_argument('actual', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = compare(args.reference.resolve(), args.actual.resolve(), args.output.resolve())
    failures = [(group.get('view'), group.get('frame'), name, region['ssim'], region['meanChannelError255'])
                for group in result['views'] + result['dynamic'] for name, region in group['regions'].items() if not region['numericPass']]
    print(json.dumps({'numericPass': result['numericPass'], 'visualParityVerified': False,
                      'failedRegions': failures, 'errors': result['errors']}))
    raise SystemExit(0 if result['numericPass'] else 1)
