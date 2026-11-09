using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;
using System.Reflection;
using AIChara;
using BepInEx;
using HS2Manager.UnityPreview;
using UnityEngine;

namespace HS2Manager.GameReference
{
    [BepInPlugin("local.hs2manager.reference.test", "HS2Manager Test Reference Capture", "0.1.0")]
    [BepInProcess("HoneySelect2.exe")]
    public sealed class ReferenceCapture : BaseUnityPlugin
    {
        private string output,card;
        private ChaControl character;
        private bool completed;
        private bool maker;
        private string batchOutput;
        private Queue<BatchCard> pending;
        private float requestedAnimationTime;
        private float requestedPartAnimationTime;
        private bool watchCaptureErrors;
        private readonly List<string> captureErrors=new List<string>();
        [Serializable]public sealed class BatchCard {public string Card,Output;}
        private void Awake()
        {
            output=Environment.GetEnvironmentVariable("HS2_REFERENCE_OUTPUT");
            card=Environment.GetEnvironmentVariable("HS2_REFERENCE_CARD");
            maker=Environment.GetEnvironmentVariable("HS2_REFERENCE_MAKER")=="1";
            string manifest=Environment.GetEnvironmentVariable("HS2_REFERENCE_MANIFEST");
            if(!string.IsNullOrEmpty(manifest))
            {
                batchOutput=output;
                using(var stream=File.OpenRead(manifest))pending=new Queue<BatchCard>((BatchCard[])new DataContractJsonSerializer(typeof(BatchCard[])).ReadObject(stream));
                var first=pending.Dequeue();card=first.Card;output=first.Output;
            }
            if(string.IsNullOrEmpty(output) || string.IsNullOrEmpty(card)) {enabled=false;return;}
            Directory.CreateDirectory(output);
            PhysicsResetTrace.Initialize(output);
            File.WriteAllText(Path.Combine(output,"texture-metadata-note.txt"),"参考采集保留材质名称、Shader、关键词、属性、队列和最终画面；因旧引擎的原生纹理句柄异常，不枚举所有材质纹理槽。面部与身体合成贴图仍单独导出。此限制不改变渲染或数值门槛。");
            Application.runInBackground=true;
            Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
            Time.captureFramerate=30;
            Application.logMessageReceived+=(message,stack,type)=>{if(watchCaptureErrors&&type==LogType.Exception)captureErrors.Add(message+"\n"+stack);};
            Logger.LogInfo("Starting explicit test-only reference capture.");
            StartCoroutine(Run());
        }
        private IEnumerator Run()
        {
            float deadline=Time.realtimeSinceStartup+120;
            while(!Singleton<Manager.Character>.IsInstance() || Singleton<Manager.Character>.Instance.chaListCtrl == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Title")
            { if(Time.realtimeSinceStartup>deadline){Fail(new TimeoutException("Game character system did not initialize."));yield break;} yield return null; }
            yield return new WaitForSecondsRealtime(3);
            if(maker)
            {
                try
                {
                    var file=new ChaFileControl();if(!file.LoadCharaFile(card))throw new IOException("Game rejected reference character card.");
                    CharaCustom.CharaCustom.modeNew=false;CharaCustom.CharaCustom.modeSex=file.parameter.sex;CharaCustom.CharaCustom.editCharaFileName=card;
                    Singleton<Manager.Character>.Instance.DeleteCharaAll();
                    Manager.Scene.LoadReserve(new Manager.Scene.Data{levelName="CharaCustom",isFade=false},true);
                }
                catch(Exception error){Fail(error);yield break;}
                deadline=Time.realtimeSinceStartup+120;
                while(!Singleton<CharaCustom.CustomBase>.IsInstance() || Singleton<CharaCustom.CustomBase>.Instance.chaCtrl==null || Singleton<CharaCustom.CustomBase>.Instance.chaCtrl.objTop==null)
                {if(Time.realtimeSinceStartup>deadline){Fail(new TimeoutException("Maker did not initialize."));yield break;}yield return null;}
                character=Singleton<CharaCustom.CustomBase>.Instance.chaCtrl;
            }
            try
            {
                if(!maker)
                {
                var file=new ChaFileControl();
                if(!file.LoadCharaFile(card))throw new IOException("Game rejected reference character card.");
                character=Singleton<Manager.Character>.Instance.CreateChara(file.parameter.sex,null,0,file);
                DontDestroyOnLoad(character.gameObject);
                character.ChangeNowCoordinate();
                character.Load();
                }
                File.WriteAllText(Path.Combine(output,"stage.txt"),"Loaded character in "+UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            }
            catch(Exception error){Fail(error);yield break;}
            CaptureNext:
            watchCaptureErrors=false;captureErrors.Clear();
            yield return new WaitForSecondsRealtime(5);
            try
            {
                EditingValidation.Write(character,card,output);
                PhysicsResetTrace.WriteCreationState();
                var visibilityRepairs=new List<string>();
                foreach(var component in character.objTop.GetComponentsInChildren<CmpBase>(true))
                {
                    var references=component.rendCheckVisible;
                    if(references==null||!references.Any(r=>r==null))continue;
                    component.rendCheckVisible=references.Where(r=>r!=null).ToArray();
                    visibilityRepairs.Add(component.name+": removed "+(references.Length-component.rendCheckVisible.Length)+" destroyed renderer references");
                }
                File.AppendAllLines(Path.Combine(output,"visibility-reference-repairs.txt"),visibilityRepairs.ToArray());
                // The local AdvIK build caches Transform wrappers while the maker
                // replaces its initial skeleton. Remove destroyed entries only;
                // retain all live references and the installed effect settings.
                var advIK=character.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="AdvIKCharaController");
                if(advIK!=null)
                {
                    var field=advIK.GetType().GetField("boneCache",BindingFlags.Instance|BindingFlags.NonPublic);
                    var cache=field==null?null:field.GetValue(advIK) as System.Collections.IDictionary;
                    var dead=new List<object>();
                    if(cache!=null)foreach(System.Collections.DictionaryEntry pair in cache)
                        if((UnityEngine.Object)pair.Value==null)dead.Add(pair.Key);
                    foreach(var key in dead)cache.Remove(key);
                    File.WriteAllLines(Path.Combine(output,"reference-initialization.txt"),new[]{"Removed destroyed AdvIK bone-cache entries: "+dead.Count,"No live bone references or plugin settings changed."});
                }
                var heelSupport=character.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="HeelsController");
                if(heelSupport!=null)
                {
                    var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
                    var rows=new List<string>();
                    var transforms=heelSupport.GetType().GetField("_transformVectors",flags).GetValue(heelSupport) as System.Collections.IDictionary;
                    foreach(System.Collections.DictionaryEntry pair in transforms)
                        rows.Add(((Transform)pair.Key).name+" "+string.Join(";",((Vector3[])pair.Value).Select(v=>v.ToString("R")).ToArray()));
                    var config=heelSupport.GetType().GetField("_currentConfig",flags).GetValue(heelSupport);
                    rows.Add("Config="+(config==null?"null":JsonUtility.ToJson(config)));
                    rows.Add("Shoe="+character.nowCoordinate.clothes.parts[7].id+" State="+character.fileStatus.clothesState[7]);
                    File.WriteAllLines(Path.Combine(output,"heels.txt"),rows.ToArray());
                }
                var support=character.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="UncensorSelectorController");
                if(support!=null)
                {
                    var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
                    foreach(string kind in new[]{"Body","Penis","Balls"})
                    {
                        var data=support.GetType().GetProperty(kind+"Data",flags).GetValue(support,null);
                        if(data!=null)File.WriteAllText(Path.Combine(output,"supplement-"+kind.ToLowerInvariant()+".json"),JsonUtility.ToJson(data,true));
                        var dictionary=support.GetType().DeclaringType.GetField(kind+"Dictionary",BindingFlags.Static|BindingFlags.Public).GetValue(null) as System.Collections.IDictionary;
                        var rows=new List<string>();
                        foreach(System.Collections.DictionaryEntry item in dictionary)rows.Add(JsonUtility.ToJson(item.Value));
                        File.WriteAllLines(Path.Combine(output,"supplement-catalog-"+kind.ToLowerInvariant()+".jsonl"),rows.ToArray());
                    }
                }
            }
            catch(Exception error){Fail(error);yield break;}
            watchCaptureErrors=true;
            Logger.LogInfo("REFERENCE FIXTURE READY: character and plugin references initialized.");
            if(maker)
            {
                // Record the real maker frame before establishing a controlled diagnostic fixture.
                yield return new WaitForEndOfFrame();
                var screen=new Texture2D(Screen.width,Screen.height,TextureFormat.RGBA32,false);
                screen.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);screen.Apply();File.WriteAllBytes(Path.Combine(output,"maker-screen.png"),screen.EncodeToPNG());Destroy(screen);
                File.WriteAllText(Path.Combine(output,"maker-state.json"),JsonUtility.ToJson(new MakerState{Scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,Pose=Singleton<CharaCustom.CustomBase>.Instance.poseNo,State=character.animBody==null?"":string.Join(",",character.animBody.GetCurrentAnimatorClipInfo(0).Select(c=>c.clip.name).ToArray()),Quality=QualitySettings.GetQualityLevel(),TextureLimit=QualitySettings.masterTextureLimit,AA=QualitySettings.antiAliasing,Ambient=RenderSettings.ambientLight,AmbientIntensity=RenderSettings.ambientIntensity,ReflectionIntensity=RenderSettings.reflectionIntensity,CameraComponents=Camera.allCameras.SelectMany(c=>c.GetComponents<MonoBehaviour>()).Where(c=>c!=null).Select(c=>c.GetType().FullName).ToArray()},true));
                var capture=CaptureMakerEnvironment();
                while(true)
                {
                    bool next;
                    try{next=capture.MoveNext();}
                    catch(Exception error){Fail(error);break;}
                    if(!next)break;
                    yield return capture.Current;
                }
                if(completed)yield break;
                watchCaptureErrors=false;
                File.WriteAllLines(Path.Combine(output,"capture-runtime-errors.txt"),captureErrors.ToArray());
                if(captureErrors.Count>0){Fail(new InvalidOperationException("Game raised exceptions during the reference fixture. See capture-runtime-errors.txt."));yield break;}
                File.WriteAllText(Path.Combine(output,"complete.txt"),"Maker environment reference captured. Comparison is required for acceptance.");
                if(pending!=null&&pending.Count>0)
                {
                    var next=pending.Dequeue();card=next.Card;output=next.Output;Directory.CreateDirectory(output);
                    var priorCharacter=character;
                    var nextFile=new ChaFileControl();if(!nextFile.LoadCharaFile(card)){Fail(new IOException("Batch card rejected: "+card));yield break;}
                    CharaCustom.CharaCustom.modeNew=false;CharaCustom.CharaCustom.modeSex=nextFile.parameter.sex;CharaCustom.CharaCustom.editCharaFileName=card;
                    Singleton<Manager.Character>.Instance.DeleteCharaAll();
                    Manager.Scene.LoadReserve(new Manager.Scene.Data{levelName="CharaCustom",isFade=false},true);
                    deadline=Time.realtimeSinceStartup+120;
                    do
                    {
                        if(Time.realtimeSinceStartup>deadline){Fail(new TimeoutException("Batch maker scene did not initialize: "+card));yield break;}
                        yield return null;
                    }while(!Singleton<CharaCustom.CustomBase>.IsInstance()||Singleton<CharaCustom.CustomBase>.Instance.chaCtrl==null||Singleton<CharaCustom.CustomBase>.Instance.chaCtrl==priorCharacter||Singleton<CharaCustom.CustomBase>.Instance.chaCtrl.objTop==null);
                    character=Singleton<CharaCustom.CustomBase>.Instance.chaCtrl;
                    goto CaptureNext;
                }
                completed=true;
                if(batchOutput!=null)File.WriteAllText(Path.Combine(batchOutput,"complete.txt"),"Batch references captured.");
                File.WriteAllText(Path.Combine(batchOutput??output,"finished.txt"),"success");
                Application.Quit(0);
                yield break;
            }
            try
            {
                if(character==null || character.objTop==null)throw new IOException("Reference character root was destroyed before capture.");
                character.ChangeEyesBlinkFlag(false);
                character.ChangeLookEyesPtn(0);
                character.ChangeLookNeckPtn(0);
                if(character.animBody!=null)character.animBody.enabled=false;
                // Isolate the fixture from title-screen geometry; maker-environment capture is separate.
                foreach(var renderer in FindObjectsOfType<Renderer>())
                    if(!renderer.transform.IsChildOf(character.objTop.transform))renderer.enabled=false;
                character.ChangeEyesOpenMax(1);
                character.ChangeMouthOpenMax(0);
                character.ChangeMouthOpenMin(0);
                foreach(var camera in Camera.allCameras)camera.enabled=false;
                foreach(var light in FindObjectsOfType<Light>())light.enabled=false;
                var checkCamera=new GameObject("Reference fixture camera").AddComponent<Camera>();
                checkCamera.enabled=false;checkCamera.clearFlags=CameraClearFlags.SolidColor;checkCamera.backgroundColor=new Color(.16f,.17f,.19f);checkCamera.fieldOfView=35;checkCamera.farClipPlane=2000;
                checkCamera.renderingPath=RenderingPath.Forward;
                QualitySettings.antiAliasing=0;
                RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight=new Color(.35f,.35f,.35f);
                RenderSettings.ambientIntensity=1;RenderSettings.reflectionIntensity=0;
                foreach(var renderer in character.objTop.GetComponentsInChildren<Renderer>(true))
                {renderer.lightProbeUsage=UnityEngine.Rendering.LightProbeUsage.Off;renderer.reflectionProbeUsage=UnityEngine.Rendering.ReflectionProbeUsage.Off;}
                var key=new GameObject("Reference fixture light").AddComponent<Light>();
                key.type=LightType.Directional;key.intensity=1;key.transform.rotation=Quaternion.Euler(25,145,0);
                var renderers=character.objTop.GetComponentsInChildren<Renderer>().Where(r=>r.enabled && !r.name.StartsWith("O_hit_",StringComparison.OrdinalIgnoreCase)).ToArray();
                var bounds=renderers[0].bounds;foreach(var renderer in renderers.Skip(1))bounds.Encapsulate(renderer.bounds);
                float distance=33f;
                bounds.center=new Vector3(0,8.5f,0);
                foreach(float angle in new[]{0f,90f,180f})
                {checkCamera.transform.position=bounds.center+Quaternion.Euler(0,angle,0)*new Vector3(0,0,distance);checkCamera.transform.LookAt(bounds.center);Capture(checkCamera,"angle-"+angle+".png");}
                var head=character.objTop.GetComponentsInChildren<Transform>().First(t=>t.name=="cf_J_Head");
                checkCamera.transform.position=new Vector3(0,15.9255455f,5.721683f);checkCamera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));Capture(checkCamera,"face.png");
                CharacterDiagnostics.Write(character.objTop,output);
                File.WriteAllText(Path.Combine(output,"complete.txt"),"Diagnostic fixture captured. This is not maker environment or visual parity acceptance.");
                completed=true;
                File.WriteAllText(Path.Combine(output,"finished.txt"),"success");
                Application.Quit(0);
            }
            catch(Exception error){Fail(error);}
        }
        private IEnumerator CaptureMakerEnvironment()
        {
            var camera=Camera.main;
            using(var shaderClock=new CharacterShaderClock(camera))
            using(var partAnimations=new CharacterPartAnimationClock())
            {
            var characterRoots=new[]{character.objTop}.Concat(character.objTop.GetComponentsInChildren<Studio.ParticleParent>(true).Where(p=>p.ObjOriginal!=null).Select(p=>p.ObjOriginal)).Distinct().ToArray();
            foreach(var animator in characterRoots.SelectMany(r=>r.GetComponentsInChildren<Animator>(true)).Where(a=>a!=character.animBody))partAnimations.Add(animator,animator.gameObject,true);
            foreach(var animation in characterRoots.SelectMany(r=>r.GetComponentsInChildren<Animation>(true)))partAnimations.Add(animation,animation.gameObject,true);
            File.WriteAllLines(Path.Combine(output,"part-animations.txt"),partAnimations.Describe());
            Func<Renderer[]> characterRenderers=()=>characterRoots.Where(r=>r!=null).SelectMany(r=>r.GetComponentsInChildren<Renderer>(true)).Distinct().ToArray();
            foreach(var canvas in FindObjectsOfType<Canvas>())canvas.enabled=false;
            // HeightBar implements the same switch for the game's screenshot
            // plugin. Its measurement helpers are editor UI outside the canvases.
            var heightBar=FindObjectsOfType<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().FullName=="HeightBar.HeightBar");
            if(heightBar!=null)heightBar.GetType().GetProperty("ForceHideBars").SetValue(heightBar,true,null);
            var control=camera.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="CameraControl_Ver2");if(control!=null)control.enabled=false;
            character.ChangeEyesBlinkFlag(false);character.ChangeLookEyesPtn(0);character.ChangeLookNeckPtn(0);
            character.neckLookCtrl.neckLookScript.skipCalc=true;
            // Disabling blinking can freeze a partially closed blink. Drive the
            // requested static openness in the same pre-skinning phase used by
            // the dynamic fixture instead of retaining a load-time blink sample.
            var staticFace=character.objHead.GetComponent<FaceBlendShape>();
            staticFace.enabled=false;
            var staticFaceTick=camera.gameObject.AddComponent<ReferenceLateTick>();
            staticFaceTick.Tick=()=>{staticFace.EyebrowCtrl.CalcBlend(1);staticFace.EyesCtrl.CalcBlend(1);staticFace.MouthCtrl.CalcBlend(0);};
            int idleState=character.animBody.GetCurrentAnimatorStateInfo(0).fullPathHash;
            // The game's corrective bones restore their saved transforms at end of
            // frame. Sampling from an end-of-frame coroutine races that restore.
            // Evaluate in Update, before the normal IK/physics LateUpdate stages.
            requestedAnimationTime=0;
            var animationTick=camera.gameObject.AddComponent<ReferenceAnimationTick>();
            requestedPartAnimationTime=0;
            animationTick.Tick=()=>{character.animBody.speed=0;character.animBody.Play(idleState,0,requestedAnimationTime);character.animBody.Update(0);partAnimations.Set(requestedPartAnimationTime);};
            Action sampleIdle=()=>{requestedAnimationTime=0;requestedPartAnimationTime=0;};
            animationTick.Tick();
            CharacterDiagnostics.Write(character.objTop,Path.Combine(output,"animation-only"));
            int ikTicks=0;string ikPose="";
            var foot=character.objTop.GetComponentsInChildren<Transform>(true).First(t=>t.name=="cf_J_Foot01_L");
            RootMotion.FinalIK.IKSolver.UpdateDelegate traceIK=()=>{ikTicks++;ikPose=Time.frameCount+" "+foot.localEulerAngles.ToString("R");};
            character.fullBodyIK.solver.OnPostUpdate+=traceIK;
            var traceSamples=new HashSet<int>();int traceWarm=0;
            RootMotion.FinalIK.IKSolver.UpdateDelegate traceInput=()=>
            {
                if(Environment.GetEnvironmentVariable("HS2_IK_TRACE")!="1")return;
                int sample=(int)Mathf.Round(requestedPartAnimationTime*30);
                if(sample==0&&++traceWarm<20)return;
                if((sample==0||sample==195||sample==300)&&traceSamples.Add(sample))CharacterDiagnostics.WritePose(character.objTop,Path.Combine(output,"ik-input-"+sample.ToString("D3")+".json"),requestedPartAnimationTime);
            };
            character.fullBodyIK.solver.OnPreRead+=traceInput;
            // Let the maker apply the selected forward gaze and corrective bones
            // before restoring particle transforms and zeroing their velocities.
            for(int poseFrame=0;poseFrame<4;poseFrame++)yield return new WaitForEndOfFrame();
            foreach(var component in characterRoots.SelectMany(r=>r.GetComponentsInChildren<MonoBehaviour>(true)).Where(c=>c!=null&&(c.GetType().Name=="DynamicBone"||c.GetType().Name=="DynamicBone_Ver02")))
            {
                var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
                var init=component.GetType().GetMethod("InitTransforms",flags);
                if(init!=null)init.Invoke(component,null);
                var reset=component.GetType().GetMethod("ResetParticlesPosition",flags);
                if(reset!=null)reset.Invoke(component,null);
            }
            character.ResetDynamicBoneAll(true);
            VisualFixture.ResetCloth(character.objTop);
            WriteDynamics();
            File.Move(Path.Combine(output,"dynamics.json"),Path.Combine(output,"initial-dynamics.json"));
            CharacterDiagnostics.WritePose(character.objTop,Path.Combine(output,"initial-pose.json"),0);
            camera.transform.position=new Vector3(0,15.9255455f,5.721683f);camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));camera.fieldOfView=35;
            var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);target.Create();camera.targetTexture=target;
            for(int i=0;i<179;i++)
            {
                sampleIdle();yield return new WaitForEndOfFrame();
                if(i<20&&Environment.GetEnvironmentVariable("HS2_PHYSICS_TRACE")=="1")WriteDynamics("physics-step-"+(i+1).ToString("D2")+".json");
            }
            Camera.CameraCallback snapshot=c=>{if(c==camera){
                CharacterDiagnostics.Write(character.objTop,output);
                if(Environment.GetEnvironmentVariable("HS2_MESH_TRACE")=="1")
                    foreach(var detached in character.objTop.GetComponentsInChildren<Studio.ParticleParent>(true))
                        if(detached.ObjOriginal!=null)
                            CharacterDiagnostics.Write(detached.ObjOriginal,Path.Combine(output,"detached-"+detached.name));
                var callback=character.fullBodyIK.solver.OnPostUpdate;
                File.WriteAllLines(Path.Combine(output,"ik-callbacks.txt"),new[]{"Enabled="+character.fullBodyIK.isActiveAndEnabled,"Weight="+character.fullBodyIK.solver.IKPositionWeight,"PostIK="+ikTicks+" "+ikPose,"PreCull="+Time.frameCount+" "+foot.localEulerAngles.ToString("R")}.Concat(callback==null?new string[0]:callback.GetInvocationList().Select(d=>d.Method.DeclaringType.FullName+"."+d.Method.Name)).ToArray());
            }};
            Camera.onPreCull+=snapshot;
            sampleIdle();yield return new WaitForEndOfFrame();
            Camera.onPreCull-=snapshot;
            character.fullBodyIK.solver.OnPostUpdate-=traceIK;
            CharacterDiagnostics.WriteEnvironment(camera,output);
            WriteDynamics();
            File.WriteAllText(Path.Combine(output,"maker-ik.json"),JsonUtility.ToJson(character.fullBodyIK));
            File.WriteAllLines(Path.Combine(output,"scene-renderers.txt"),FindObjectsOfType<Renderer>().Where(r=>!r.transform.IsChildOf(character.objTop.transform)).Select(r=>r.name+" parent="+(r.transform.parent==null?"":r.transform.parent.name)+" position="+r.transform.position.ToString("R")+" scale="+r.transform.lossyScale.ToString("R")+" bounds="+r.bounds+" materials="+string.Join(";",r.sharedMaterials.Select(m=>m==null?"null":m.name+"/"+m.shader.name).ToArray())).ToArray());
            var fixture=VisualFixture.Create(character.objTop,characterRenderers());fixture.PartAnimationClockVersion=partAnimations.Count>0?1:0;VisualFixture.Save(Path.Combine(output,"fixture.json"),fixture);
            foreach(var view in fixture.Views)
            {
                PhysicsResetTrace.Phase=view.Name+"-warm";
                VisualFixture.Set(camera,view);
                ResetTemporalHistory(camera);
                for(int i=0;i<VisualFixture.WarmFrames;i++)
                    using(i==VisualFixture.WarmFrames-3?VisualFixture.GraphicsCapture(view.Name):null)
                    {sampleIdle();yield return new WaitForEndOfFrame();}
                if(Mathf.Abs(character.animBody.GetCurrentAnimatorStateInfo(0).normalizedTime)>1e-5f)throw new InvalidOperationException("Static animation advanced outside the fixture clock.");
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+".png"));
                CharacterDiagnostics.WritePose(character.objTop,Path.Combine(output,view.Name+"-pose.json"),0);
                WriteDynamics(view.Name+"-dynamics.json");
                WriteAnimationState(Path.Combine(output,view.Name+"-animation.json"));
                if(view.Name=="face")VisualFixture.FaceBounds(camera,character.objTop,Path.Combine(output,"face-region.json"));
                var visible=characterRenderers().Where(r=>r!=null&&r.enabled).ToArray();
                PhysicsResetTrace.Phase=view.Name+"-background";
                foreach(var r in visible)r.enabled=false;
                for(int i=0;i<VisualFixture.WarmFrames;i++){sampleIdle();foreach(var r in visible)r.enabled=false;yield return new WaitForEndOfFrame();}
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+"-background.png"));
                WriteDynamics(view.Name+"-background-dynamics.json");
                foreach(var r in visible)r.enabled=true;
            }
            camera.transform.position=new Vector3(0,15.9255455f,5.721683f);camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));
            ResetTemporalHistory(camera);
            for(int i=0;i<VisualFixture.WarmFrames;i++){sampleIdle();yield return new WaitForEndOfFrame();}
            staticFaceTick.Tick=null;Destroy(staticFaceTick);
            yield return StartCoroutine(CaptureDynamics(camera,target,fixture,shaderClock));
            var prior=RenderTexture.active;var pixels=new Texture2D(1280,720,TextureFormat.RGBA32,false);
            try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(output,"environment-face.png"),pixels.EncodeToPNG());}
            finally{RenderTexture.active=prior;camera.targetTexture=null;target.Release();Destroy(target);Destroy(pixels);}
            var volumes=FindObjectsOfType<MonoBehaviour>().Where(c=>c!=null&&c.GetType().Name=="PostProcessVolume").Select(c=>{var type=c.GetType();var profile=(UnityEngine.Object)type.GetProperty("profile").GetValue(c,null);var settings=(IEnumerable)profile.GetType().GetField("settings").GetValue(profile);return new VolumeState{Name=c.name,Enabled=c.enabled,Weight=(float)type.GetField("weight").GetValue(c),Priority=(float)type.GetField("priority").GetValue(c),Profile=profile.name,Effects=settings.Cast<UnityEngine.Object>().Select(e=>JsonUtility.ToJson(e)).ToArray()};}).ToArray();
            using(var stream=File.Create(Path.Combine(output,"environment-state.json")))
                new DataContractJsonSerializer(typeof(EnvironmentState)).WriteObject(stream,new EnvironmentState{RenderingPath=camera.actualRenderingPath.ToString(),CameraPosition=camera.transform.position,CameraRotation=camera.transform.rotation,Volumes=volumes,Lights=FindObjectsOfType<Light>().Where(l=>l.enabled).Select(l=>new LightState{Name=l.name,Color=l.color,Intensity=l.intensity,Rotation=l.transform.rotation,Bounce=l.bounceIntensity,Shadow=l.shadowStrength}).ToArray()});
            var controllers=character.objTop.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&(c.GetType().Name.Contains("Look")||c.GetType().Name=="FrameCorrect"||c.GetType().Name=="IKCorrect"||c.GetType().Name=="FullBodyBipedIK")).Select(c=>c.GetType().FullName+" "+JsonUtility.ToJson(c)).ToArray();
            File.WriteAllLines(Path.Combine(output,"controllers.txt"),controllers);
            File.WriteAllLines(Path.Combine(output,"animator.txt"),Enumerable.Range(0,character.animBody.layerCount).Select(i=>i+" "+character.animBody.GetLayerName(i)+" "+character.animBody.GetLayerWeight(i)+" "+character.animBody.GetCurrentAnimatorStateInfo(i).normalizedTime+" "+string.Join(",",character.animBody.GetCurrentAnimatorClipInfo(i).Select(c=>c.clip.name+":"+c.weight).ToArray())).ToArray());
            animationTick.Tick=null;Destroy(animationTick);
            character.fullBodyIK.solver.OnPreRead-=traceInput;
            }
        }
        private static void ResetTemporalHistory(Camera camera)
        {
            VisualFixture.ResetDithering(camera);
            foreach(var effect in camera.GetComponents<MonoBehaviour>().Where(c=>c!=null))
            {
                var type=effect.GetType();
                if(type.Name=="CTAA_PC")
                {
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    type.GetField("frameCounter",flags).SetValue(effect,0);
                    type.GetField("firstFrame",flags).SetValue(effect,true);
                    type.GetField("swap",flags).SetValue(effect,true);
                }
                if(type.Name=="PostProcessLayer")type.GetMethod("ResetHistory").Invoke(effect,null);
            }
        }
        [Serializable] public sealed class ColliderDiagnostic {public string Name;public Vector3 Center,World,Scale;public float Radius,Height;public int Axis,Bound;}
        [Serializable] public sealed class PhysicsDiagnostic {public string Host,Root,Configuration;public int RootId;public string[] CurveWrapModes;public bool Enabled;public Vector3 LocalGravity;public ParticleDiagnostic[] Particles;public ColliderDiagnostic[] Colliders;}
        [Serializable] public sealed class ParticleDiagnostic {public string Name;public int Parent;public Vector3 RestPosition,End,Position;public Quaternion RestRotation;public float Distance,Radius,Damping,Elasticity,Stiffness,Inert;}
        private void WriteDynamics(string filename="dynamics.json")
        {
            var states=new List<PhysicsDiagnostic>();
            foreach(var component in character.objTop.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&c.GetType().Name=="DynamicBone"))
            {
                var type=component.GetType();var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
                var root=(Transform)type.GetField("m_Root",flags).GetValue(component);
                var particles=(IEnumerable)type.GetField("m_Particles",flags).GetValue(component);
                states.Add(new PhysicsDiagnostic{Host=component.name,Root=root==null?"":root.name,RootId=root==null?0:root.GetInstanceID(),Enabled=component.isActiveAndEnabled,Configuration=JsonUtility.ToJson(component),LocalGravity=(Vector3)type.GetField("m_LocalGravity",flags).GetValue(component),
                    Colliders=((IEnumerable)type.GetField("m_Colliders",flags).GetValue(component)).Cast<DynamicBoneCollider>().Where(c=>c!=null&&c.enabled).Select(c=>new ColliderDiagnostic{Name=c.name,Center=c.m_Center,World=c.transform.position,Scale=c.transform.lossyScale,Radius=c.m_Radius,Height=c.m_Height,Axis=(int)c.m_Direction,Bound=(int)c.m_Bound}).ToArray(),
                    CurveWrapModes=new[]{"Damping","Elasticity","Stiffness","Inert","Radius"}.Select(n=>{var curve=(AnimationCurve)type.GetField("m_"+n+"Distrib",flags).GetValue(component);return n+":"+curve.preWrapMode+"/"+curve.postWrapMode;}).ToArray(),
                    Particles=particles.Cast<object>().Select(p=>{var t=p.GetType();Func<string,object> get=n=>t.GetField(n,flags).GetValue(p);var bone=(Transform)get("m_Transform");return new ParticleDiagnostic{Name=bone==null?"<end>":bone.name,Parent=(int)get("m_ParentIndex"),Distance=(float)get("m_BoneLength"),RestPosition=(Vector3)get("m_InitLocalPosition"),RestRotation=(Quaternion)get("m_InitLocalRotation"),End=(Vector3)get("m_EndOffset"),Position=(Vector3)get("m_Position"),Radius=(float)get("m_Radius"),Damping=(float)get("m_Damping"),Elasticity=(float)get("m_Elasticity"),Stiffness=(float)get("m_Stiffness"),Inert=(float)get("m_Inert")};}).ToArray()});
            }
            VisualFixture.Save(Path.Combine(output,filename),states.ToArray());
        }
        private IEnumerator CaptureDynamics(Camera camera,RenderTexture target,VisualFixture.Settings fixture,CharacterShaderClock shaderClock)
        {
            string directory=Path.Combine(output,"dynamic");Directory.CreateDirectory(directory);
            var face=character.objHead.GetComponent<FaceBlendShape>();
            face.enabled=false;face.BlinkCtrl=new FBSBlinkControl();
            face.SetBlinkControlEx(null);
            var late=typeof(FaceBlendShape).GetMethod("OnLateUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
            ReferenceBlinkClock.Enabled=true;ReferenceBlinkClock.Time=0;
            typeof(FBSBlinkControl).GetField("blinkTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(face.BlinkCtrl,0f);
            var previous=UnityEngine.Random.state;UnityEngine.Random.InitState(fixture.Seed);var random=UnityEngine.Random.state;UnityEngine.Random.state=previous;
            int sample=0;float animationLength=character.animBody.GetCurrentAnimatorClipInfo(0)[0].clip.length;
            int state=character.animBody.GetCurrentAnimatorStateInfo(0).fullPathHash;
            var blinkStates=new List<string>();
            // FaceBlendShape normally runs in LateUpdate, before native skinning.
            // Updating its weights from onPreCull can leave the rendered mesh one
            // frame behind the recorded weights when updateWhenOffscreen is on.
            var faceTick=camera.gameObject.AddComponent<ReferenceLateTick>();
            faceTick.Tick=()=>{ReferenceBlinkClock.Time=sample/30f;var global=UnityEngine.Random.state;UnityEngine.Random.state=random;face.BlinkCtrl.CalcBlink();random=UnityEngine.Random.state;UnityEngine.Random.state=global;float openness=face.BlinkCtrl.GetOpenRate();face.EyebrowCtrl.CalcBlend(openness);face.EyesCtrl.CalcBlend(openness);face.MouthCtrl.CalcBlend(0);blinkStates.Add(sample+" "+Time.time+" "+string.Join(";",typeof(FBSBlinkControl).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Select(f=>f.Name+"="+f.GetValue(face.BlinkCtrl)).ToArray()));};
            Camera.CameraCallback tick=c=>{if(c==camera&&sample>0&&sample%15==0)CharacterDiagnostics.WritePose(character.objTop,Path.Combine(directory,"pose-"+sample.ToString("D3")+".json"),sample/30f);};
            Camera.onPreCull+=tick;
            try
            {
                for(int frame=1;frame<=360;frame++)
                {
                    sample=frame;VisualFixture.Set(camera,fixture.Views[frame<=180?0:3]);
                    shaderClock.Set(frame/30f,1f/30);
                    requestedAnimationTime=(frame/30f)%animationLength/animationLength;
                    requestedPartAnimationTime=frame/30f;
                    yield return new WaitForEndOfFrame();
                    if(Mathf.Abs(character.animBody.GetCurrentAnimatorStateInfo(0).normalizedTime-requestedAnimationTime)>1e-5f)throw new InvalidOperationException("Dynamic animation advanced outside the fixture clock.");
                    if(frame%15==0)VisualFixture.Pixels(target,Path.Combine(directory,"frame-"+frame.ToString("D3")+".png"));
                }
            }
            finally{ReferenceBlinkClock.Enabled=false;Camera.onPreCull-=tick;faceTick.Tick=null;Destroy(faceTick);face.enabled=true;character.ChangeEyesBlinkFlag(false);}
            File.WriteAllLines(Path.Combine(directory,"blink.txt"),blinkStates);File.WriteAllText(Path.Combine(directory,"face-controller.json"),JsonUtility.ToJson(face));
            WriteAnimationState(Path.Combine(directory,"animation.json"));
            File.WriteAllText(Path.Combine(directory,"complete.txt"),"360 fixed 1/30 steps; seed 1979; independent blink RNG; front frames 15..180 and face frames 195..360.");
        }
        private void WriteAnimationState(string path)
        {
            var state=character.animBody.GetCurrentAnimatorStateInfo(0);
            VisualFixture.Save(path,new AnimationDiagnostic{Speed=character.animBody.speed,Enabled=character.animBody.enabled,NormalizedTime=state.normalizedTime,StateSpeed=state.speed,StateSpeedMultiplier=state.speedMultiplier,Delta=Time.deltaTime,TimeScale=Time.timeScale,CaptureRate=Time.captureFramerate,UpdateMode=(int)character.animBody.updateMode});
        }
        [Serializable]public sealed class AnimationDiagnostic{public float Speed,NormalizedTime,StateSpeed,StateSpeedMultiplier,Delta,TimeScale;public bool Enabled;public int CaptureRate,UpdateMode;}
        private void Capture(Camera camera,string name)
        {
            var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
            var prior=RenderTexture.active;var pixels=new Texture2D(1280,720,TextureFormat.RGBA32,false);
            try{target.Create();camera.targetTexture=target;camera.Render();RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(output,name),pixels.EncodeToPNG());}
            finally{RenderTexture.active=prior;camera.targetTexture=null;target.Release();Destroy(target);Destroy(pixels);}
        }
        private void Fail(Exception error){Logger.LogError(error);File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString());completed=true;File.WriteAllText(Path.Combine(output,"finished.txt"),"failure");Application.Quit(1);}
        private void OnDestroy(){if(!completed && !string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"interrupted.txt"),"Capture interrupted before completion.");}
        [Serializable]private sealed class MakerState{public string Scene,State;public int Pose,Quality,TextureLimit,AA;public Color Ambient;public float AmbientIntensity,ReflectionIntensity;public string[] CameraComponents;}
        [Serializable]public sealed class VolumeState{public string Name,Profile;public bool Enabled;public float Weight,Priority;public string[] Effects;}
        [Serializable]public sealed class LightState{public string Name;public Color Color;public float Intensity,Bounce,Shadow;public Quaternion Rotation;}
        [Serializable]public sealed class EnvironmentState{public string RenderingPath;public Vector3 CameraPosition;public Quaternion CameraRotation;public VolumeState[] Volumes;public LightState[] Lights;}
    }
}

namespace HS2Manager.GameReference
{
    [DefaultExecutionOrder(10000)]
    internal sealed class ReferenceAnimationTick : UnityEngine.MonoBehaviour
    {
        internal System.Action Tick;
        private void Update() { if(Tick!=null)Tick(); }
    }
    internal sealed class ReferenceLateTick : UnityEngine.MonoBehaviour
    {
        internal System.Action Tick;
        private void LateUpdate() { if(Tick!=null)Tick(); }
    }
}
