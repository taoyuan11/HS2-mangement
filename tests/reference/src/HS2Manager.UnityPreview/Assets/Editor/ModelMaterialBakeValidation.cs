using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace HS2Manager.UnityPreview.Editor
{
    public static class ModelMaterialBakeValidation
    {
        [Serializable] private sealed class Outcome {public bool Passed;public string UnityVersion,Error;public string[] Checks;}
        public static void Run()
        {
            var report=new Outcome{UnityVersion=Application.unityVersion};
            var checks=new List<string>();var owned=new List<Object>();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite;
            try
            {
                var shader=Shader.Find("Hidden/HS2Manager/ModelExportBake");
                if(shader==null||!shader.isSupported)throw new Exception("Bake shader unavailable");
                var m=new Material(shader);owned.Add(m);
                var defaults=new Material(shader);owned.Add(defaults);
                var target=new RenderTexture(4,4,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);owned.Add(target);target.Create();
                var image=new Texture2D(4,4,TextureFormat.RGBA32,false,true);owned.Add(image);
                Texture2D Texture(Color color)
                {
                    var t=new Texture2D(2,2,TextureFormat.RGBA32,false,true);owned.Add(t);
                    t.SetPixels(new[]{color,color,color,color});t.Apply();return t;
                }
                Color Read(int kind)
                {
                    m.SetFloat("_BakeKind",kind);GL.sRGBWrite=false;
                    Graphics.Blit(m.GetTexture("_MainTex"),target,m);RenderTexture.active=target;
                    image.ReadPixels(new Rect(0,0,4,4),0,0);image.Apply();return image.GetPixel(2,2);
                }
                void Expect(string name,Color actual,Color expected)
                {
                    for(int c=0;c<4;c++)if(Mathf.Abs(actual[c]-expected[c])>.012f)throw new Exception(name+": "+actual+" != "+expected);
                    checks.Add(name);
                }
                Color Gray(float v){return new Color(v,v,v,1);}
                m.SetTexture("_MainTex",Texture(Color.white));m.SetFloat("_BaseMetallic",.2f);m.SetFloat("_BaseSmoothness",.3f);
                m.SetFloat("_HasDetailMainTex",1);m.SetTexture("_DetailMainTex",Texture(new Color(.8f,.1f,.6f,1)));m.SetFloat("_DetailMainScale",1);
                m.SetFloat("_PackedKind",1);
                Expect("Skin metallic uses detail R",Read(2),Gray(.8f));
                Expect("Skin gloss uses detail B",Read(3),Gray(.4f));
                m.SetFloat("_PackedKind",2);m.SetFloat("_HasMetallicGlossMap",1);m.SetTexture("_MetallicGlossMap",Texture(new Color(.5f,0,0,.8f)));
                Expect("Clothes metallic combines packed R",Read(2),Gray(.4f));
                Expect("Clothes roughness combines B and A",Read(3),Gray(.52f));
                m.SetFloat("_PackedKind",0);m.SetFloat("_BaseMetallic",0);m.SetFloat("_BaseSmoothness",.8f);
                Expect("Standard map overrides metallic slider",Read(2),Gray(.5f));
                Expect("Standard detail albedo is not gloss",Read(3),Gray(.36f));
                m.CopyPropertiesFromMaterial(defaults);
                m.SetTexture("_MainTex",Texture(Color.white));m.SetFloat("_MaterialKind",2);m.SetFloat("_OriginalEye",1);
                m.SetColor("_Color",Color.white);m.SetColor("_Color2",Color.green);m.SetColor("_Color3",Color.red);
                m.SetVector("_texture2uv",new Vector4(1,1,0,0));m.SetVector("_texture3uv",new Vector4(1,1,1,0));
                m.SetTexture("_Texture2",Texture(new Color(1,0,1,1)));m.SetTexture("_Texture3",Texture(new Color(1,0,0,0)));
                Expect("Pupil red is not coverage",Read(0),Color.green);
                m.SetTexture("_Texture3",Texture(new Color(0,0,0,1)));
                Expect("Pupil alpha is coverage",Read(0),Color.red);
                m.CopyPropertiesFromMaterial(defaults);
                m.SetTexture("_MainTex",Texture(new Color(0,0,0,.4f)));m.SetFloat("_MaterialKind",1);m.SetFloat("_OriginalHair",1);m.SetFloat("_BakeAlpha",1);
                m.SetColor("_Color",Color.red);m.SetColor("_TopColor",Color.green);m.SetColor("_UnderColor",Color.blue);
                m.SetTexture("_ColorMask",Texture(new Color(0,0,1,0)));m.SetFloat("_HasColorMask",1);
                Expect("Hair gradient uses mask and preserves coverage",Read(0),new Color(0,1,0,.4f));
                m.CopyPropertiesFromMaterial(defaults);
                m.SetFloat("_MaterialKind",5);m.SetFloat("_ShadowScale",.5f);m.SetColor("_Color",Color.red);m.SetTexture("_MainTex",Texture(Color.clear));
                Expect("Eye shadow empty area is neutral multiplier",Read(0),Color.white);
                m.SetTexture("_MainTex",Texture(Color.white));Expect("Eye shadow opaque area is tint",Read(0),Color.red);
                m.SetFloat("_HasOcclusionMap",1);m.SetFloat("_OcclusionStrength",0);m.SetTexture("_OcclusionMap",Texture(Color.black));
                Expect("Disabled AO is neutral",Read(4),Color.white);
                m.CopyPropertiesFromMaterial(defaults);
                m.SetFloat("_OriginalHair",1);m.SetFloat("_HasDetailMask",1);m.SetFloat("_DetailScale",1);
                m.SetTexture("_DetailMask",Texture(new Color(.5f,.75f,1,1)));
                Expect("Hair detail normal works without primary bump",Read(1),new Color(.5f,.75f,.933f,1));
                report.Passed=true;
            }
            catch(Exception error){report.Error=error.ToString();Debug.LogException(error);}
            finally
            {
                GL.sRGBWrite=srgb;RenderTexture.active=previous;
                foreach(var value in owned)Object.DestroyImmediate(value);
                report.Checks=checks.ToArray();
                File.WriteAllText(Environment.GetEnvironmentVariable("HS2_BAKE_VALIDATION_REPORT"),JsonUtility.ToJson(report,true));
            }
            EditorApplication.Exit(report.Passed?0:1);
        }
    }
}
