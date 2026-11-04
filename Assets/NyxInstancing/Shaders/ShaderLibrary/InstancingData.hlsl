#ifndef NYX_INSTANCING_DATA_INCLUDED
#define NYX_INSTANCING_DATA_INCLUDED

// Structures partagées entre le C# (GpuInstanceData.cs), le culling (compute) et les shaders de rendu.
// Toutes font 64 octets : lecture alignée sur 16 octets, une seule ligne de cache par instance.

// Donnée d'instance "source", écrite par le C#.
// Chaque système (sargasses, bulles...) interprète row0..row2 et custom.xyz à sa façon.
// Seul custom.w est commun : l'index de variante.
struct NyxInstanceData
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 custom;
};

// Donnée de rendu compactée, écrite par le culling pour chaque instance VISIBLE :
// matrice objet -> monde 3x4 (3 lignes) + 4 paramètres libres pour le shader.
struct NyxRenderData
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 custom;
};

// Description d'une variante de mesh.
struct NyxVariantInfo
{
    float4 boundsCenter;    // xyz : centre des bounds locales (union des LODs)
    float4 boundsExtents;   // xyz : demi-tailles + marge de déformation
    float4 lodDistancesSq;  // x, y, z : distance² de début des LOD1..3 ; w : distance² de disparition
    uint   firstBucket;     // index du draw du LOD0 de cette variante
    uint   lodCount;        // 1 à 4
    uint   pad0;
    uint   pad1;
};

#endif // NYX_INSTANCING_DATA_INCLUDED
