// Native version of this repository's existing PBR export equations.
// No engine includes, runtime, or copied SDK shader implementation.
__BINDINGS__
struct BakeVertex { float4 position:SV_Position; float2 uv:TEXCOORD0; };
BakeVertex VS(uint id:SV_VertexID) { BakeVertex o;o.uv=float2((id<<1)&2,id&2);o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o; }
float3 UnpackNormal(float4 p) { p.x*=p.w;float2 xy=p.xy*2-1;return float3(xy,sqrt(1-saturate(dot(xy,xy)))); }
#define SAMPLE(texture,uv) texture.Sample(texture##Sampler,uv)
   float2 layout(float2 uv,float4 st,float angle)
   {
    float2 p=uv-.5;float s=sin(angle*3.14159265358979323846),c=cos(angle*3.14159265358979323846);
    return float2(p.x*c-p.y*s,p.x*s+p.y*c)*st.xy+.5+st.zw;
   }
   float inside(float2 uv){return step(0,uv.x)*step(0,uv.y)*step(uv.x,1)*step(uv.y,1);}
   float4 baseColor(float2 uv)
   {
    float4 main=SAMPLE(_MainTex,uv*_MainTex_ST.xy+_MainTex_ST.zw);
    float4 color=main*_Color;
    if(_MaterialKind==1)
    {
     float4 mask=SAMPLE(_ColorMask,uv*_ColorMask_ST.xy+_ColorMask_ST.zw);
     if(_OriginalHair>0)
     {
      // hair_07/08 use the B/G channels of two masks, not vertical UV gradients.
      float2 b=pow(saturate(SAMPLE(_ColorMask_B,uv*_ColorMask_B_ST.xy+_ColorMask_B_ST.zw).bg),max(float2(_TopGradation,_UnderGradation),.001));
      float3 tint=saturate(lerp(_Color.rgb,lerp(lerp(_Color.rgb,_TopColor.rgb,b.x),_UnderColor.rgb,b.y),(b.x+b.y)*_ColorMask_A0B1*_HasColorMask_B));
      float3 shade=lerp(tint*.745283,tint+float3(.084,.049,.049),main.b);
      shade=lerp(shade,tint+float3(.070588,.066353,.066353),main.g);
      float2 a=pow(saturate(mask.bg),max(float2(_TopGradation,_UnderGradation),.001));
      shade=lerp(shade,lerp(lerp(tint,_TopColor.rgb,a.x),_UnderColor.rgb,a.y),(a.x+a.y)*(1-_ColorMask_A0B1)*_HasColorMask);
      color=float4(saturate(shade),main.a);
     }
     else
     {
      float3 tint=lerp(lerp(_UnderColor.rgb,_Color.rgb,saturate(uv.y*2)),_TopColor.rgb,saturate(uv.y*2-1));
      color=float4(tint*(.75+.25*main.b),main.a*_Color.a);
      if(_HasMeshColorMask_B>0)color.rgb=lerp(color.rgb,_MeshColor_01.rgb,SAMPLE(_MeshColorMask_B,uv*_MeshColorMask_B_ST.xy+_MeshColorMask_B_ST.zw).r);
      if(_HasColorMask>0)color.a*=mask.a;
     }
    }
    else if(_MaterialKind==2)
    {
     if(_OriginalEye>0)
     {
      // AIT iris offset includes a 0.06 vertical correction. Pupil z is a
      // zoom multiplier, and pupil coverage is stored in alpha, not red.
      float2 offset=_texture2uv.zw+_eyebaseoffset.xy;
      float2 irisUV=(uv-.5)*_texture2uv.xy+.5+offset+float2(0,(_texture2uv.y-1)*.06);
      float4 iris=SAMPLE(_Texture2,irisUV);
      float2 pupilUV=(uv-.5)*_texture2uv.xy*_texture3uv.xy*_texture3uv.z+.5+offset*_texture3uv.z;
      float4 pupil=SAMPLE(_Texture3,pupilUV);
      color=float4(lerp(main.rgb*_Color.rgb,iris.r*_Color2.rgb,saturate(iris.b*_Color2.a)*inside(irisUV)),1);
      color.rgb=lerp(color.rgb,_Color3.rgb,saturate(pupil.a*_Color3.a)*inside(pupilUV));
      float2 p=uv+_Texture4UV.zw-.5;float s=sin(_Texture4Rotator*3.14159265358979323846),c=cos(_Texture4Rotator*3.14159265358979323846);
      float2 hlUV=float2(p.x*c+p.y*s,-p.x*s+p.y*c)*_Texture4UV.xy+.5;
      float highlight=SAMPLE(_Texture4,hlUV).r*inside(hlUV)*_Color4.a*_BaseSmoothness*_HasTexture4;
      color.rgb=lerp(color.rgb,_Color4.rgb,saturate(highlight));
     }
     else
     {
     float2 irisUV=layout(uv,_texture2uv,0);float4 iris=SAMPLE(_Texture2,irisUV);
     float2 pupilUV=layout(irisUV,_texture3uv,0);float4 pupil=SAMPLE(_Texture3,pupilUV);
     float3 tint=lerp(_Color2.rgb*(.2+.9*iris.r),_Color3.rgb,pupil.r*inside(pupilUV));
     color=float4(lerp(_Color.rgb,tint,iris.b*inside(irisUV)),1);
     float2 hlUV=layout(uv,_Texture4UV,_Texture4Rotator);float highlight=SAMPLE(_Texture4,hlUV).r*inside(hlUV)*_Color4.a;
     color.rgb=lerp(color.rgb,_Color4.rgb,highlight*_HasTexture4);
     }
    }
    else if(_MaterialKind==3)
    {
     // Skin color, makeup and overlays have already been composed by the original creators.
     color=main;
     if(_HasTexture3>0)
     {
      float side=uv.x<.5?1:-1;
      float2 brow=uv-float2(.5-side*(.145+_Texture3UV.x),.445+_Texture3UV.y);brow.x*=side;
      float s=sin(_Texture3Rotator*3.14159265358979323846),c=cos(_Texture3Rotator*3.14159265358979323846);
      brow=float2(brow.x*c-brow.y*s,brow.x*s+brow.y*c)*4*_Texture3UV.zw+float2(.5,.56);
      color.rgb=lerp(color.rgb,_Color3.rgb,SAMPLE(_Texture3,brow).b*inside(brow)*_Color3.a);
     }
    }
    else if(_MaterialKind==4)color=float4(_Color.rgb,main.r*_Color.a);
    else if(_MaterialKind==5)color=float4(lerp(float3(1,1,1),_Color.rgb,saturate(pow(saturate(main.a),max(_ShadowScale,.001))*_Color.a)),1);
    else if(_MaterialKind==6)
    {
     float exponent=8-7*saturate(_NamidaScale);
     color=float4(1,1,1,saturate(pow(saturate(main.g),exponent)*exponent));
    }
    else if(_HasColorMask>0)
    {
     float3 mask=SAMPLE(_ColorMask,uv*_ColorMask_ST.xy+_ColorMask_ST.zw).rgb;
     float3 tint=lerp(lerp(lerp(_Color.rgb,_Color2.rgb,mask.r),_Color3.rgb,mask.g),_Color4.rgb,mask.b);
     color.rgb=main.rgb*tint;
    }
    if(_IgnoreMainAlpha>0)color.a=1;
    if(_HasAlphaMask>0)
    {
     float3 mask=SAMPLE(_AlphaMask,uv).rgb;
     color.a*=lerp(1,mask.r,saturate(_alpha_a))*lerp(1,mask.g,saturate(_alpha_b))*lerp(1,mask.b,saturate(_alpha_c));
    }
    if(_HasAlphaMask2>0)color.a*=lerp(1,SAMPLE(_AlphaMask2,uv).r,saturate(_alpha_d));
    if(_BakeAlpha==0)color.a=1;
    return color;
   }
   float4 frag(BakeVertex input):SV_Target
   {
    float2 uv=input.uv;
    if(_BakeKind==6){float mask=SAMPLE(_MeshColorMask_B,uv*_MeshColorMask_B_ST.xy+_MeshColorMask_B_ST.zw).r;return float4(mask,mask,mask,1);}
    if(_BakeKind==0)return baseColor(uv);
    if(_BakeKind==1)
    {
     float3 n=float3(0,0,1);
     if(_HasBumpMap>0){n=UnpackNormal(SAMPLE(_BumpMap,uv*_BumpMap_ST.xy+_BumpMap_ST.zw));n.xy*=_BumpScale;n=normalize(n);}
     if(_OriginalHair>0&&_HasDetailMask>0){float3 d=UnpackNormal(SAMPLE(_DetailMask,uv*_DetailMask_ST.xy*16+_DetailMask_ST.zw));n=normalize(lerp(n,d,saturate(_DetailScale)));}
     if(_HasBumpMap2>0){float3 d=UnpackNormal(SAMPLE(_BumpMap2,uv*_BumpMap2_ST.xy+_BumpMap2_ST.zw));d.xy*=_BumpScale2;n=normalize(float3(n.xy+d.xy,n.z*d.z));}
     return float4(n*.5+.5,1);
    }
    if(_BakeKind==2||_BakeKind==3)
    {
     float metal=_BaseMetallic,smooth=_BaseSmoothness;
     float4 packed=SAMPLE(_MetallicGlossMap,uv*_MetallicGlossMap_ST.xy+_MetallicGlossMap_ST.zw);
     float4 detail=SAMPLE(_DetailMainTex,uv*_DetailMainTex_ST.xy+_DetailMainTex_ST.zw);
     if(_PackedKind==1&&_HasDetailMainTex>0){metal=max(metal,detail.r*_DetailMainScale);smooth=max(smooth,detail.b*_DetailMainScale);}
     else if(_PackedKind==2&&_HasDetailMainTex>0){metal=detail.r*packed.r;smooth=detail.b*packed.a;}
     else
     {
      if(_HasMetallicGlossMap>0){metal=packed.r;smooth*=packed.a;}
      // Unity Standard's Detail Albedo is color data, not a metallic/gloss map.
     }
     metal=saturate(metal);smooth=saturate(smooth);
     float value=_BakeKind==2?metal:1-smooth;return float4(value,value,value,1);
    }
    if(_BakeKind==4){float ao=_HasOcclusionMap>0?SAMPLE(_OcclusionMap,uv*_OcclusionMap_ST.xy+_OcclusionMap_ST.zw).g:1;ao=lerp(1,ao,saturate(_OcclusionStrength));return float4(ao,ao,ao,1);}
    float3 emission=_EmissionColor.rgb*_EmissionGain;
    if(_HasEmissionMap>0)emission*=SAMPLE(_EmissionMap,uv*_EmissionMap_ST.xy+_EmissionMap_ST.zw).rgb;
    if(_MaterialKind==2)emission+=baseColor(uv).rgb*_Emission;
    return float4(emission,1);
   }
