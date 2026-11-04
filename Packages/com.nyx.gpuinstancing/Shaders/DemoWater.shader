// Eau de démonstration : surface transparente déplacée par la même houle que les sargasses (OceanWaves.hlsl).
// Sert d'exemple de synchronisation pour le shader d'eau d'un projet : même fonction, même temps, même niveau.
// Mesh attendu : grille plate posée au niveau de l'eau (générée par NyxDemoWater).
Shader "Nyx/GPU Instancing/Demo Water"
{
    Properties
    {
        _Color ("Couleur (alpha = opacité)", Color) = (0.10, 0.40, 0.48, 0.80)
        _GrazingColor ("Couleur rasante", Color) = (0.30, 0.50, 0.58, 0.90)
        _Specular ("Reflet du soleil", Range(0, 2)) = 0.8
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma target 3.5

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "ShaderLibrary/OceanWaves.hlsl"

            fixed4 _Color;
            fixed4 _GrazingColor;
            half _Specular;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 normal : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 normal;
                worldPos += NyxWaveDisplacement(worldPos.xz, _NyxWaveTime, normal);   // la houle des sargasses
                o.worldPos = worldPos;
                o.normal = normal;
                o.pos = UnityWorldToClipPos(worldPos);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 view = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 n = normalize(i.normal);
                n = dot(n, view) < 0 ? -n : n;   // vue de dessous
                float fresnel = pow(1.0 - saturate(dot(n, view)), 3.0);
                fixed4 color = lerp(_Color, _GrazingColor, fresnel);
                float3 light = normalize(_WorldSpaceLightPos0.xyz);
                float diffuse = saturate(dot(n, light));
                float specular = pow(saturate(dot(reflect(-light, n), view)), 64.0) * _Specular;
                color.rgb = color.rgb * (0.45 + 0.55 * diffuse * _LightColor0.rgb) + specular * _LightColor0.rgb;
                UNITY_APPLY_FOG(i.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
