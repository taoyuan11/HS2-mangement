"""Regression checks: execution, image parity and the full catalog are distinct gates."""
import importlib.util
import unittest
import tempfile
import hashlib
import json
from pathlib import Path

spec=importlib.util.spec_from_file_location('catalog',Path(__file__).with_name('Run-NativeCatalog.py'))
catalog=importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)
review_spec=importlib.util.spec_from_file_location('review',Path(__file__).with_name('Complete-NativeCatalogReview.py'))
review=importlib.util.module_from_spec(review_spec)
review_spec.loader.exec_module(review)


class ObservedReviewChecks(unittest.TestCase):
    def setUp(self):
        self.directory=tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root=Path(self.directory.name)
        self.actual=self.root/'one';self.actual.mkdir()
        self.reference=self.root/'reference';self.reference.mkdir()
        self.evidence=self.root/'visual-review'/'one';self.evidence.mkdir(parents=True)
        card=self.root/'card.png';card.write_bytes(b'frozen card')
        digest=lambda value:hashlib.sha256(value).hexdigest().upper()
        self.entry=dict(Id='one',Name='sample',Card=str(card),Hash=digest(card.read_bytes()),Evidence=[])
        samples=[]
        for name in review.SAMPLES:
            for directory in [self.actual,self.reference]:
                image=directory/name;image.parent.mkdir(parents=True,exist_ok=True);image.write_bytes(b'captured '+name.encode())
            hashed=digest((self.actual/name).read_bytes())
            self.entry['Evidence'].append(dict(Name=name,Source='legacy-preview',Path=str(self.reference/name),SHA256=hashed))
            samples.append(dict(Image=name,ReferenceSHA256=hashed,NativeSHA256=hashed))
        region=dict(character=dict(ssim=.995,meanChannelError255=1,numericPass=True))
        self.scope=dict(CapturedAmbientProbe=False,EffectsExecuted=True,UnsupportedEnabledEffects=[],DynamicCaptured=True,
                        CapturedViews=['front','side','back','face'],EnabledEffects=['Bloom'],Cloth=True,PendingValidation=['shadow maps'])
        (self.actual/'render-scope.json').write_text(json.dumps(self.scope),encoding='utf8')
        comparison=dict(thresholds=dict(ssim=.99,meanChannelError255=2),numericPass=True,errors=[],renderScope=self.scope,
                        views=[dict(view=v,regions=region) for v in ['front','side','back','face']],
                        dynamic=[dict(frame=f,view='front' if f<=180 else 'face',regions=region) for f in range(15,361,15)])
        (self.actual/'comparison.json').write_text(json.dumps(comparison),encoding='utf8')
        self.manifest=dict(Id='one',CardSHA256=self.entry['Hash'],Samples=samples,ReviewStatus='passed',ReviewedUtc='2026-11-11T00:00:00Z',ReviewNotes='Observed paired images and original resolution local detail.')
        for name in ['overview.png','local.png']:(self.evidence/name).write_bytes(b'review sheet')
        self.execution=dict(ExecutionPassed=True,MaterialStatePassed=True,Errors=[])

    def inspect(self):
        (self.evidence/'evidence.json').write_text(json.dumps(self.manifest),encoding='utf8')
        return review.inspect(self.entry,self.root,self.execution)

    def test_observed_review_matches_all_current_images(self):
        row=self.inspect()
        self.assertTrue(row['Passed'])
        self.assertTrue(row['ClothParityVerified'])
        self.assertEqual(row['ValidatedCaptureRequirements'],['shadow maps'])

    def test_missing_or_changed_effect_execution_cannot_pass(self):
        self.scope['UnsupportedEnabledEffects']=['Bloom']
        (self.actual/'render-scope.json').write_text(json.dumps(self.scope),encoding='utf8')
        self.assertFalse(self.inspect()['Passed'])
        self.scope['UnsupportedEnabledEffects']=[];self.scope['EnabledEffects']=[]
        (self.actual/'render-scope.json').write_text(json.dumps(self.scope),encoding='utf8')
        self.assertFalse(self.inspect()['Passed'])

    def test_pending_or_undated_review_cannot_pass(self):
        self.manifest['ReviewStatus']='pending'
        self.assertFalse(self.inspect()['Passed'])
        self.manifest['ReviewStatus']='passed';self.manifest.pop('ReviewedUtc')
        self.assertFalse(self.inspect()['Passed'])

    def test_changed_capture_invalidates_prior_approval(self):
        (self.actual/'dynamic/frame-225.png').write_bytes(b'rerendered image')
        row=self.inspect()
        self.assertFalse(row['Passed'])
        self.assertTrue(any('hash changed' in value for value in row['Errors']))

    def test_duplicate_sample_cannot_cover_a_missing_image(self):
        self.manifest['Samples'][-1]=self.manifest['Samples'][0]
        self.assertFalse(self.inspect()['Passed'])


class CatalogGateChecks(unittest.TestCase):
    def setUp(self):
        self.selected=[{'Id':str(i)} for i in range(84)]
        self.rows=[{'Id':str(i),'ExecutionPassed':True,'MaterialStatePassed':True,'NumericPass':True,'Errors':[]} for i in range(84)]

    def test_numeric_failure_does_not_pass_with_successful_processes(self):
        self.rows[7]['NumericPass']=False
        report=catalog.summarize('frames',self.selected,self.rows)
        self.assertTrue(report['ExecutionPassed'])
        self.assertFalse(report['NumericPass'])

    def test_subset_does_not_pass_full_catalog(self):
        report=catalog.summarize('frames',self.selected[:2],self.rows[:2])
        self.assertTrue(report['SelectedNumericPass'])
        self.assertFalse(report['FullCatalog'])
        self.assertFalse(report['NumericPass'])

    def test_incomplete_duplicate_and_stopped_runs_fail(self):
        for rows,stopped in [(self.rows[:-1],False),(self.rows[:-1]+self.rows[:1],False),(self.rows,True)]:
            self.assertFalse(catalog.summarize('frames',self.selected,rows,stopped)['NumericPass'])

    def test_material_or_evidence_errors_fail(self):
        self.rows[0]['Errors']=['missing completion evidence']
        self.assertFalse(catalog.summarize('frames',self.selected,self.rows)['NumericPass'])
        self.rows[0]['Errors']=[]
        self.rows[0]['MaterialStatePassed']=False
        self.assertFalse(catalog.summarize('frames',self.selected,self.rows)['NumericPass'])

    def test_numeric_success_requires_separate_visual_review(self):
        report=catalog.summarize('frames',self.selected,self.rows)
        self.assertTrue(report['NumericPass'])
        self.assertFalse(report['VisualReviewPassed'])
        self.assertFalse(report['VisualParityVerified'])

    def test_preflight_is_not_visual_acceptance(self):
        report=catalog.summarize('preflight',self.selected,self.rows)
        self.assertTrue(report['MaterialStatePassed'])
        self.assertFalse(report['NumericPass'])


if __name__=='__main__':unittest.main()
