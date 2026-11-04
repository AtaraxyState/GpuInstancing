// Bulles instanciées (pipeline Built-in) : transparentes, bord argenté (réflexion totale interne),
// reflet du soleil, brouillard sous-marin. À dessiner via un MeshSpawner + Bubble Columns (DrawMeshInstancedIndirect).
// Compatible VR Single Pass Instanced (macros stéréo) et multiview.
Shader "Nyx/GPU Instancing/Bubble"
{
    Properties
    {
        _RimColor ("Couleur du bord", Color) = (0.80, 0.92, 1.0, 1)
        _RimPower ("Finesse du bord", Range(0.5, 8)) = 2.5
        _RimIntensity ("Intensite du bord", Range(0, 4)) = 1.2
        _CoreOpacity ("Opacite du centre", Range(0, 1)) = 0.04
        _GlintPower ("Nettete du reflet soleil", Range(8, 1024)) = 256
        _GlintIntensity ("Intensite du reflet soleil", Range(0, 8)) = 2
        _ReflectionStrength ("Reflexion de l'environnement", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend One OneMinusSrcAlpha   // alpha prémultiplié : l'ordre (non trié) des instances se voit peu
            ZWrite Off
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:NyxInstancingSetup
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "ShaderLibrary/InstancingSetup.cginc"

            half4 _RimColor;
            half _RimPower;
            half _RimIntensity;
            half _CoreOpacity;
            half _GlintPower;
            half _GlintIntensity;
            half _ReflectionStrength;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float3 normalWS  : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
                half   fade      : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);                 // -> NyxInstancingSetup() + œil courant
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 positionWS = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
                o.pos = UnityWorldToClipPos(positionWS);
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                o.viewDirWS = _WorldSpaceCameraPos - positionWS;
            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                o.fade = NyxGetRenderData().custom.w;
            #else
                o.fade = 1;
            #endif
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 n = normalize(i.normalWS);
                float3 viewDir = normalize(i.viewDirWS);
                half rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                // Bord argenté : sous l'eau, la surface de la bulle réfléchit totalement aux angles rasants
                half3 environment = DecodeHDR(UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, reflect(-viewDir, n)), unity_SpecCube0_HDR);

                // Reflet du soleil (lumière directionnelle principale)
                float3 halfDir = normalize(_WorldSpaceLightPos0.xyz + viewDir);
                half glint = pow(saturate(dot(n, halfDir)), _GlintPower) * _GlintIntensity;

                // Le reflet est purement additif (absent de l'alpha) : sans soleil, pas de tache sombre sur la bulle
                half3 color = rim * (_RimColor.rgb * _RimIntensity + environment * _ReflectionStrength) + glint * _LightColor0.rgb;
                half alpha = saturate(_CoreOpacity + rim * _RimColor.a);

                color *= i.fade;
                alpha *= i.fade;
                UNITY_APPLY_FOG_COLOR(i.fogCoord, color, unity_FogColor * alpha);   // brouillard en alpha prémultiplié
                return half4(color, alpha);
            }
            ENDCG
        }
    }

    FallBack Off
}
