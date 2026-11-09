"""Build a browsable report without treating missing evidence as a pass."""
import argparse
import csv
import html
import hashlib
import json
from pathlib import Path


def read(path, default=None):
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (FileNotFoundError, ValueError):
        return default


def review_matches(directory, reviewed):
    expected = {name.replace('\\', '/'): value for name, value in reviewed.get('evidenceSHA256', {}).items()}
    required = [f'comparison/{view}-comparison.png' for view in ['front','side','back','face']]
    required += [f'comparison/dynamic/frame-{frame:03}.png' for frame in range(15,361,15)]
    required += [f'review-{view}.png' for view in ['static','dynamic-front','dynamic-face']]
    return (reviewed.get('staticViews') == 4 and reviewed.get('dynamicFrames') == 24
            and all((directory/name).is_file() and hashlib.sha256((directory/name).read_bytes()).hexdigest() == expected.get(name)
                    for name in required))


def build(root):
    catalog = read(root/'catalog.json', [])
    results = read(root/'results.json', [])
    if isinstance(results, dict):
        results = [results]
    results = {r['Id']: r for r in results}
    rows = []
    cards = []
    e = html.escape
    for entry in catalog:
        key = entry['Id']
        directory = root/key
        run = results.get(key, {})
        report = read(directory/'comparison'/'report.json', {})
        dynamic = read(directory/'comparison'/'dynamic'/'report.json', {})
        regions = [r for v in report.get('views', []) for r in v.get('regions', {}).values()]
        motion_regions = [r for frame in dynamic.get('frames', []) for r in frame.get('regions', {}).values()]
        reviewed = read(directory/'review.json', {})
        review_verified = review_matches(directory, reviewed)
        static_pass = len(report.get('views', [])) == 4 and bool(regions) and report.get('numericPass') is True
        dynamic_pass = dynamic.get('complete') is True and len(dynamic.get('frames', [])) == 24 and dynamic.get('numericPass') is True
        hash_pass = run.get('HashUnchanged') is True
        independent = read(directory/'independent'/'run.json', {})
        independence_pass = independent.get('GameOrPluginAssemblyLoaded') is False and independent.get('CardSHA256') == entry['Hash']
        reference = Path(run['Reference']) if run.get('Reference') else directory/'reference'
        fixture = read(reference/'fixture.json', {})
        actual_fixture = read(directory/'independent'/'fixture.json', {})
        actual_state = read(directory/'independent'/'state.json', {})
        has_cloth = any(r.get('Cloth') for state in (read(reference/'state.json', {}), actual_state)
                        for r in state.get('Renderers', []))
        for settings in (fixture, actual_fixture):
            settings.setdefault('ClothResetVersion', 0)
            settings.setdefault('PartAnimationClockVersion', 0)
            settings.setdefault('BlinkClockVersion', 0)
            if not has_cloth:
                settings['ClothResetVersion'] = 0
        current_fixture = fixture == actual_fixture and all(fixture.get(k, 0) >= v for k, v in {
            'TemporalPhaseVersion':2, 'PoseSamplingVersion':2, 'FaceTickVersion':1,
            'PhysicsResetVersion':3, 'GizmoVisibilityVersion':1, 'BlinkClockVersion':1}.items())
        if has_cloth:
            current_fixture = current_fixture and fixture.get('ClothResetVersion', 0) >= 1
        part_animations = directory/'independent'/'part-animations.txt'
        if part_animations.exists() and part_animations.read_text(encoding='utf-8-sig').strip():
            current_fixture = current_fixture and fixture.get('PartAnimationClockVersion', 0) >= 1
        runtime_errors = reference/'capture-runtime-errors.txt'
        reference_clean = runtime_errors.exists() and not runtime_errors.read_text(encoding='utf-8-sig').strip() and (reference/'finished.txt').exists() and (reference/'finished.txt').read_text(encoding='utf-8-sig').strip() == 'success'
        accepted = static_pass and dynamic_pass and hash_pass and independence_pass and current_fixture and reference_clean and review_verified and reviewed.get('staticPassed') is True and reviewed.get('dynamicPassed') is True
        row = dict(id=key, name=entry['Name'], card=entry['Card'], staticNumericPass=static_pass,
                   dynamicNumericPass=dynamic_pass, visualReviewPassed=review_verified and reviewed.get('staticPassed') is True,
                   dynamicReviewPassed=review_verified and reviewed.get('dynamicPassed') is True, accepted=accepted,
                   reviewEvidenceVerified=review_verified,
                   hashUnchanged=hash_pass, independenceVerified=independence_pass,
                   currentFixtureVerified=current_fixture, referenceRuntimeVerified=reference_clean,
                   minimumStaticSSIM=min((r['ssim'] for r in regions), default=None),
                   maximumStaticError255=max((r['meanChannelError255'] for r in regions), default=None),
                   minimumDynamicSSIM=min((r['ssim'] for r in motion_regions), default=None),
                   maximumDynamicError255=max((r['meanChannelError255'] for r in motion_regions), default=None),
                   failedStaticRegions='; '.join(v['view']+'/'+name for v in report.get('views', []) for name,r in v.get('regions',{}).items() if not r['numericPass']),
                   failedDynamicFrames='; '.join(str(f['frame']) for f in dynamic.get('frames',[]) if f.get('error') or not all(r['numericPass'] for r in f.get('regions',{}).values())),
                   error=run.get('Error') or entry.get('Error') or (None if report else '对照结果缺失'))
        rows.append(row)
        notes = list(entry.get('Diagnostics', []))
        notes += reviewed.get('notes', [])
        if run.get('Reference'):
            texture_note=Path(run['Reference'])/'texture-metadata-note.txt'
            if texture_note.exists(): notes.append(texture_note.read_text(encoding='utf-8-sig'))
        texture_note=directory/'independent'/'texture-metadata-note.txt'
        if texture_note.exists(): notes.append(texture_note.read_text(encoding='utf-8-sig'))
        notes += run.get('PreviousErrors', [])
        diagnostic = directory/'independent'/'diagnostics.txt'
        if diagnostic.exists():
            notes += diagnostic.read_text(encoding='utf-8-sig').splitlines()
        if row['error']:
            notes.append(row['error'])
        notes = list(dict.fromkeys(notes))
        row['diagnostics']='\n'.join(notes)
        cells = []
        for view in report.get('views', []):
            for region, values in view['regions'].items():
                cells.append(f'<tr><td>{e(view["view"])}</td><td>{e(region)}</td><td>{values["ssim"]:.6f}</td><td>{values["meanChannelError255"]:.4f}</td><td>{"通过" if values["numericPass"] else "未通过"}</td></tr>')
        pictures = ''.join(f'<figure><a href="{key}/comparison/{v}-comparison.png"><img loading="lazy" src="{key}/comparison/{v}-comparison.png"></a><figcaption>{v}：左为游戏，右为独立内核 · <a href="{key}/comparison/{v}-difference-x8.png">差异 ×8</a> · <a href="{key}/comparison/{v}-mask.png">人物区域</a></figcaption></figure>' for v in ['front', 'side', 'back', 'face'] if (directory/'comparison'/f'{v}-comparison.png').exists())
        available_frames=[i for i in range(15,361,15) if (directory/'comparison'/'dynamic'/f'frame-{i:03}.png').exists()]
        dynamic_pictures=''
        if available_frames:
            first=available_frames[0]
            dynamic_pictures=f'<div class="motion" data-path="{key}/comparison/dynamic/" data-frames="{e(json.dumps(available_frames),quote=True)}"><button type="button" onclick="playMotion(this)">播放采样序列</button><input aria-label="动态采样帧" type="range" min="0" max="{len(available_frames)-1}" value="0" oninput="showMotion(this.closest(\'.motion\'))"><p class="motion-label">{first}/360 · {first/30:g} 秒</p><a class="motion-link" href="{key}/comparison/dynamic/frame-{first:03}.png"><img loading="lazy" alt="左为游戏，右为独立内核的动态采样帧" src="{key}/comparison/dynamic/frame-{first:03}.png"></a><p>按原采样间隔播放，每秒 2 帧；仅展示记录中的 24 个采样帧。</p></div>'
        motion_cells=[]
        for frame in dynamic.get('frames',[]):
            for region,values in frame.get('regions',{}).items():
                motion_cells.append(f'<tr><td>{frame["frame"]}</td><td>{e(frame["view"])}/{e(region)}</td><td>{values["ssim"]:.6f}</td><td>{values["meanChannelError255"]:.4f}</td><td>{"通过" if values["numericPass"] else "未通过"}</td></tr>')
        dynamic_pictures='<table><thead><tr><th>帧</th><th>视角/区域</th><th>SSIM</th><th>通道误差</th><th>数值结果</th></tr></thead><tbody>'+''.join(motion_cells)+'</tbody></table>'+dynamic_pictures
        status = '已通过' if accepted else '未通过' if report else '未完成'
        summary = f'静态数值：{"通过" if static_pass else "未通过"}；动态数值：{"通过" if dynamic_pass else "未通过"}；视觉复核：{"通过" if accepted else "未通过或未完成"}；原卡哈希：{"不变" if hash_pass else "未确认"}'
        cards.append(f'<details class="card" data-search="{e(key+" "+entry["Name"]+" "+entry["Card"], quote=True)}"><summary><b>{e(key)} · {e(entry["Name"])}</b><span>{status}</span></summary><p>{summary}</p><p class="path">{e(entry["Card"])}</p><table><thead><tr><th>视角</th><th>区域</th><th>SSIM ≥ 0.99</th><th>通道误差 ≤ 2/255</th><th>数值结果</th></tr></thead><tbody>{"".join(cells)}</tbody></table>{pictures}<details><summary>动态序列：30 Hz，360 步，24 个采样帧</summary>{dynamic_pictures}</details><details><summary>诊断与原始记录</summary><pre>{e(chr(10).join(notes))}</pre><p><a href="{key}/comparison/report.json">静态 JSON</a> · <a href="{key}/comparison/dynamic/report.json">动态 JSON</a> · <a href="{key}/independent/run.json">资源与独立性记录</a></p></details></details>')
    totals = {k: sum(r[k] for r in rows) for k in ['staticNumericPass','dynamicNumericPass','accepted','hashUnchanged','independenceVerified']}
    totals.update(total=len(catalog), compared=sum(bool(read(root/r['id']/'comparison'/'report.json')) for r in rows),
                  captureFailures=sum(bool(r.get('Error')) for r in results.values()),
                  awaitingVisualReview=sum(r['staticNumericPass'] and r['dynamicNumericPass'] and not r['accepted'] for r in rows),
                  complete=len(results)==len(catalog))
    (root/'summary.json').write_text(json.dumps(dict(totals=totals,cards=rows),ensure_ascii=False,indent=2),encoding='utf-8')
    with (root/'summary.csv').open('w',newline='',encoding='utf-8-sig') as f:
        if rows:
            writer=csv.DictWriter(f,fieldnames=rows[0].keys());writer.writeheader();writer.writerows(rows)
    page = f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>HS2 独立预览对照报告</title><style>
