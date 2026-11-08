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
            File.WriteAllText(Path.Combine(output,"texture-metadata-note.txt"),"参考采集保留材质名称、Shader、关键词、属性、队列和最终画面；因旧引擎的原生纹理句柄异常，不枚举所有材质纹理槽。面部与身体合成贴图仍单独导出。此限制不改变渲染或数值门槛。");
            Application.runInBackground=true;
            Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
            Time.captureFramerate=30;
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
            yield return new WaitForSecondsRealtime(5);
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
            foreach(var canvas in FindObjectsOfType<Canvas>())canvas.enabled=false;
            var control=camera.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="CameraControl_Ver2");if(control!=null)control.enabled=false;
            character.ChangeEyesBlinkFlag(false);character.ChangeLookEyesPtn(0);character.ChangeLookNeckPtn(0);
            character.neckLookCtrl.neckLookScript.skipCalc=true;
            if(character.animBody!=null){character.animBody.Play(character.animBody.GetCurrentAnimatorStateInfo(0).fullPathHash,0,0);character.animBody.Update(0);character.animBody.speed=0;}
            CharacterDiagnostics.Write(character.objTop,Path.Combine(output,"animation-only"));
            character.ResetDynamicBoneAll(true);
            camera.transform.position=new Vector3(0,15.9255455f,5.721683f);camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));camera.fieldOfView=35;
            var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);target.Create();camera.targetTexture=target;
            for(int i=0;i<179;i++)yield return new WaitForEndOfFrame();
            Camera.CameraCallback snapshot=c=>{if(c==camera)CharacterDiagnostics.Write(character.objTop,output);};
            Camera.onPreCull+=snapshot;
            yield return new WaitForEndOfFrame();
            Camera.onPreCull-=snapshot;
            CharacterDiagnostics.WriteEnvironment(camera,output);
            var fixture=VisualFixture.Create(character.objTop);VisualFixture.Save(Path.Combine(output,"fixture.json"),fixture);
            foreach(var view in fixture.Views)
            {
                VisualFixture.Set(camera,view);
                for(int i=0;i<VisualFixture.WarmFrames;i++)yield return new WaitForEndOfFrame();
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+".png"));
                if(view.Name=="face")VisualFixture.FaceBounds(camera,character.objTop,Path.Combine(output,"face-region.json"));
                var visible=character.objTop.GetComponentsInChildren<Renderer>(true).Where(r=>r.enabled).ToArray();
                foreach(var r in visible)r.enabled=false;
                for(int i=0;i<VisualFixture.WarmFrames;i++){foreach(var r in visible)r.enabled=false;yield return new WaitForEndOfFrame();}
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+"-background.png"));
                foreach(var r in visible)r.enabled=true;
            }
            camera.transform.position=new Vector3(0,15.9255455f,5.721683f);camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));
            for(int i=0;i<VisualFixture.WarmFrames;i++)yield return new WaitForEndOfFrame();
            yield return StartCoroutine(CaptureDynamics(camera,target,fixture));
            var prior=RenderTexture.active;var pixels=new Texture2D(1280,720,TextureFormat.RGBA32,false);
            try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(output,"environment-face.png"),pixels.EncodeToPNG());}
            finally{RenderTexture.active=prior;camera.targetTexture=null;target.Release();Destroy(target);Destroy(pixels);}
            var volumes=FindObjectsOfType<MonoBehaviour>().Where(c=>c!=null&&c.GetType().Name=="PostProcessVolume").Select(c=>{var type=c.GetType();var profile=(UnityEngine.Object)type.GetProperty("profile").GetValue(c,null);var settings=(IEnumerable)profile.GetType().GetField("settings").GetValue(profile);return new VolumeState{Name=c.name,Enabled=c.enabled,Weight=(float)type.GetField("weight").GetValue(c),Priority=(float)type.GetField("priority").GetValue(c),Profile=profile.name,Effects=settings.Cast<UnityEngine.Object>().Select(e=>JsonUtility.ToJson(e)).ToArray()};}).ToArray();
            using(var stream=File.Create(Path.Combine(output,"environment-state.json")))
                new DataContractJsonSerializer(typeof(EnvironmentState)).WriteObject(stream,new EnvironmentState{RenderingPath=camera.actualRenderingPath.ToString(),CameraPosition=camera.transform.position,CameraRotation=camera.transform.rotation,Volumes=volumes,Lights=FindObjectsOfType<Light>().Where(l=>l.enabled).Select(l=>new LightState{Name=l.name,Color=l.color,Intensity=l.intensity,Rotation=l.transform.rotation,Bounce=l.bounceIntensity,Shadow=l.shadowStrength}).ToArray()});
            var controllers=character.objTop.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&(c.GetType().Name.Contains("Look")||c.GetType().Name=="FrameCorrect"||c.GetType().Name=="IKCorrect")).Select(c=>c.GetType().FullName+" "+JsonUtility.ToJson(c)).ToArray();
            File.WriteAllLines(Path.Combine(output,"controllers.txt"),controllers);
            File.WriteAllLines(Path.Combine(output,"animator.txt"),Enumerable.Range(0,character.animBody.layerCount).Select(i=>i+" "+character.animBody.GetLayerName(i)+" "+character.animBody.GetLayerWeight(i)+" "+character.animBody.GetCurrentAnimatorStateInfo(i).normalizedTime+" "+string.Join(",",character.animBody.GetCurrentAnimatorClipInfo(i).Select(c=>c.clip.name+":"+c.weight).ToArray())).ToArray());
        }
        private IEnumerator CaptureDynamics(Camera camera,RenderTexture target,VisualFixture.Settings fixture)
        {
            string directory=Path.Combine(output,"dynamic");Directory.CreateDirectory(directory);
            var face=character.objHead.GetComponent<FaceBlendShape>();
            face.enabled=false;face.BlinkCtrl=new FBSBlinkControl();
            face.SetBlinkControlEx(null);
            var late=typeof(FaceBlendShape).GetMethod("OnLateUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
            typeof(FBSBlinkControl).GetField("blinkTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(face.BlinkCtrl,Time.time);
            var previous=UnityEngine.Random.state;UnityEngine.Random.InitState(fixture.Seed);var random=UnityEngine.Random.state;UnityEngine.Random.state=previous;
            int sample=0;float animationLength=character.animBody.GetCurrentAnimatorClipInfo(0)[0].clip.length;
            int state=character.animBody.GetCurrentAnimatorStateInfo(0).fullPathHash;
            var blinkStates=new List<string>();
            Camera.CameraCallback tick=c=>{if(c!=camera)return;var global=UnityEngine.Random.state;UnityEngine.Random.state=random;face.BlinkCtrl.CalcBlink();random=UnityEngine.Random.state;UnityEngine.Random.state=global;float openness=face.BlinkCtrl.GetOpenRate();face.EyebrowCtrl.CalcBlend(openness);face.EyesCtrl.CalcBlend(openness);face.MouthCtrl.CalcBlend(0);blinkStates.Add(sample+" "+Time.time+" "+string.Join(";",typeof(FBSBlinkControl).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Select(f=>f.Name+"="+f.GetValue(face.BlinkCtrl)).ToArray()));if(sample>0&&sample%15==0)CharacterDiagnostics.WritePose(character.objTop,Path.Combine(directory,"pose-"+sample.ToString("D3")+".json"),sample/30f);};
            Camera.onPreCull+=tick;
            try
            {
                for(int frame=1;frame<=360;frame++)
                {
                    sample=frame;VisualFixture.Set(camera,fixture.Views[frame<=180?0:3]);
                    character.animBody.Play(state,0,(frame/30f)%animationLength/animationLength);character.animBody.Update(0);
                    yield return new WaitForEndOfFrame();
                    if(frame%15==0)VisualFixture.Pixels(target,Path.Combine(directory,"frame-"+frame.ToString("D3")+".png"));
                }
            }
            finally{Camera.onPreCull-=tick;face.enabled=true;character.ChangeEyesBlinkFlag(false);}
            File.WriteAllLines(Path.Combine(directory,"blink.txt"),blinkStates);File.WriteAllText(Path.Combine(directory,"face-controller.json"),JsonUtility.ToJson(face));
            File.WriteAllText(Path.Combine(directory,"complete.txt"),"360 fixed 1/30 steps; seed 1979; independent blink RNG; front frames 15..180 and face frames 195..360.");
        }
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
