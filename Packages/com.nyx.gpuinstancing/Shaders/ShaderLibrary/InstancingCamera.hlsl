#ifndef NYX_INSTANCING_CAMERA_INCLUDED
#define NYX_INSTANCING_CAMERA_INCLUDED

// Caméra du culling, posée par CameraCulling.cs à chaque rendu : plans du frustum (deux yeux en stéréo), distance max, LOD,
// taille mini à l'écran, extrusion le long de la lumière des ombres, multiplicateur VR. Le même test de boîte rejette sur
// CPU le spawner entier (CameraCulling.Sees).

uint   _NyxInstanceMultiplier;  // 2 en VR Single Pass Instanced, sinon 1
uint   _NyxPlaneSetCount;       // 1 = mono, 2 = stéréo (union des frustums des deux yeux)
float4 _NyxFrustumPlanes[12];   // normales tournées vers l'intérieur
float4 _NyxCameraPosition;      // xyz : position caméra, w : distance max²
float4 _NyxCullParams;          // x : 1 / lodBias², y : (taille écran mini / échelle de projection)²
float4 _NyxShadowSweep;         // xyz : demi-vecteur d'extrusion le long de la lumière (0 sans ombres)

bool NyxAabbInsidePlanes(float3 center, float3 extents, uint firstPlane)
{
    [unroll]
    for (uint p = 0; p < 6; p++)
    {
        float4 plane = _NyxFrustumPlanes[firstPlane + p];
        if (dot(plane.xyz, center) + plane.w < -dot(abs(plane.xyz), extents))
            return false;
    }
    return true;
}

// Boîte monde dans le frustum d'un des yeux. Avec ombres, la boîte est étirée le long de la lumière : les instances hors
// champ dont l'ombre est visible restent dessinées.
bool NyxCameraSeesBox(float3 center, float3 extents)
{
    float3 cullCenter  = center + _NyxShadowSweep.xyz;
    float3 cullExtents = extents + abs(_NyxShadowSweep.xyz);
    bool visible = NyxAabbInsidePlanes(cullCenter, cullExtents, 0);
    if (!visible && _NyxPlaneSetCount > 1)
        visible = NyxAabbInsidePlanes(cullCenter, cullExtents, 6);
    return visible;
}

#endif // NYX_INSTANCING_CAMERA_INCLUDED
