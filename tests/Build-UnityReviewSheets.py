"""Create compact review sheets; this script never marks a review as passed."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw


def panel(comparison, background, size, label):
    image = Image.open(comparison).convert('RGB')
    half = image.width // 2
    a, b = image.crop((0, 0, half, image.height)), image.crop((half, 0, image.width, image.height))
    if background.exists():
        mask = np.asarray(Image.open(background).convert('L')) > 0
        ys, xs = np.nonzero(mask)
    else:
        ys, xs = np.array([]), np.array([])
    if len(ys):
        box = (max(0, int(xs.min())-12), max(0, int(ys.min())-12),
               min(half, int(xs.max())+13), min(image.height, int(ys.max())+13))
        a, b = a.crop(box), b.crop(box)
    pair = Image.new('RGB', (a.width+b.width, a.height))
    pair.paste(a, (0, 0)); pair.paste(b, (a.width, 0))
    pair.thumbnail((size[0]-12, size[1]-30), Image.Resampling.LANCZOS)
    out = Image.new('RGB', size, '#161c23')
    out.paste(pair, ((size[0]-pair.width)//2, 26+(size[1]-30-pair.height)//2))
    ImageDraw.Draw(out).text((9, 7), label+' | game / independent', fill='white')
    return out


def build(directory):
    comparison = directory/'comparison'
    sheet = Image.new('RGB', (1680, 1320), '#101419')
    for index, view in enumerate(['front', 'side', 'back', 'face']):
        sheet.paste(panel(comparison/(view+'-comparison.png'), comparison/(view+'-mask.png'),
                          (840, 660), view), ((index % 2)*840, (index//2)*660))
    sheet.save(directory/'review-static.png')
    for view, start in [('front', 15), ('face', 195)]:
        cell = (560, 430) if view == 'front' else (640, 370)
        sheet = Image.new('RGB', (cell[0]*4, cell[1]*3), '#101419')
        for index, frame in enumerate(range(start, start+180, 15)):
            sheet.paste(panel(comparison/'dynamic'/f'frame-{frame:03}.png',
                              comparison/(view+'-mask.png'), cell, f'{frame}/360'),
                        ((index % 4)*cell[0], (index//4)*cell[1]))
        sheet.save(directory/('review-dynamic-'+view+'.png'))


if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('directory', type=Path); p.add_argument('--catalog', action='store_true')
    args = p.parse_args()
    for directory in sorted(args.directory.glob('*')) if args.catalog else [args.directory]:
        if (directory/'comparison/report.json').exists():
            build(directory)
