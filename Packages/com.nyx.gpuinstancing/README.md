# Nyx GPU Instancing

Sargasses flottantes et bulles sous-marines par milliers, avec un nombre de draw calls qui ne dépend pas du nombre d'instances : culling GPU par caméra (compute shader) et `Graphics.DrawMeshInstancedIndirect`. Même technique que `Assets/NyxInstancing/`, emballée pour être installée dans n'importe quel projet.

- Unity 2021.3 et plus, pipeline **Built-in**, PC et VR (Single Pass Instanced, multiview).
- Prêt à l'emploi : touffes, texture, matériaux et compute shaders par défaut sont fournis et assignés automatiquement.

## Installation

Au choix, dans `Packages/manifest.json` du projet :

- depuis GitHub (dépôt privé : git doit y avoir accès sur la machine ; `#<commit ou tag>` à la fin pour figer une version) :
  `"com.nyx.gpuinstancing": "https://github.com/AtaraxyState/GpuInstancing.git?path=/Packages/com.nyx.gpuinstancing"` ;
- depuis un dossier local :
  `"com.nyx.gpuinstancing": "file:C:/chemin/vers/com.nyx.gpuinstancing"`.

Ou **copier** le dossier `com.nyx.gpuinstancing` dans `Packages/` du projet (package intégré, modifiable).

Vérifier l'installation : **Tools > Nyx > GPU Instancing > Run Smoke Test** (rapport et captures dans `Logs/NyxGpuInstancingTest/`).

## Démarrage en 1 minute

- **Tools > Nyx > GPU Instancing > Create Demo Scene** : mer qui ondule, couche de sargasses avec amas au-dessus d'une mine et d'un leurre, colonnes de bulles, plongeur qui expire. Caméra libre en Play mode : clic droit + souris, ZQSD (ou WASD), A/E pour descendre/monter, Maj, molette.
- Dans une scène existante, menu **GameObject > Nyx > GPU Instancing** :

| Menu | Crée |
|---|---|
| Sargassum Field | Un champ de sargasses visible tout de suite (2 touffes × 2 LODs, matériau par défaut). |
| Bubble Field | Un champ de bulles avec une colonne, placé sous la surface. |
| Ocean Waves | La houle (un seul par scène). Ajoutée automatiquement avec les champs si elle manque. |
| Demo Water | Une surface d'eau simple qui suit la houle (exemple de synchronisation). |

Avec *Add Component*, le champ reçoit aussi ses assets par défaut ; sans `Ocean Waves` dans la scène, l'eau est plate à Y = 0.

## Composants

| Composant | Rôle |
|---|---|
| `Sargassum Field` | Touffes posées sur l'eau, qui suivent la houle. Placement procédural (zone, densité, graine) ou liste. **Cluster Anchors** : amas autour des mines **et de leurres**. **Layer Thickness** : épaisseur de la couche. |
| `Bubble Field` | Colonnes de bulles en boucle (coût CPU nul) et bulles ponctuelles par `Emit(position, nombre, dispersion)`. |
| `Bubble Burst Emitter` | Exemple : expirations périodiques d'un plongeur. |
| `Ocean Waves` | Niveau de l'eau et 4 vagues de Gerstner, partagés par le GPU, les shaders et le gameplay. |
| `Nyx Demo Water`, `Nyx Fly Camera` | Démo uniquement. |

Remplacer les touffes par défaut : **Variants**, pour chaque variante de 1 à 4 meshes (LOD0 en premier), les distances de LOD et un matériau `Nyx/GPU Instancing/Sargassum` avec **Enable GPU Instancing** coché.

### Côté gameplay

```csharp
// Une mine ou une bouée qui flotte exactement comme les algues
Vector3 rest = new Vector3(x, OceanWaves.WaterLevel, z);
transform.position = rest + OceanWaves.SampleDisplacement(new Vector2(x, z), out Vector3 normal);
transform.up = normal;

bubbles.Emit(regulator.position, 14, 0.03f);         // expiration d'un plongeur
foreach (SargassumPlacement p in sargassum.Placements) { /* positions des touffes */ }
```

