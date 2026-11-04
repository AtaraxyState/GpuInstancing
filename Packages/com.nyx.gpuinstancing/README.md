# Nyx GPU Instancing

Des milliers de copies de meshes (sargasses flottantes, bulles sous-marines, rochers...) avec un nombre de draw calls qui ne dépend pas du nombre d'instances : culling GPU par caméra (compute shader) et `Graphics.DrawMeshInstancedIndirect`. Même technique que `Assets/NyxInstancing/`, emballée pour être installée dans n'importe quel projet.

- Unity 2021.3 et plus, pipeline **Built-in**, PC et VR (Single Pass Instanced, multiview).
- Un composant **`Mesh Spawner`** + un **comportement** au choix, qui décide comment les instances bougent. Trois sont fournis, et en écrire un ne demande qu'une classe courte et un compute shader.
- Prêt à l'emploi : meshes, matériaux et compute shaders par défaut sont fournis et assignés automatiquement.

## Installation

Au choix, dans `Packages/manifest.json` du projet :

- depuis GitHub (dépôt privé : git doit y avoir accès sur la machine ; `#<commit ou tag>` à la fin pour figer une version) :
  `"com.nyx.gpuinstancing": "https://github.com/AtaraxyState/GpuInstancing.git?path=/Packages/com.nyx.gpuinstancing"` ;
- depuis un dossier local :
  `"com.nyx.gpuinstancing": "file:C:/chemin/vers/com.nyx.gpuinstancing"`.

Ou **copier** le dossier `com.nyx.gpuinstancing` dans `Packages/` du projet (package intégré, modifiable).

Vérifier l'installation : **Tools > Nyx > GPU Instancing > Run Smoke Test** (rapport et captures dans `Logs/NyxGpuInstancingTest/`).

## Démarrage en 1 minute

- **Tools > Nyx > GPU Instancing > Create Demo Scene** : mer qui ondule, couche de sargasses avec amas au-dessus d'une mine et d'un leurre, colonnes de bulles, plongeur qui expire, rochers sur le fond. Caméra libre en Play mode : clic droit + souris, ZQSD (ou WASD), A/E pour descendre/monter, Maj, molette.
- Dans une scène existante, menu **GameObject > Nyx > GPU Instancing** :

| Menu | Crée |
|---|---|
| Sargassum (Float On Waves) | Un Mesh Spawner de sargasses qui flottent sur la houle (2 touffes × 2 LODs). |
| Bubbles (Bubble Columns) | Un Mesh Spawner de bulles avec une colonne, placé sous la surface. |
| Static Meshes (Static Instances) | Un Mesh Spawner de rochers immobiles (à remplacer par vos meshes). |
| Ocean Waves | La houle (une seule par scène). Ajoutée automatiquement avec les sargasses et les bulles si elle manque. |
| Demo Water | Une surface d'eau simple qui suit la houle (exemple de synchronisation). |

## Mesh Spawner et comportements

**`Mesh Spawner`** porte tout ce qui est commun :

- **Variants** : pour chaque variante, 1 à 4 meshes (LOD0 en premier), les distances de LOD et un matériau compatible (shader `Nyx/GPU Instancing/*` ou adapté, **Enable GPU Instancing** coché) ;
- **Placement** : zone (taille, densité, graine, échelle), amas autour de **Cluster Anchors** (mines **et leurres**), ou liste (`SetPlacements`, menu contextuel « Figer le placement ») ;
- rendu, culling GPU, LODs, VR, ombres.

Le **comportement** est un second composant sur le même objet. Dans l'inspecteur du Mesh Spawner, la liste **Comportement** propose tous les comportements du projet, y compris ceux que vous écrivez. Changer de comportement garde les meshes et le placement ; le bouton **Meshes par défaut du comportement** remplace les meshes par ceux fournis avec lui. Ajouter un comportement par *Add Component* ajoute aussi le Mesh Spawner et ses meshes par défaut.

