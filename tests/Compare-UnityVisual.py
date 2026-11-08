"""Maker-environment acceptance, using reference camera coordinates and foreground unions."""
import argparse
import json
from pathlib import Path
import numpy as np
from PIL import Image
from skimage.metrics import structural_similarity


def measures(a, b, ba, bb, region=None):
    mask = (np.abs(a.astype(float)-ba).max(axis=2)>4) | (np.abs(b.astype(float)-bb).max(axis=2)>4)
    _, score = structural_similarity(a,b,data_range=255,channel_axis=2,full=True)
    error = np.abs(a.astype(float)-b)
    masks = {'character':mask}
    if region is not None:
        face = np.zeros(mask.shape, dtype=bool)
        h,w = mask.shape
        left,right = [int(np.clip(region[k],0,1)*w) for k in ['Left','Right']]
        top,bottom = [int(np.clip(1-region[k],0,1)*h) for k in ['Top','Bottom']]
        face[top:bottom,left:right]=True
        masks['face'] = mask & face
    result = {}
    for name,m in masks.items():
        count=int(m.sum())
        s=float(score.mean(axis=2)[m].mean()) if count else 0
        e=float(error[m].mean()) if count else 255
        result[name]={'pixels':count,'ssim':s,'meanChannelError255':e,'numericPass':count>100 and s>=.99 and e<=2}
    return result, mask, error


def pixels(directory, name):
    return np.asarray(Image.open(directory/name).convert('RGB'))


def bone_distances(states):
    groups=[]
    for state in states:
        grouped={}
        for bone in state['Bones']:
            grouped.setdefault((bone['Name'],bone['Parent']),[]).append(bone)
        groups.append(grouped)
    common=groups[0].keys() & groups[1].keys()
    unique=[key for key in common if len(groups[0][key])==len(groups[1][key])==1]
    distances=[(name+' / '+parent,float(np.linalg.norm([groups[0][(name,parent)][0]['World'][k]-groups[1][(name,parent)][0]['World'][k] for k in 'xyz']))) for name,parent in unique]
    return dict(largestBoneDistances=sorted(distances,key=lambda item:item[1],reverse=True)[:12],
                boneMatchMethod='unique name and parent; ambiguous duplicates excluded from diagnostics',
                matchedBoneCount=len(unique),ambiguousBoneCount=len(common)-len(unique))


def dynamic(reference, actual, output, region):
    output.mkdir(parents=True,exist_ok=True)
    frames = []
    backgrounds = {view:(pixels(reference,view+'-background.png'),pixels(actual,view+'-background.png')) for view in ['front','face']}
    for frame in range(15,361,15):
        name=f'frame-{frame:03}.png'
        try:
            a,b=pixels(reference/'dynamic',name),pixels(actual/'dynamic',name)
            view='front' if frame<=180 else 'face'
            regions,mask,error=measures(a,b,*backgrounds[view],region if view=='face' else None)
            row={'frame':frame,'timeSeconds':frame/30,'view':view,'regions':regions}
            Image.fromarray(np.concatenate((a,b),axis=1)).save(output/name)
            Image.fromarray(np.clip(error*8,0,255).astype('uint8')).save(output/f'difference-{frame:03}.png')
            states=[json.loads((directory/'dynamic'/f'pose-{frame:03}.json').read_text(encoding='utf-8-sig')) for directory in [reference,actual]]
            row.update(bone_distances(states))
            faces={r['Name']:r['BlendWeights'] for r in states[1]['Faces']}
            row['faceBlendWeightMaxError']={r['Name']:float(np.max(np.abs(np.asarray(r['BlendWeights'])-faces[r['Name']]))) for r in states[0]['Faces'] if r['Name'] in faces and len(r['BlendWeights'])>0 and len(r['BlendWeights'])==len(faces[r['Name']])}
        except (OSError,ValueError,KeyError) as error:
            row={'frame':frame,'error':str(error)}
        frames.append(row)
    complete=all((directory/'dynamic'/'complete.txt').exists() for directory in [reference,actual])
    result={'rate':30,'seed':1979,'steps':360,'sampledFrames':24,'complete':complete,'frames':frames,
            'numericPass':complete and all('error' not in row and all(r['numericPass'] for r in row['regions'].values()) for row in frames),
            'visualReviewPassed':False}
    (output/'report.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    return result


def compare(reference, actual, output):
    output.mkdir(parents=True, exist_ok=True)
    region = json.loads((reference / 'face-region.json').read_text(encoding='utf-8-sig'))
    rows = []
    for view in ['front', 'side', 'back', 'face']:
        a,b,ba,bb=[pixels(directory,filename) for directory,filename in [(reference,view+'.png'),(actual,view+'.png'),(reference,view+'-background.png'),(actual,view+'-background.png')]]
        regions,mask,error=measures(a,b,ba,bb,region if view=='face' else None)
        row={'view':view,'regions':regions}
        Image.fromarray((mask*255).astype('uint8')).save(output/(view+'-mask.png'))
        Image.fromarray(np.clip(error*8,0,255).astype('uint8')).save(output/(view+'-difference-x8.png'))
        Image.fromarray(np.concatenate((a,b),axis=1)).save(output/(view+'-comparison.png'))
        rows.append(row)
    motion=dynamic(reference,actual,output/'dynamic',region)
    result={'reference':str(reference),'actual':str(actual),'views':rows,'numericPass':all(r['numericPass'] for v in rows for r in v['regions'].values()),'visualReviewPassed':False,'dynamicNumericPass':motion['numericPass'],'dynamicParityVerified':False}
    (output/'report.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    return result


if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('reference',type=Path);p.add_argument('actual',type=Path);p.add_argument('output',type=Path)
    args=p.parse_args();print(json.dumps(compare(args.reference,args.actual,args.output),indent=2))
