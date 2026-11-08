using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed class CharacterCheckRunner : MonoBehaviour
    {
        private OriginalCharacter character;
        private OriginalEnvironment environment;
        private Camera environmentCamera;
        private OriginalAssetStore sharedAssets;
        private IEnumerator Start()
        {
            int exitCode = 1;
            string output = Argument("--output");
            Time.captureFramerate=30;
            try
            {
                Directory.CreateDirectory(output);
                if(Argument("--prime-character")==null)
                {
                    var definition = CharacterData.Read(Argument("--character"));
                    character = new OriginalCharacter(definition);
                }
            }
            catch (Exception error) { Debug.LogException(error); File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString()); }
            if(Argument("--prime-character")!=null)
            {
                var steps=new Stack<IEnumerator>();steps.Push(ExerciseReuse(output));
                try
                {
                    while(steps.Count>0)
                    {
                        object next=null;Exception failure=null;
                        try
                        {
                            if(!steps.Peek().MoveNext()){(steps.Pop() as IDisposable)?.Dispose();continue;}
                            next=steps.Peek().Current;
                        }
                        catch(Exception error){failure=error;}
                        if(failure!=null){Debug.LogException(failure);File.WriteAllText(Path.Combine(output,"error.txt"),failure.ToString());character?.Dispose();character=null;break;}
                        var nested=next as IEnumerator;if(nested!=null){steps.Push(nested);continue;}
                        yield return next;
                    }
                }
                finally{while(steps.Count>0)(steps.Pop() as IDisposable)?.Dispose();}
            }
            if (character != null)
            {
                yield return null;
                try
                {
                    var bounds = character.Bounds();
                    var camera = new GameObject("Character check camera").AddComponent<Camera>();
                    camera.enabled = false;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.16f,.17f,.19f);
                    camera.fieldOfView = 35;
                    camera.farClipPlane = 2000;
                    camera.renderingPath = RenderingPath.Forward;
                    QualitySettings.antiAliasing = 0;
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(.35f,.35f,.35f);
                    RenderSettings.ambientIntensity = 1; RenderSettings.reflectionIntensity = 0;
                    var light = new GameObject("Character check light").AddComponent<Light>();
                    light.type = LightType.Directional; light.intensity = 1;
                    light.transform.rotation = Quaternion.Euler(25,145,0);
                    float distance = 33f;
                    bounds.center = new Vector3(0,8.5f,0);
                    foreach (var angle in new[] { 0f,90f,180f })
                    {
                        camera.transform.position = bounds.center + Quaternion.Euler(0,angle,0) * new Vector3(0,0,distance);
                        camera.transform.LookAt(bounds.center);
                        Capture(camera,Path.Combine(output,"angle-" + angle + ".png"));
                    }
                    Transform face;
                    if (character.Bones.TryGetValue("cf_J_Head",out face))
                    {
                        camera.transform.position = new Vector3(0,15.9255455f,5.721683f);
                        camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));
                        Capture(camera,Path.Combine(output,"face.png"));
                    }
                    environment=character.CreateEnvironment(camera);
                    character.UpdateMotion(0,0);character.PoseFace(1);
                    light.enabled=false;
                    camera.transform.position=new Vector3(0,15.9255455f,5.721683f);
                    camera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));
                    environmentCamera=camera;
                    File.WriteAllLines(Path.Combine(output,"diagnostics.txt"),character.Diagnostics);
                    CharacterDiagnostics.Write(character.Root,output);
                    Debug.Log("CHARACTER CHECK: " + character.Root.GetComponentsInChildren<Renderer>().Length + " renderers; bounds=" + bounds + "; diagnostics=" + character.Diagnostics.Count);
                    exitCode = 0;
                }
                catch (Exception error) { Debug.LogException(error); File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString()); }
            }
            if(exitCode==0 && environmentCamera!=null)
            {
                var capture=CaptureEnvironment(output);
                while(true)
                {
                    bool next;
                    try{next=capture.MoveNext();}
                    catch(Exception error){Debug.LogException(error);File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString());exitCode=1;break;}
                    if(!next)break;
                    yield return capture.Current;
                }
            }
            environment?.Dispose();environment=null;
            if (character != null) character.Dispose();
            character = null;
            sharedAssets?.Dispose();sharedAssets=null;
            Application.Quit(exitCode);
        }
        private IEnumerator ExerciseReuse(string output)
        {
            sharedAssets=new OriginalAssetStore();
            string target=Argument("--character"),prime=Argument("--prime-character");
            var sequence=new[]{target,prime,target};
            for(int i=0;i<sequence.Length;i++)
            {
                var definition=CharacterData.Read(sequence[i]);
                yield return sharedAssets.Prepare(definition,()=>false,_=>{});
                character=new OriginalCharacter(definition,sharedAssets);
                yield return character.Build(()=>false,null);
                if(i==sequence.Length-1)
                {
                    if(sharedAssets.RetainedBundles==0)throw new IOException("Reuse fixture did not retain any common bundles.");
                    File.WriteAllText(Path.Combine(output,"reuse.txt"),"A-B-A; retained="+sharedAssets.RetainedBundles+"; loaded="+sharedAssets.LoadedBundles+"; total="+sharedAssets.BundleCount);
                    break;
                }
                var temporary=new GameObject("Reuse environment camera");
                character.UpdateMotion(0,0);
                using(var environment=character.CreateEnvironment(temporary.AddComponent<Camera>())){}
                Destroy(temporary);character.Dispose();character=null;
                yield return null;
            }
        }
        private IEnumerator CaptureEnvironment(string output)
        {
            var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);target.Create();environmentCamera.targetTexture=target;
            environmentCamera.enabled=true;
            for(int i=0;i<180;i++){character.UpdateMotion(1f/30,0);character.PoseFace(1);yield return new WaitForEndOfFrame();}
            CharacterDiagnostics.Write(character.Root,output);
            CharacterDiagnostics.WriteEnvironment(environmentCamera,output);
            string fixturePath=Argument("--fixture");
            var fixture=fixturePath==null?VisualFixture.Create(character.Root):VisualFixture.Load(fixturePath);
            VisualFixture.Save(Path.Combine(output,"fixture.json"),fixture);
            foreach(var view in fixture.Views)
            {
                VisualFixture.Set(environmentCamera,view);
                for(int i=0;i<VisualFixture.WarmFrames;i++){character.UpdateMotion(1f/30,0);character.PoseFace(1);yield return new WaitForEndOfFrame();}
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+".png"));
                if(view.Name=="face")VisualFixture.FaceBounds(environmentCamera,character.Root,Path.Combine(output,"face-region.json"));
                var visible=character.Root.GetComponentsInChildren<Renderer>(true).Where(r=>r.enabled).ToArray();
                foreach(var r in visible)r.enabled=false;
                for(int i=0;i<VisualFixture.WarmFrames;i++)yield return new WaitForEndOfFrame();
                VisualFixture.Pixels(target,Path.Combine(output,view.Name+"-background.png"));
                foreach(var r in visible)r.enabled=true;
            }
            environmentCamera.transform.position=new Vector3(0,15.9255455f,5.721683f);environmentCamera.transform.LookAt(new Vector3(0,15.9255455f,-.2183166f));
            if(Environment.GetCommandLineArgs().Contains("--probe-aa"))
            {
                string probe=Path.Combine(output,"aa-phases");Directory.CreateDirectory(probe);
                VisualFixture.Set(environmentCamera,fixture.Views.First(v=>v.Name=="face"));
                for(int phase=0;phase<16;phase++)
                {
                    environment.ResetHistory(phase);
                    for(int i=0;i<VisualFixture.WarmFrames;i++){character.UpdateMotion(1f/30,0);character.PoseFace(1);yield return new WaitForEndOfFrame();}
                    VisualFixture.Pixels(target,Path.Combine(probe,phase.ToString("D2")+".png"));
                }
            }
            for(int i=0;i<VisualFixture.WarmFrames;i++){character.UpdateMotion(1f/30,0);character.PoseFace(1);yield return new WaitForEndOfFrame();}
            string dynamicDirectory=Path.Combine(output,"dynamic");Directory.CreateDirectory(dynamicDirectory);
            var blinkStates=new System.Collections.Generic.List<string>();
            for(int frame=1;frame<=360;frame++)
            {
                VisualFixture.Set(environmentCamera,fixture.Views[frame<=180?0:3]);
                character.UpdateMotion(1f/30,frame/30f);
                blinkStates.Add(frame+" "+character.BlinkDiagnostic);
                yield return new WaitForEndOfFrame();
                if(frame%15==0){VisualFixture.Pixels(target,Path.Combine(dynamicDirectory,"frame-"+frame.ToString("D3")+".png"));CharacterDiagnostics.WritePose(character.Root,Path.Combine(dynamicDirectory,"pose-"+frame.ToString("D3")+".json"),frame/30f);}
            }
            File.WriteAllText(Path.Combine(dynamicDirectory,"complete.txt"),"360 fixed 1/30 steps; seed 1979; front then face.");
            File.WriteAllLines(Path.Combine(dynamicDirectory,"blink.txt"),blinkStates);
            var previous=RenderTexture.active;var image=new Texture2D(1280,720,TextureFormat.RGBA32,false);
            try{RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();File.WriteAllBytes(Path.Combine(output,"environment-face.png"),image.EncodeToPNG());}
            finally{RenderTexture.active=previous;environmentCamera.enabled=false;environmentCamera.targetTexture=null;target.Release();Destroy(target);Destroy(image);}
        }
        private static void Capture(Camera camera,string path,int frames=1)
        {
            var target = new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
            Texture2D pixels = null;
            var previous = RenderTexture.active;
            try
            {
                target.Create(); camera.targetTexture = target; for(int frame=0;frame<frames;frame++)camera.Render(); RenderTexture.active = target;
                pixels = new Texture2D(1280,720,TextureFormat.RGBA32,false);
                pixels.ReadPixels(new Rect(0,0,1280,720),0,0); pixels.Apply(); File.WriteAllBytes(path,pixels.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; camera.targetTexture = null; target.Release(); Destroy(target); if(pixels != null) Destroy(pixels); }
        }
        private void OnDestroy() { environment?.Dispose();if(character != null) character.Dispose();sharedAssets?.Dispose(); }
        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args,name);
            return index>=0 && index+1<args.Length ? args[index+1] : null;
        }
    }
}