### Synchroniser le shader d'eau du projet

Les sargasses suivent `NyxWaveDisplacement` (Gerstner). Pour qu'elles collent à l'eau, le shader d'eau applique la même fonction (voir `Shaders/DemoWater.shader`) :

```hlsl
#include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/OceanWaves.hlsl"

float3 positionWS = mul(unity_ObjectToWorld, v.vertex).xyz;   // mesh plat posé au niveau de l'eau
float3 normalWS;
positionWS += NyxWaveDisplacement(positionWS.xz, _NyxWaveTime, normalWS);
```

Avec une eau d'asset (Crest, KWS...) : régler `Ocean Waves` pour imiter sa houle, ou remplacer le corps de `NyxWaveDisplacement` par l'échantillonnage de l'asset.

## Fonctionnement

1. Les instances sont envoyées **une fois** au GPU (64 octets chacune).
2. Chaque frame, pour chaque caméra, un compute shader calcule la position finale (houle, montée des bulles), rejette ce qui est hors champ (les deux yeux en VR), trop loin ou plus petit qu'un pixel, choisit le LOD et range les instances visibles.
3. Un `DrawMeshInstancedIndirect` par (variante × LOD) : 4 draws pour les sargasses par défaut, 3 pour les bulles, pour 100 ou 1 000 000 d'instances.

Mesures (test de fumée, RTX 4070, éditeur 2021.3) : 460 touffes en 4 draws, 692 bulles en 3 draws, rendu complet de la vue de surface avec l'eau 0,36 ms CPU.

## VR

- Culling sur l'union des frustums des deux yeux.
- **Single Pass Instanced** (PC VR) : nombre d'instances doublé automatiquement. Si les instances n'apparaissent que dans un œil : `Stereo Instancing = ForceSinglePassInstanced` ; si elles clignotent ou sont en double : `ForceOff`.
- **Multiview** (PICO, Quest) : pas de doublement, détecté automatiquement.
- Ombres : draw `ShadowsOnly` séparé, automatique.

## Réglages recommandés

- `Max Draw Distance` : sous l'eau, inutile de dépasser la visibilité du brouillard (20 à 40 m pour les bulles).
- `Min Screen Size` : 0,001 (≈ 1 px) ; 0,002 à 0,003 sur casque autonome.
- Ombres des sargasses : `Off` par défaut. Si `On`, `Shadow Culling Extrusion` ≈ profondeur visible sous la surface.
- Rendu **Forward**. Chaque caméra (vue Scene, réflexion d'eau) a ses propres buffers.
- Matériaux : **Enable GPU Instancing** coché, sinon le build retire les variantes instanciées et rien ne s'affiche (le champ l'affiche en avertissement).

## Limites

- Shaders Built-in uniquement (porter `Sargassum.shader` et `Bubble.shader` pour URP ; le C# et les compute shaders ne changent pas).
- Bulles transparentes non triées entre elles (alpha prémultiplié : quasi invisible).
- Changement de LOD sans fondu.
- Rendu seulement, pas de collisions : le gameplay utilise `Placements`, `GetLayerDepth` et `OceanWaves.SampleDisplacement`.
- 64 draws maximum (variantes × LODs) par champ, 4 LODs par variante.

## Contenu

```
Runtime/   InstancedFieldBase (cœur : buffers, culling par caméra, draws, VR), SargassumField, BubbleField,
           BubbleBurstEmitter, OceanWaves, NyxDefaultAssets (éditeur), NyxDemoWater, NyxFlyCamera
Shaders/   Sargassum, Bubble, DemoWater, ShaderLibrary/ (données, setup, culling, vagues),
           Resources/NyxGpuInstancing/ (compute shaders de culling, chargés automatiquement)
Editor/    menus de création, scène de démo, test de fumée
Defaults/  touffes, texture de fronde, matériaux
```

Noms propres au package (namespace `Nyx.GpuInstancing`, shaders `Nyx/GPU Instancing/*`, menus `Nyx/GPU Instancing`) : il cohabite avec la version d'origine `Assets/NyxInstancing/` sans conflit.