| Comportement | Mouvement | Matériau | Placement |
|---|---|---|---|
| `Float On Waves` | Flotte sur la houle : hauteur et inclinaison des vagues, dandinement, dérive, **Layer Thickness** (épaisseur de la couche). | `Sargassum` | Celui du spawner (la hauteur vient de l'eau). |
| `Bubble Columns` | Monte en spirale, grossit (loi de Boyle), éclate en surface. Colonnes en boucle (coût CPU nul) et `Emit(position, nombre, dispersion)`. | `Bubble` | Propre : émetteurs du comportement. |
| `Static Instances` | Immobile. | `Lit` | Celui du spawner, à sa hauteur. |

Autres composants : `Bubble Burst Emitter` (exemple : expirations périodiques d'un plongeur), `Ocean Waves` (niveau de l'eau et 4 vagues de Gerstner, partagés par le GPU, les shaders et le gameplay), `Nyx Demo Water` et `Nyx Fly Camera` (démo).

### Écrire un comportement

Deux fichiers. Le plus simple à copier est `Static Instances` (`Runtime/StaticInstances.cs` et `Shaders/Resources/NyxGpuInstancing/StaticInstances.compute`).

1. Un compute shader dans un dossier `Resources` du projet, par exemple `Assets/MonJeu/Resources/Bobbing.compute`. Il calcule la matrice finale de chaque instance, puis inclut le culling :

```hlsl
#pragma kernel NyxCull
#pragma kernel NyxWriteArgs
#include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/InstancingData.hlsl"

float _NyxTime;       // temps (s), posé par le Mesh Spawner
float4 _Bobbing;      // x : amplitude (m), y : vitesse, posé par SetShaderParameters

bool NyxEvaluateInstance(uint index, NyxInstanceData src, out NyxRenderData dst)
{
    dst.row0 = src.row0;                 // matrice 3x4 écrite par CreateInstance (position, lacet, échelle)
    dst.row1 = src.row1;
    dst.row2 = src.row2;
    dst.row1.w += sin(_NyxTime * _Bobbing.y + src.custom.x * 6.2832) * _Bobbing.x;   // monte et descend
    dst.custom = src.custom;             // x : graine, w : variante (ne pas modifier)
    return true;                         // false = instance ignorée
}

#include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/InstancingCulling.hlsl"
```

2. Une classe qui hérite de `MeshSpawnerBehavior` :

```csharp
using Nyx.GpuInstancing;
using UnityEngine;

public sealed class Bobbing : MeshSpawnerBehavior
{
    [SerializeField] float amplitude = 0.3f;
    [SerializeField] float speed = 1f;

    protected override string CullingShaderResource => "Bobbing";   // chemin dans Resources, sans extension

    public override void SetShaderParameters(ComputeShader shader, Camera camera) =>
        shader.SetVector("_Bobbing", new Vector4(amplitude, speed, 0f, 0f));

    // Le culling du spawner entier doit couvrir le mouvement
    public override Bounds GetPlacementBounds(Bounds positions, float meshRadius)
    {
        positions.Expand(2f * (meshRadius + amplitude));
        return positions;
    }
}
```

`Bobbing` apparaît alors dans la liste **Comportement** de tous les Mesh Spawners.

Méthodes facultatives de `MeshSpawnerBehavior` :

| Méthode | Rôle | Par défaut |
|---|---|---|
| `SetShaderParameters(shader, camera)` | Paramètres du compute shader, avant chaque dispatch. | rien |
| `CreateInstance(placement)` | Donnée GPU d'une instance placée (64 octets : `row0..row2`, `custom`). Y écrire `custom.y/z` ou une autre interprétation des lignes. | matrice position + lacet + échelle, graine en `custom.x` |
| `GetPlacementBounds(positions, meshRadius)` | Boîte monde de toutes les positions possibles (culling du spawner entier). | positions + rayon des meshes |
| `UsesPlacement` + `BuildInstances(output)` | `false` : le comportement fournit ses propres instances (comme `Bubble Columns`). | `true` |
| `GetBoundsPadding(variant, localBounds, default)` | Marge si le vertex shader déforme les meshes. | réglage du spawner |
| `CreateDefaultVariants()` | Meshes et matériau donnés au spawner à l'ajout (éditeur). | aucun |

Côté rendu, le shader lit `NyxGetRenderData().custom` (valeurs écrites par le compute shader). `Shaders/Lit.shader` est un modèle de surface shader compatible : trois lignes `#pragma` et l'include de `InstancingSetup.cginc` suffisent.

### Côté gameplay

```csharp
// Une mine ou une bouée qui flotte exactement comme les algues
Vector3 rest = new Vector3(x, OceanWaves.WaterLevel, z);
transform.position = rest + OceanWaves.SampleDisplacement(new Vector2(x, z), out Vector3 normal);
transform.up = normal;

bubbles.GetComponent<BubbleColumns>().Emit(regulator.position, 14, 0.03f);   // expiration d'un plongeur
foreach (MeshPlacement p in spawner.Placements) { /* positions au repos des instances */ }
```

### Synchroniser le shader d'eau du projet

`Float On Waves` suit `NyxWaveDisplacement` (Gerstner). Pour que les instances collent à l'eau, le shader d'eau applique la même fonction (voir `Shaders/DemoWater.shader`) :

```hlsl
#include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/OceanWaves.hlsl"

float3 positionWS = mul(unity_ObjectToWorld, v.vertex).xyz;   // mesh plat posé au niveau de l'eau
float3 normalWS;
positionWS += NyxWaveDisplacement(positionWS.xz, _NyxWaveTime, normalWS);
```

Avec une eau d'asset (Crest, KWS...) : régler `Ocean Waves` pour imiter sa houle, ou remplacer le corps de `NyxWaveDisplacement` par l'échantillonnage de l'asset.

## Fonctionnement

1. Les instances sont envoyées **une fois** au GPU (64 octets chacune).
2. Chaque frame, pour chaque caméra, le compute shader du comportement calcule la position finale de chaque instance ; le culling commun rejette ce qui est hors champ (les deux yeux en VR), trop loin ou plus petit qu'un pixel, choisit le LOD et range les instances visibles.
3. Un `DrawMeshInstancedIndirect` par (variante × LOD), pour 100 ou 1 000 000 d'instances.

Mesures (test de fumée, RTX 4070, éditeur 2021.3) : 460 touffes en 4 draws, 692 bulles en 3 draws, 196 rochers en 2 draws ; rendu complet de la vue de surface avec l'eau ≈ 0,4 ms CPU.

## VR

- Culling sur l'union des frustums des deux yeux.
- **Single Pass Instanced** (PC VR) : nombre d'instances doublé automatiquement. Si les instances n'apparaissent que dans un œil : `Stereo Instancing = ForceSinglePassInstanced` ; si elles clignotent ou sont en double : `ForceOff`.
- **Multiview** (PICO, Quest) : pas de doublement, détecté automatiquement.
- Ombres : draw `ShadowsOnly` séparé, automatique.

## Réglages recommandés

- `Max Draw Distance` : sous l'eau, inutile de dépasser la visibilité du brouillard (20 à 40 m pour les bulles).
- `Min Screen Size` : 0,001 (≈ 1 px) ; 0,002 à 0,003 sur casque autonome.
- Ombres : `Off` par défaut. Si `On` pour des sargasses, `Shadow Culling Extrusion` ≈ profondeur visible sous la surface.
- Rendu **Forward**. Chaque caméra (vue Scene, réflexion d'eau) a ses propres buffers.
- Matériaux : **Enable GPU Instancing** coché, sinon le build retire les variantes instanciées et rien ne s'affiche (le spawner l'affiche en avertissement).

## Limites

- Shaders Built-in uniquement (porter les shaders de rendu pour URP ; le C# et les compute shaders ne changent pas).
- Un seul comportement par Mesh Spawner (le premier activé).
- Bulles transparentes non triées entre elles (alpha prémultiplié : quasi invisible).
- Changement de LOD sans fondu.
- Rendu seulement, pas de collisions : le gameplay utilise `Placements`, `FloatOnWaves.GetLayerDepth` et `OceanWaves.SampleDisplacement`.
- 64 draws maximum (variantes × LODs) par spawner, 4 LODs par variante.

## Contenu

```
Runtime/   MeshSpawner (buffers, placement, culling par caméra, draws, VR), MeshSpawnerBehavior,
           FloatOnWaves, BubbleColumns, StaticInstances, BubbleBurstEmitter, OceanWaves,
           NyxDefaultAssets (éditeur), NyxDemoWater, NyxFlyCamera
Shaders/   Sargassum, Bubble, Lit, DemoWater, ShaderLibrary/ (données, setup, culling, vagues),
           Resources/NyxGpuInstancing/ (un compute shader par comportement, chargé automatiquement)
Editor/    inspecteur du Mesh Spawner, menus de création, scène de démo, test de fumée
Defaults/  touffes, bulles, rochers, texture de fronde, matériaux
```

Noms propres au package (namespace `Nyx.GpuInstancing`, shaders `Nyx/GPU Instancing/*`, menus `Nyx/GPU Instancing`) : il cohabite avec la version d'origine `Assets/NyxInstancing/` sans conflit.
