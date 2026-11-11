"""Run owned native catalog jobs, retaining every failure and separating preflight from visual acceptance."""
import argparse
import json
import subprocess
import time
import threading
import shutil
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path


def summarize(mode, selected, rows, stopped=False):
    expected={entry['Id'] for entry in selected}
    actual=[row['Id'] for row in rows]
    complete=len(actual)==len(expected) and set(actual)==expected
    execution=complete and all(row.get('ExecutionPassed') is True for row in rows)
    materials=execution and all(row.get('MaterialStatePassed') is True and not row.get('Errors') for row in rows)
    numeric=mode=='frames' and materials and all(row.get('NumericPass') is True for row in rows)
    return {'SchemaVersion':2,'Mode':mode,'ExpectedCards':84,'SelectedCards':len(selected),'CompletedCards':len(rows),
            'RunStopped':stopped,'Complete':complete and not stopped,'FullCatalog':len(expected)==84,
            'Cards':rows,'ExecutionPassed':execution and not stopped,'MaterialStatePassed':materials and not stopped,
            'SelectedNumericPass':numeric and not stopped,'NumericPass':len(expected)==84 and numeric and not stopped,
            'VisualReviewPassed':False,'VisualParityVerified':False}


def publish(path, report):
    # A cancelled writer must not leave a truncated report that resembles a
    # completed run. The last complete snapshot remains readable.
    temporary=path.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
    temporary.replace(path)


def run(args):
    baseline=json.loads(args.baseline.read_text(encoding='utf-8-sig'))
    if len(baseline['Cards'])!=84 or len({entry['Id'] for entry in baseline['Cards']})!=84:
        raise ValueError('The frozen catalog must contain all 84 distinct cards.')
    args.output.mkdir(parents=True,exist_ok=False)
    validation=args.output/'validation-tools';validation.mkdir()
    for name in ['Compare-NativeFrames.py','Compare-UnityVisual.py']:
        shutil.copyfile(Path(__file__).with_name(name),validation/name)
    selected=baseline['Cards']
    if args.ids:
        requested=set(args.ids.split(','));selected=[r for r in selected if r['Id'] in requested]
        if {r['Id'] for r in selected}!=requested:
            raise ValueError('Unknown catalog sample id.')
    rows=[]
    cancelled=threading.Event()
    def validate(entry):
        folder=args.output/entry['Id'];folder.mkdir();started=time.monotonic()
        row={'Id':entry['Id'],'ExecutionPassed':False,'Errors':[],'Seconds':0,'VisualParityVerified':False}
        if args.mode=='frames':row['NumericPass']=False
        reference=Path(next(e['Path'] for e in entry['Evidence'] if e['Source']=='legacy-preview' and e['Name']=='front.png')).parent
        command=[str(args.dotnet),str(args.checks),'--scene-probe',entry['Definition'],'--fixture',str(reference),'--output',str(folder)]
        command+=['--preflight'] if args.mode=='preflight' else ['--frame','--frame-dynamic','--acceptance']
        try:
            with (folder/'native.log').open('w',encoding='utf8') as log:
                process=subprocess.Popen(command,stdout=log,stderr=subprocess.STDOUT)
                row['OwnedProcessId']=process.pid
                (folder/'process.json').write_text(json.dumps({'Pid':process.pid,'Command':command,'Owner':str(args.output.resolve())}),encoding='utf8')
                try:
                    deadline=time.monotonic()+args.timeout
                    while True:
                        if cancelled.is_set():
                            raise RuntimeError('Catalog validation was cancelled.')
                        remaining=deadline-time.monotonic()
                        if remaining<=0:
                            raise subprocess.TimeoutExpired(command,args.timeout)
                        try:
                            code=process.wait(timeout=min(.5,remaining));break
                        except subprocess.TimeoutExpired:
                            continue
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.wait()
                if code:
                    raise RuntimeError(f'Native validation exited with {code}; see native.log')
            evidence=folder/('preflight.json' if args.mode=='preflight' else 'render-scope.json')
            if not evidence.is_file():
                raise RuntimeError('The native process did not publish completion evidence.')
            row['ExecutionPassed']=True
            material_state=json.loads((folder/'material-state.json').read_text(encoding='utf-8-sig'))
            row['MaterialStatePassed']=material_state['Passed']
            row['Errors']+=material_state['Errors']
            if args.mode=='frames':
                import importlib.util
                spec=importlib.util.spec_from_file_location('native_visual',validation/'Compare-NativeFrames.py')
                visual=importlib.util.module_from_spec(spec);spec.loader.exec_module(visual)
                comparison=visual.compare(reference,folder,folder/'comparison.json');row['NumericPass']=comparison['numericPass']
                row['Errors']+=comparison['errors']
                row['FailedRegions']=[{'View':group['view'],'Frame':group.get('frame'),'Region':name,**region}
                                      for group in comparison['views']+comparison['dynamic']
                                      for name,region in group['regions'].items() if not region['numericPass']]
        except (OSError,RuntimeError,ValueError,subprocess.TimeoutExpired) as error:
            row['Errors'].append(str(error))
        row['Seconds']=round(time.monotonic()-started,3)
        return row
    order={r['Id']:i for i,r in enumerate(selected)}
    publish(args.output/'report.json',summarize(args.mode,selected,rows))
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        pending=[pool.submit(validate,entry) for entry in selected]
        try:
            for future in as_completed(pending):
                row=future.result();rows.append(row);rows.sort(key=lambda r:order[r['Id']])
                publish(args.output/'report.json',summarize(args.mode,selected,rows))
                status='NUMERIC_PASS' if args.mode=='frames' and row.get('NumericPass') and not row['Errors'] else (
                    'PREFLIGHT_PASS' if args.mode=='preflight' and row.get('MaterialStatePassed') and not row['Errors'] else 'FAIL')
                print(row['Id'],status,f'{len(rows)}/{len(selected)}',row['Seconds'],flush=True)
        except BaseException:
            cancelled.set()
            for future in pending:future.cancel()
            publish(args.output/'report.json',summarize(args.mode,selected,rows,stopped=True))
            raise
    report=summarize(args.mode,selected,rows)
    return 0 if (report['SelectedNumericPass'] if args.mode=='frames' else report['MaterialStatePassed']) else 1


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline',type=Path,required=True);parser.add_argument('--dotnet',type=Path,required=True)
    parser.add_argument('--checks',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--mode',choices=['preflight','frames'],required=True);parser.add_argument('--ids')
    parser.add_argument('--timeout',type=int,default=900)
    parser.add_argument('--jobs',type=int,choices=range(1,5),default=1)
    raise SystemExit(run(parser.parse_args()))