body{{margin:36px auto;max-width:1240px;padding:0 24px;background:#101419;color:#e6ebf0;font:15px/1.65 system-ui}}h1{{font-size:30px}}a{{color:#84c4ff}}.stats{{padding:18px;background:#1a2634;border-radius:12px}}input{{box-sizing:border-box;width:100%;padding:14px;background:#202932;color:white;border:1px solid #52606e;border-radius:8px;margin:18px 0}}details.card{{border:1px solid #36434f;border-radius:8px;margin:12px 0;padding:14px}}summary{{cursor:pointer}}summary span{{float:right;color:#ffbd85}}figure{{margin:20px 0}}img{{width:100%;height:auto;border-radius:6px}}figcaption,.path{{color:#a5b4c2}}.path,pre{{white-space:pre-wrap;word-break:break-word}}table{{border-collapse:collapse;width:100%}}th,td{{text-align:left;padding:8px;border-bottom:1px solid #36434f}}details details{{margin:18px 0}}.hidden{{display:none}}
</style><h1>独立 Unity 预览 · 游戏画面对照</h1><p>本报告覆盖 {len(catalog)} 张固定卡片（女性 {sum(entry.get('Sex') == 1 for entry in catalog)} 张、男性 {sum(entry.get('Sex') == 0 for entry in catalog)} 张），包含原版样本、本机目录卡片和编辑保存的测试副本。仅测试时启动游戏本体，未使用 Studio。通过必须同时满足静态、动态、视觉复核、独立性和原卡完整性要求。</p><div class="stats">已生成对照 {totals['compared']}/{totals['total']} · 静态数值通过 {totals['staticNumericPass']} · 动态数值通过 {totals['dynamicNumericPass']} · 完整验收通过 {totals['accepted']}<br>原卡哈希确认不变 {totals['hashUnchanged']} · 独立程序集验证 {totals['independenceVerified']}</div><p>人物区域由两边画面与各自空背景差异的并集确定；面部区域取游戏参考头部包围范围的投影。SSIM ≥ 0.99，平均通道误差 ≤ 2/255；明显局部错误仍判失败。静态截图不替代动态验证，缺失证据不计为通过。</p><p><a href="summary.json">汇总 JSON</a> · <a href="summary.csv">汇总 CSV</a></p><input type="search" placeholder="按卡片名、文件名或编号筛选" aria-label="筛选人物卡" oninput="document.querySelectorAll('.card').forEach(c=>c.classList.toggle('hidden',!c.dataset.search.toLowerCase().includes(this.value.toLowerCase())))">{''.join(cards)}</html>'''
    script='''<script>
function showMotion(box){const frames=JSON.parse(box.dataset.frames),frame=frames[Number(box.querySelector('input').value)];const path=box.dataset.path+'frame-'+String(frame).padStart(3,'0')+'.png';box.querySelector('img').src=path;box.querySelector('.motion-link').href=path;box.querySelector('.motion-label').textContent=frame+'/360 · '+(frame/30)+' 秒';}
function stopMotion(box){clearInterval(box.timer);box.timer=null;box.querySelector('button').textContent='播放采样序列';}
function playMotion(button){const box=button.closest('.motion');if(box.timer){stopMotion(box);return;}button.textContent='暂停';box.timer=setInterval(()=>{const slider=box.querySelector('input');slider.value=(Number(slider.value)+1)%(Number(slider.max)+1);showMotion(box);},500);}
document.querySelectorAll('details').forEach(d=>d.addEventListener('toggle',()=>{if(!d.open)d.querySelectorAll('.motion').forEach(stopMotion);}));
</script>'''
    if (root/'resource-index.json').exists():
        page=page.replace('<a href="summary.csv">汇总 CSV</a>', '<a href="summary.csv">汇总 CSV</a> · <a href="resource-index.json">资源索引快照</a>')
    evidence=[('validation-metadata.json','构建与验证记录'),('card-integrity.json','原卡哈希'),('release-verification.json','发布包核对'),('renderer-build-equivalence.json','诊断修正版代码核对'),('diagnostic-control.json','控制样本图片核对')]
    links=''.join(f' · <a href="{name}">{label}</a>' for name,label in evidence if (root/name).exists())
    page=page.replace('<a href="summary.csv">汇总 CSV</a>', '<a href="summary.csv">汇总 CSV</a>'+links)
    (root/'index.html').write_text(page.replace('</html>',script+'</html>'),encoding='utf-8')
    lines=['# 未通过与未完成项目', '',
           f"总数 {totals['total']}；已生成对照 {totals['compared']}；静态数值通过 {totals['staticNumericPass']}；动态数值通过 {totals['dynamicNumericPass']}；完整验收通过 {totals['accepted']}。", '',
           'SSIM ≥ 0.99、平均通道误差 ≤ 2/255。静态、动态与视觉复核均通过才可接受。未完成项目不计为通过。', '',
           '| 编号 | 卡片 | 静态最低 SSIM | 动态最低 SSIM | 失败或未完成项目 |',
           '| --- | --- | ---: | ---: | --- |']
    for r in rows:
        if r['accepted']:
            continue
        reasons=[]
        if r['error']: reasons.append('采集/对照未完成：'+r['error'].splitlines()[0])
        if r['failedStaticRegions']: reasons.append('静态：'+r['failedStaticRegions'])
        if r['failedDynamicFrames']: reasons.append('动态帧：'+r['failedDynamicFrames'])
        if not r['visualReviewPassed'] or not r['dynamicReviewPassed']: reasons.append('视觉复核未通过或未完成')
        if not r['hashUnchanged']: reasons.append('原卡哈希未确认')
        if not r['independenceVerified']: reasons.append('独立运行证据未确认')
        if not r['currentFixtureVerified']: reasons.append('当前采样流程未确认')
        if not r['referenceRuntimeVerified']: reasons.append('游戏运行时异常或成功记录未确认')
        clean=lambda s:str(s).replace('|','\\|').replace('\n',' ')
        score=lambda s:'—' if s is None else f'{s:.6f}'
        lines.append(f"| {r['id']} | {clean(Path(r['card']).name)} | {score(r['minimumStaticSSIM'])} | {score(r['minimumDynamicSSIM'])} | {clean('；'.join(reasons))} |")
    (root/'not-passed.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps(totals,ensure_ascii=False,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('directory',type=Path)
    build(p.parse_args().directory)
