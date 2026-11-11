using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Drives the user's installed CTAA shaders with their stored settings. No plugin code is loaded.
    public sealed class IndependentTemporalAA : MonoBehaviour
    {
        private Material resolve,enhance;
        private Camera cameraComponent;
        private float[] settings;
        private RenderTexture previous,current,sharpened;
        private bool initialized;
        private int sample;
        public Vector2 ReferenceResolution;
        public bool Bypass {get;set;}
        public System.Action<RenderTexture,RenderTexture,RenderTexture,RenderTexture> DiagnosticTrace;
        private static readonly float[] Horizontal={.5f,-.25f,.75f,-.125f,.625f,.575f,-.875f,.0625f,-.3f,.75f,-.25f,-.625f,.325f,.975f,-.075f,.625f};
        private static readonly float[] Vertical={.33f,-.66f,.51f,.44f,-.77f,.12f,-.55f,.88f,-.83f,.14f,.71f,-.34f,.87f,-.12f,.75f,.08f};
        public void Configure(Shader resolveShader,Shader enhanceShader,float[] parameters,Vector2 referenceResolution)
        {
            resolve=new Material(resolveShader);enhance=new Material(enhanceShader);settings=parameters;
            ReferenceResolution=referenceResolution;
            cameraComponent=GetComponent<Camera>();cameraComponent.depthTextureMode|=DepthTextureMode.Depth|DepthTextureMode.DepthNormals|DepthTextureMode.MotionVectors;
        }
        public void ResetHistory(int phase=0){initialized=false;sample=((phase%16)+16)%16;}
        private void OnPreCull()
        {
            if(settings==null)return;
            cameraComponent.ResetProjectionMatrix();cameraComponent.nonJitteredProjectionMatrix=cameraComponent.projectionMatrix;
            if(Bypass)return;
            var matrix=cameraComponent.projectionMatrix;
            matrix.m02+=(Horizontal[sample]*settings[3]*2-1)/cameraComponent.pixelWidth;
            matrix.m12+=(Vertical[sample]*settings[3]*2-1)/cameraComponent.pixelHeight;
            sample=(sample+1)%16;cameraComponent.projectionMatrix=matrix;
        }
        private void OnRenderImage(RenderTexture source,RenderTexture destination)
        {
            if(resolve==null || Bypass){initialized=false;Graphics.Blit(source,destination);return;}
            if(previous==null||previous.width!=source.width||previous.height!=source.height||previous.format!=source.format)
            {
                Release();previous=Buffer(source);current=Buffer(source);sharpened=Buffer(source);initialized=false;
            }
            if(settings[2]>.01f)
            {
                // CTAA uses the game's window dimensions for enhancement, even when its
                // camera renders into a differently sized target texture.
                enhance.SetFloat("_AEXCTAA",1f/(ReferenceResolution.x>0?ReferenceResolution.x:source.width));
                enhance.SetFloat("_AEYCTAA",1f/(ReferenceResolution.y>0?ReferenceResolution.y:source.height));
                enhance.SetFloat("_AESCTAA",Mathf.Lerp(.2f,2,settings[2]));enhance.SetFloat("_AEMAXCTAA",Mathf.Lerp(.005f,.12f,settings[2]));
                Graphics.Blit(source,sharpened,enhance,1);
            }
            else Graphics.Blit(source,sharpened);
            if(!initialized){Graphics.Blit(sharpened,previous);initialized=true;}
            resolve.SetFloat("_AdaptiveResolve",3000);resolve.SetVector("_ControlParams",new Vector4(settings[5],settings[0],settings[1],settings[4]));resolve.SetTexture("_Accum",previous);
            Graphics.Blit(sharpened,current,resolve);
            if(DiagnosticTrace!=null)DiagnosticTrace(source,sharpened,previous,current);
            Graphics.Blit(current,destination);var swap=previous;previous=current;current=swap;
        }
        private static RenderTexture Buffer(RenderTexture source){var texture=new RenderTexture(source.width,source.height,0,source.format){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};texture.Create();return texture;}
        private void Release(){foreach(var texture in new[]{previous,current,sharpened})if(texture!=null){texture.Release();Destroy(texture);}previous=current=sharpened=null;}
        private void OnDisable(){Release();if(resolve!=null)Destroy(resolve);if(enhance!=null)Destroy(enhance);resolve=enhance=null;if(cameraComponent!=null)cameraComponent.ResetProjectionMatrix();}
    }
}
