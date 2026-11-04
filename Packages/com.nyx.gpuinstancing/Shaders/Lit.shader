// Shader éclairé générique pour les instances d'un MeshSpawner (pipeline Built-in, rendu Forward) :
// Standard + setup procédural, variation de teinte par instance (graine). Base pour un shader propre au projet :
// les 3 lignes « instancing » et l'include de InstancingSetup.cginc suffisent à rendre un surface shader compatible.
Shader "Nyx/GPU Instancing/Lit"
{
    Properties
    {
        _Color ("Couleur", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        [NoScaleOffset] _BumpMap ("Normal map", 2D) = "bump" {}
        _Glossiness ("Lisse", Range(0, 1)) = 0.2
        _Metallic ("Metal", Range(0, 1)) = 0
        _ColorVariation ("Variation de teinte par instance", Range(0, 0.5)) = 0.15
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        // noforwardadd : une lumière ponctuelle qui touche les bounds (immenses) du spawner ajouterait une passe sur
        //                toutes les instances. addshadow : la passe d'ombre reprend le setup procédural.
        #pragma surface surf Standard vertex:vert addshadow noforwardadd nolightmap nodynlightmap nodirlightmap nometa exclude_path:deferred exclude_path:prepass
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:NyxInstancingSetup
        #pragma target 4.5

        #include "ShaderLibrary/InstancingSetup.cginc"

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _Glossiness;
        half _Metallic;
        half _ColorVariation;

        struct Input
        {
            float2 uv_MainTex;
            float seed;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.seed = 0.5;
        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            o.seed = NyxGetRenderData().custom.x;
        #endif
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 albedo = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            albedo.rgb *= 1.0 + (IN.seed - 0.5) * 2.0 * _ColorVariation;
            o.Albedo = albedo.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback Off
}
