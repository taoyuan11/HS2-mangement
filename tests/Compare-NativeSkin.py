"""Compare original GPU skin composition against frozen legacy previews; not a scene parity verdict."""
import argparse
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image


def run(args):
    baseline = json.loads(args.baseline.read_text(encoding='utf-8-sig'))
    if len(baseline['Cards']) != 84:
        raise ValueError('All 84 accepted cards are required.')
    args.output.mkdir(parents=True, exist_ok=False)
    rows = []
    for entry in baseline['Cards']:
        folder = args.output/entry['Id']
        folder.mkdir()
        row = {'Id': entry['Id'], 'Textures': [], 'Errors': [], 'TextureNumericPass': False}
        try:
            with (folder/'native.log').open('w', encoding='utf-8') as log:
                # This process is owned by this invocation. Terminate only it on timeout/cancellation.
                process = subprocess.Popen([str(args.dotnet), str(args.checks), '--skin-probe', entry['Definition'], '--output', str(folder)], stdout=log, stderr=subprocess.STDOUT)
                try:
                    code = process.wait(timeout=180)
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.wait()
                if code:
                    raise RuntimeError(f'Native composition exited with {code}; see native.log')
            expected = ['o_head_MainTex.png', 'o_head_DetailMainTex.png']
            expected += [f"o_body_{'cm' if entry['Sex']==0 else 'cf'}_{kind}.png" for kind in ['MainTex', 'DetailMainTex']]
            evidence = next(e for e in entry['Evidence'] if e['Source']=='legacy-preview' and e['Name']=='front.png')
            reference = Path(evidence['Path']).parent
            for name in expected:
                with Image.open(reference/name) as image:
                    original = np.asarray(image.convert('RGBA'))
                with Image.open(folder/name) as image:
                    actual = np.asarray(image.convert('RGBA'))
                if original.shape != actual.shape:
                    raise ValueError(f'{name}: dimensions changed: {original.shape} / {actual.shape}')
                error = np.abs(original.astype(np.int16)-actual.astype(np.int16))
                mae = error.mean(axis=(0,1))
                row['Textures'].append({'Name': name, 'Width': actual.shape[1], 'Height': actual.shape[0], 'MAE': mae.tolist(), 'Max': int(error.max()), 'PixelExact': bool(not error.any()), 'NumericPass': bool(mae.max()<=2)})
            row['TextureNumericPass'] = all(t['NumericPass'] for t in row['Textures'])
            if row['TextureNumericPass']:
                for name in expected:
                    (folder/name).unlink()
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
            row['Errors'].append(str(error))
        rows.append(row)
        (folder/'comparison.json').write_text(json.dumps(row, indent=2), encoding='utf-8')
        summary = {'SchemaVersion': 1, 'Scope': 'Skin composition only; SSIM, scene appearance, dynamics, export and visual review remain separate gates.', 'Cards': rows, 'ExpectedCards': 84, 'TextureNumericPass': len(rows)==84 and all(r['TextureNumericPass'] for r in rows), 'VisualParityVerified': False}
        (args.output/'report.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(entry['Id'], 'PASS' if row['TextureNumericPass'] else 'FAIL', f'{len(rows)}/84', flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--checks', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    run(parser.parse_args())
