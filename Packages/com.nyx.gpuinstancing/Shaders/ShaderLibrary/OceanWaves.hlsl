#ifndef NYX_OCEAN_WAVES_INCLUDED
#define NYX_OCEAN_WAVES_INCLUDED

// Somme de 4 vagues de Gerstner.
// La MÊME fonction existe côté C# (OceanWaves.SampleDisplacement) et doit être utilisée par le shader
// d'eau : c'est ce qui garantit que les sargasses et les bulles collent exactement à la surface.
// Les variables sont globales (Shader.SetGlobal*), alimentées par le composant OceanWaves.

#define NYX_WAVE_COUNT 4

float  _NyxWaterLevel;              // hauteur Y de l'eau au repos (m)
float  _NyxWaveTime;                // temps des vagues (s)
float4 _NyxWaves[NYX_WAVE_COUNT];   // xy : direction normalisée (plan XZ), z : cambrure 0..1, w : longueur d'onde (m)

// Déplacement (x, y, z) de la particule d'eau dont la position au repos est (xz), et normale de la surface.
// Un objet flottant suit ce déplacement : il décrit les orbites de Gerstner, comme une vraie épave.
float3 NyxWaveDisplacement(float2 xz, float time, out float3 normal)
{
    float3 displacement = 0;
    float3 tangent  = float3(1, 0, 0);
    float3 binormal = float3(0, 0, 1);

    [unroll]
    for (int i = 0; i < NYX_WAVE_COUNT; i++)
    {
        float4 w = _NyxWaves[i];
        float k = 6.28318530718 / w.w;      // nombre d'onde
        float c = sqrt(9.81 / k);           // vitesse de phase en eau profonde
        float f = k * (dot(w.xy, xz) - c * time);
        float a = w.z / k;                  // amplitude
        float s, co;
        sincos(f, s, co);

        displacement += float3(w.x * a * co, a * s, w.y * a * co);
        tangent      += float3(-w.x * w.x * w.z * s, w.x * w.z * co, -w.x * w.y * w.z * s);
        binormal     += float3(-w.x * w.y * w.z * s, w.y * w.z * co, -w.y * w.y * w.z * s);
    }

    normal = normalize(cross(binormal, tangent));
    return displacement;
}

// Hauteur approchée de la surface au-dessus de (xz) : ignore le décalage horizontal de Gerstner.
float NyxWaveHeight(float2 xz, float time)
{
    float3 unusedNormal;
    return _NyxWaterLevel + NyxWaveDisplacement(xz, time, unusedNormal).y;
}

#endif // NYX_OCEAN_WAVES_INCLUDED
