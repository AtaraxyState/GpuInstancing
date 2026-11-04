// Sargasses instanciées (pipeline Built-in) : alpha-test, double face, ondulation des frondes,
// teinte variable par instance, contre-jour. À dessiner via SargassumField (DrawMeshInstancedIndirect).
Shader "Nyx/Instancing/Sargassum"
{
    Properties
    {
        _MainTex ("Albedo (RGB) + Alpha (A)", 2D) = "white" {}
        [NoScaleOffset] _BumpMap ("Normal map", 2D) = "bump" {}
        _ColorA ("Teinte A", Color) = (0.72, 0.55, 0.22, 1)
        _ColorB ("Teinte B", Color) = (0.50, 0.38, 0.14, 1)
        _Cutoff ("Seuil alpha", Range(0, 1)) = 0.4
        _Glossiness ("Lisse (aspect mouille)", Range(0, 1)) = 0.35
        _TranslucencyColor ("Contre-jour : couleur", Color) = (0.55, 0.45, 0.10, 1)
        _TranslucencyPower ("Contre-jour : concentration", Range(1, 16)) = 4

        [Header(Ondulation des frondes)]
        _SwayAmplitude ("Amplitude (m)", Float) = 0.04
        _SwayFrequency ("Frequence spatiale (1 par m)", Float) = 2.0
        _SwaySpeed ("Vitesse", Float) = 1.5
        _SwayRadius ("Rayon (m) d'ondulation maximale", Float) = 0.6
        [ToggleUI] _SwayUseVertexColor ("Masque = couleur de vertex R", Float) = 0
        [Toggle(_NYX_CONFORM_WAVES)] _ConformWaves ("Epouser les vagues par vertex (mettre Tilt Follow a 0)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        LOD 200
        Cull Off    // visibles depuis le bateau ET depuis le dessous (vue sous-marine)

        CGPROGRAM
        // noforwardadd : sans lui, chaque lumière ponctuelle qui touche les bounds (immenses) du champ
        //                ajoute une passe ForwardAdd sur TOUTES les instances.
        // addshadow    : la passe d'ombre reprend le setup procédural et l'ondulation.
        // exclude_path : rendu Forward uniquement (standard en VR) ; le contre-jour lit le soleil de la passe ForwardBase.
        #pragma surface surf Standard vertex:vert addshadow noforwardadd nolightmap nodynlightmap nodirlightmap nometa alphatest:_Cutoff exclude_path:deferred exclude_path:prepass
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:NyxInstancingSetup
        #pragma shader_feature_local _NYX_CONFORM_WAVES
        #pragma target 4.5

        #include "ShaderLibrary/InstancingSetup.cginc"
        #include "ShaderLibrary/OceanWaves.hlsl"

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _ColorA;
        fixed4 _ColorB;
        fixed4 _TranslucencyColor;
        half _Glossiness;
        half _TranslucencyPower;
        float _SwayAmplitude;
        float _SwayFrequency;
        float _SwaySpeed;
        float _SwayRadius;
        float _SwayUseVertexColor;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
            fixed facing : VFACE;
        };

        void vert(inout appdata_full v)
        {
            // Ici unity_ObjectToWorld / unity_WorldToObject sont déjà ceux de l'instance (NyxInstancingSetup).
            float seed = 0;
        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            seed = NyxGetRenderData().custom.x;
        #endif

            float3 positionWS = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
            float mask = _SwayUseVertexColor > 0.5
                ? v.color.r
                : saturate(length(v.vertex.xz) / max(_SwayRadius, 1e-3));   // le centre de la touffe bouge peu

            float phase = dot(positionWS.xz, float2(0.8, 0.6)) * _SwayFrequency - _Time.y * _SwaySpeed + seed * 6.2831853;
            float3 offsetWS = float3(0, (sin(phase) + 0.4 * sin(phase * 2.3 + 1.7)) * _SwayAmplitude * mask, 0);

        #if defined(_NYX_CONFORM_WAVES)
            // Le tapis épouse la houle : écart de hauteur d'eau entre ce sommet et le pivot de la touffe
            float3 pivotWS = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
            float3 unusedNormal;
            offsetWS.y += NyxWaveDisplacement(positionWS.xz, _NyxWaveTime, unusedNormal).y
                        - NyxWaveDisplacement(pivotWS.xz, _NyxWaveTime, unusedNormal).y;
        #endif

            v.vertex.xyz += mul((float3x3)unity_WorldToObject, offsetWS);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float seed = 0;
        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            seed = NyxGetRenderData().custom.x;     // même valeur pour tous les pixels de l'instance (lecture en cache)
        #endif

            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            fixed3 albedo = tex.rgb * lerp(_ColorA.rgb, _ColorB.rgb, seed);

            // Face arrière (vue depuis le dessous) : normale retournée
            float3 normalTS = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            normalTS.z *= IN.facing > 0 ? 1.0 : -1.0;

            // Contre-jour : la lumière traverse les frondes quand on regarde vers le soleil.
            // Pondéré par la couleur/intensité du soleil : rien la nuit ou soleil coupé.
            float3 viewDirWS = normalize(_WorldSpaceCameraPos - IN.worldPos);
            half backLight = pow(saturate(dot(viewDirWS, -_WorldSpaceLightPos0.xyz)), _TranslucencyPower);

            o.Albedo = albedo;
            o.Normal = normalTS;
            o.Emission = albedo * _TranslucencyColor.rgb * _LightColor0.rgb * backLight;
            o.Metallic = 0;
            o.Smoothness = _Glossiness;
            o.Alpha = tex.a;
        }
        ENDCG
    }

    FallBack Off
}
