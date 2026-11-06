# Nyx GPU Instancing

Des milliers de copies de meshes (sargasses flottantes, bulles sous-marines, rochers...) avec un nombre de draw calls qui ne dépend pas du nombre d'instances : culling GPU par caméra (compute shader) et `Graphics.DrawMeshInstancedIndirect`. Même technique que `Assets/NyxInstancing/`, emballée pour être installée dans n'importe quel projet.

- Unity 2021.3 et plus, pipeline **Built-in**, PC et VR (Single Pass Instanced, multiview).
- Un composant **`Mesh Spawner`** + un **comportement** au choix, qui décide comment les instances bougent. Trois sont fournis, et en écrire un ne demande qu'une classe courte et un compute shader.
- Prêt à l'emploi : meshes, matériaux et compute shaders par défaut sont fournis et assignés automatiquement.
- Dépendances installées automatiquement par le Package Manager : Burst 1.8.26 et Mathematics 1.2.6 (instances écrites en C# par un comportement : jobs Burst, en mémoire native ; le placement sur GPU reprend le tirage aléatoire de Mathematics).

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
- **Zone** : un polygone extrudé vers le bas (voir ci-dessous) ;
- **Placement** : grille perturbée dans la zone (densité, graine, échelle), amas autour de **Cluster Anchors** (mines **et leurres**), ou liste (`SetPlacements`, menu contextuel « Figer le placement ») ;
- rendu, culling GPU, LODs, VR, ombres.

### Zone : polygone extrudé

Trois réglages : le **centre de la face du haut** (`Zone Center`, décalage par rapport à l'objet, axes du monde), les **sommets** de cette face par rapport au centre (`Zone Points`, X et Z, dans l'ordre du contour, 3 à 4 096, concave possible ; le test coûte en proportion du nombre de sommets, voir **Bord naturel**), et la **profondeur** du volume sous la face (`Zone Depth`).

- Les instances sont placées **sur la face du haut**, dans le polygone (Float On Waves ignore cette hauteur : elle vient de l'eau).
- **Clip To Volume** (activé à la création, sauf pour Bubble Columns) : une instance hors du volume est **détruite**. Au placement, elle n'est pas créée (amas d'une ancre hors zone, liste figée). En mouvement, la première fois que le culling la voit hors du volume, sa variante devient « désactivée » dans le buffer d'instances : elle n'est plus évaluée ni dessinée jusqu'à la prochaine reconstruction (ou jusqu'à ce que le comportement réécrive sa case, comme `Emit` des bulles). Le test se fait sur le pivot de l'instance, quand le spawner est rendu par au moins une caméra.
- Vue Scene (objet sélectionné) : volume en orange si Clip To Volume est actif, en bleu sinon. Glisser un sommet pour le déplacer, **Ctrl+clic** pour le supprimer, clic sur le rond au milieu d'une arête pour insérer un sommet. Poignées de position pour le centre, et cube sous le volume pour la profondeur.
- Si le mouvement du comportement dépasse la hauteur du volume (crêtes des vagues au-dessus de la face du haut...), l'inspecteur l'indique (`MotionExceedsVolumeHeight`) ; pour Float On Waves, le bouton **Poser le volume sur l'eau** (`FloatOnWaves.FitZoneToWater`) met la face du haut au-dessus des crêtes et le fond sous les creux et la couche, ce que fait aussi l'ajout du comportement.
- Spawners enregistrés avant la zone : sans sommets, ils gardent leur ancien rectangle (même placement) et Clip To Volume reste coupé. Bouton **Convertir la zone en polygone** pour l'éditer.
- Coût : nul de façon mesurable sur GPU (1 000 000 de rochers, étoile de 64 sommets : le culling reste lu par la mémoire, ~1 ms sur GTX 1060) ; au placement, le filtrage se fait sur GPU, dans le même passage que la génération.
- **Bord naturel** : quelques points de contrôle suffisent pour une zone au contour naturel. Le même calcul sert au placement, à la destruction et à `VolumeContains`.
  - *Zone Smooth* : courbe qui passe par chaque point (Catmull-Rom centripète), découpée en 64 sommets au plus.
  - *Zone Edge Noise* et *Zone Edge Noise Scale* : bord irrégulier, déplacé de ± l'amplitude par un bruit fractal suivant la taille des irrégularités.
  - *Zone Edge Feather* : lisière, où la densité baisse jusqu'à zéro avant le bord. Placement procédural seulement : une liste, placement figé compris, garde toutes ses instances.
  - La vue Scene montre la courbe, le bord irrégulier et la limite de la lisière (pointillés).
  - Coût : 0,75 ms pour 1 000 000 de tests sur GTX 1060, contre 8 ms pour un contour décrit par 1 024 sommets. Plutôt que de longues listes de points, ajouter ces réglages : voir `Documentation~/Optimisations.md` et la scène de test `Assets/DevTools/Scenes/ZoneNaturelleTest.unity` du dépôt.

Le **comportement** est un second composant sur le même objet. Dans l'inspecteur du Mesh Spawner, la liste **Comportement** propose tous les comportements du projet, y compris ceux que vous écrivez. Changer de comportement garde les meshes et le placement ; le bouton **Meshes par défaut du comportement** remplace les meshes par ceux fournis avec lui. Ajouter un comportement par *Add Component* ajoute aussi le Mesh Spawner et ses meshes par défaut.

| Comportement | Mouvement | Matériau | Placement |
|---|---|---|---|
| `Float On Waves` | Flotte sur la houle : hauteur et inclinaison des vagues, dandinement, dérive, **Layer Thickness** (épaisseur de la couche). | `Sargassum` | Celui du spawner (la hauteur vient de l'eau). Volume posé sur l'eau à l'ajout. |
| `Bubble Columns` | Monte en spirale, grossit (loi de Boyle), éclate en surface. Colonnes en boucle (coût CPU nul par frame, construites sur GPU) et `Emit(position, nombre, dispersion)`. | `Bubble` | Propre : émetteurs du comportement. Clip To Volume coupé par défaut. |
| `Static Instances` | Immobile. | `Lit` | Celui du spawner, sur la face du haut. |

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
    dst.row0 = src.row0;                 // matrice 3x4 construite sur GPU (position, lacet, échelle)
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

    // Les instances montent au-dessus de la face du haut (où elles sont placées) : pas de destruction par le volume
    public override bool ClipToVolumeByDefault => false;

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
| `CreateInstance(placement)` | Donnée d'une instance placée écrite en C# (64 octets : `row0..row2`, `custom`), pour y mettre `custom.y/z` ou une autre interprétation des lignes. **À éviter** : calculer plutôt ces données dans `NyxEvaluateInstance` à partir de la graine (`custom.x`), comme `Float On Waves` la profondeur dans sa couche. Surcharger cette méthode fait lire les placements sur GPU, puis envoyer les instances : bien plus lent. | construite sur GPU : matrice position + lacet + échelle, graine en `custom.x` |
| `CreateInstances(placements, output)` | La même chose pour toutes les instances, en `NativeArray`, avec un job Burst. Mêmes réserves. | appelée seulement si l'une des deux est surchargée |
| `GetPlacementBounds(positions, meshRadius)` | Boîte monde de toutes les positions possibles (culling du spawner entier). | positions + rayon des meshes |
| `UsesPlacement` + `GetInstanceCount()` + `BuildInstancesOnGpu(instances, count, out bounds)` | `false` : le comportement fournit ses propres instances (comme `Bubble Columns`). `BuildInstancesOnGpu` les écrit directement dans le buffer GPU, par un noyau de son compute shader lancé avec `MeshSpawner.DispatchInstances` ; sinon `BuildInstances(output)` les écrit en C# dans une `NativeArray` de la taille annoncée, envoyée ensuite. | `true` |
| `GetBoundsPadding(variant, localBounds, default)` | Marge si le vertex shader déforme les meshes. | réglage du spawner |
| `ClipToVolumeByDefault` | Clip To Volume donné au spawner à l'ajout du comportement : `false` si les instances sortent normalement de la zone (bulles qui montent, dandinement au-dessus de la face du haut, comme `Bobbing`). | `true` |
| `CreateDefaultVariants()` | Meshes et matériau donnés au spawner à l'ajout (éditeur). | aucun |

Côté rendu, le shader lit `NyxGetRenderData().custom` (valeurs écrites par le compute shader). `Shaders/Lit.shader` est un modèle de surface shader compatible : trois lignes `#pragma` et l'include de `InstancingSetup.cginc` suffisent.

### Côté gameplay

```csharp
// Une mine ou une bouée qui flotte exactement comme les algues
Vector3 rest = new Vector3(x, OceanWaves.WaterLevel, z);
transform.position = rest + OceanWaves.SampleDisplacement(new Vector2(x, z), out Vector3 normal);
transform.up = normal;

bubbles.GetComponent<BubbleColumns>().Emit(regulator.position, 14, 0.03f);   // expiration d'un plongeur
foreach (MeshPlacement p in spawner.Placements) { /* positions au repos (lues sur GPU à la 1re demande après une reconstruction) */ }

// Zone par code : centre de la face du haut (par rapport à l'objet), sommets X/Z, profondeur
spawner.SetZone(new Vector3(0f, 1f, 0f), new[] { new Vector2(-20f, -10f), new Vector2(25f, -15f), new Vector2(10f, 20f) }, 4f);
bool inside = spawner.VolumeContains(diver.position);   // même règle que le placement et le GPU
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

1. À la reconstruction, les instances sont construites **sur GPU** (`InstancePlacement.compute`) : candidats (grille perturbée sur la boîte du polygone, amas, ou liste), filtrage par la zone, matrice de chaque instance écrite directement dans le buffer d'instances, dans l'ordre des candidats (masque de bits par groupe et somme préfixe). Le CPU prépare quelques paramètres et ne lit que des compteurs (total, nombre par variante, boîte des positions) pour dimensionner les buffers. Le tirage aléatoire est celui de l'ancien calcul CPU (portage exact de `Unity.Mathematics.Random`) : les scènes existantes gardent leur placement. Les colonnes de bulles sont construites de même par `BubbleColumnsBuild.compute`. Un comportement qui écrit ses instances en C# reçoit les placements lus sur GPU, et ses instances sont envoyées une fois (`ComputeBuffer.SetData`), puis comptées par variante sur GPU (`InstanceVariants.compute`).
2. Chaque frame, pour chaque caméra, le compute shader du comportement calcule la position finale de chaque instance ; avec Clip To Volume, une instance hors du volume de la zone est détruite (marquée dans le buffer d'instances) ; le culling commun rejette ce qui est hors champ (les deux yeux en VR), trop loin ou plus petit qu'un pixel, choisit le LOD et écrit chaque instance visible une seule fois, avec son index (4 octets) dans la liste de son draw.
3. Un `DrawMeshInstancedIndirect` par (variante × LOD), pour 100 ou 1 000 000 d'instances.

Mesures (test de fumée, RTX 4070, éditeur 2021.3) : 460 touffes en 4 draws, 692 bulles en 3 draws, 196 rochers en 2 draws ; rendu complet de la vue de surface avec l'eau ≈ 0,4 ms CPU.

Construction sur GPU plutôt que par des jobs Burst suivis d'un envoi (éditeur, i7-6700K, GTX 1060). Reconstruction, temps du thread principal, puis entre parenthèses l'image d'édition (reconstruction + premier rendu + fin du GPU) :

| | Jobs Burst + envoi | Sur GPU |
|---|---|---|
| 1 000 000 de rochers | 45 ms (62 ms) | 1,3 ms (7 ms) |
| 1 000 000 de touffes | 42 ms (69 ms) | 1,8 ms (6 ms) |
| 640 000 touffes de la démo | 29 ms (59 ms) | 1,0 ms (27 ms, surtout le rendu) |
| 1 000 000 de bulles | 70 ms (86 ms) | 1,1 ms (8,5 ms) |
| liste figée de 1 000 000 de placements | 308 ms | 22 ms (envoi de la liste, 28 Mo) |

Banc : `Assets/DevTools/Editor/MeshSpawnerPerfBenchmark.cs` du dépôt ; à chaque cas, les instances construites sur GPU sont comparées à celles écrites en C# depuis les mêmes placements (écart < 10⁻⁶). Le placement et les bulles sont identiques à ceux de l'ancien calcul CPU (mêmes nombres, variantes et graines ; positions à l'arrondi près). L'ancien envoi par buffer intermédiaire (job qui écrit dans un buffer `SubUpdates`, copié par un compute shader) a été retiré : mesuré jusqu'à la fin du GPU, la copie lit lentement la mémoire visible du CPU (≈ 50 ms de GPU pour 64 Mo, contre ≈ 18 ms pour `SetData`).

Coût GPU d'un grand champ de sargasses : scène de démo, environ 930 000 touffes dont 440 000 visibles ; GTX 1060, 1280 × 720, ombres du soleil actives. Outil : `Assets/DevTools/Editor/DemoGpuBreakdown.cs` du dépôt, qui mesure la scène ouverte vue de sa caméra principale.

| Poste | Coût mesuré |
|---|---|
| reste de la scène | 1 ms |
| culling des 930 000 touffes | 0,6 ms |
| sommets (2 passes : profondeur, image) | ≈ 4 ms |
| pixels | ≈ 6 ms |

Ce qui a été fait :
- La graine passe au pixel shader par un interpolateur au lieu d'être relue dans le buffer à chaque pixel : −0,4 à −0,8 ms, sans changement à l'image.
- Exclude From Depth Texture, voir **Réglages recommandés** : −4 ms.

Pistes mesurées, mais qui changent l'image ou n'apportent rien :
- les touffes au-delà de 60 m représentent la moitié des visibles et environ 3 ms ;
- dessiner tous les LOD0 en premier, ou calculer l'inverse de matrice plus simplement : aucun gain mesurable.

Dans le Profiler, les sections `MeshSpawner.Rebuild`, `MeshSpawner.BuildInstances`, `MeshSpawner.Upload` (instances écrites en C# seulement) et `MeshSpawner.Render` nomment chaque étape. `MeshSpawner.BuildInstances` comprend l'attente des compteurs du GPU : la reconstruction attend que le GPU ait fini le travail déjà soumis.

## Mémoire

| | Formule | 1 000 000 d'instances, 2 LODs |
|---|---|---|
| CPU, entre deux reconstructions | rien ; les placements (N × 28 o, mémoire native) seulement s'ils ont été lus (`Placements`) | 0 |
| CPU, pendant une reconstruction | quelques paramètres et compteurs | 0 |
| GPU, pendant une reconstruction | masque des candidats (1 bit chacun) ; placement en liste : + N × 28 o | 0,1 Mo (liste : + 27 Mo) |
| GPU, instances | N × 64 o | 61 Mo |
| GPU, culling | N × 64 o + N × LODs × 4 o, **un seul jeu pour toutes les caméras rendues l'une après l'autre** | 69 Mo |

- Les caméras rendues l'une après l'autre (vue Game, vue Scene, écran partagé) partagent le même jeu de buffers de culling. Une caméra rendue pendant le rendu d'une autre (réflexion d'eau via `Camera.Render`) en prend un second ; `CullingBufferSets` donne le nombre de jeux.
- Instances écrites en C# (comportement qui surcharge `CreateInstance(s)` ou `BuildInstances`) : + N × 28 o de placements lus sur GPU (gardés), + N × 64 o d'instances en mémoire native le temps de l'envoi (`SetData`).
- Diagnostic : `GpuMemoryBytes`, `CpuMemoryBytes`, `CullingBufferSets` et `LastUploadMethod` (`BuiltOnGpu`, ou `SetData` pour des instances écrites en C#) sur le Mesh Spawner. `MeshSpawner.CreateInstancesOnGpu = false` fait écrire les instances placées en C# (comparaison, débogage).
- Mesuré sur 1 000 000 d'instances et 2 caméras, avant / après cette organisation : mémoire C# retenue 153 Mo → 0, GPU 305 Mo → 130 Mo, reconstruction 1,8 s → 50 ms. Le tas Mono d'Unity ne rend jamais la mémoire au système : éviter les gros tableaux C# évite qu'il grossisse pour de bon.

## VR

- Culling sur l'union des frustums des deux yeux.
- **Single Pass Instanced** (PC VR) : nombre d'instances doublé automatiquement. Si les instances n'apparaissent que dans un œil : `Stereo Instancing = ForceSinglePassInstanced` ; si elles clignotent ou sont en double : `ForceOff`.
- **Multiview** (PICO, Quest) : pas de doublement, détecté automatiquement.
- Ombres : draw `ShadowsOnly` séparé, automatique.

## Réglages recommandés

- `Max Draw Distance` : sous l'eau, inutile de dépasser la visibilité du brouillard (20 à 40 m pour les bulles).
- `Min Screen Size` : 0,001 (≈ 1 px) ; 0,002 à 0,003 sur casque autonome.
- Ombres : `Off` par défaut. Si `On` pour des sargasses, `Shadow Culling Extrusion` ≈ profondeur visible sous la surface.
- **Exclude From Depth Texture** pour un grand champ de végétation alpha-testée qui n'a pas besoin de recevoir d'ombres (sargasses en pleine mer). Dès que le soleil projette des ombres, Unity redessine toute la scène opaque dans une texture de profondeur avant l'image. Cette passe coûte presque autant que l'image elle-même, et l'option en sort les instances.
  - Gain mesuré sur la démo (930 000 touffes, 440 000 visibles, GTX 1060, 1280 × 720) : 12,4 → 8,3 ms GPU.
  - En contrepartie, les instances ne reçoivent plus d'ombres (d'un bateau par exemple), et les effets qui lisent la profondeur ne les voient plus : profondeur de champ, SSAO, eau qui lit la profondeur.
  - Elles projettent toujours leur ombre si `Cast Shadows` est actif.
  - Le spawner dessine avec des copies des matériaux, dont la file de rendu passe au-dessus de 2500 ; les assets ne sont pas modifiés.
  - `Receive Shadows` décoché seul ne fait presque rien gagner (0,4 ms) : la passe de profondeur dessine toujours les instances.
- Rendu **Forward**. Chaque caméra (vue Scene, réflexion d'eau) a ses propres buffers.
- Matériaux : **Enable GPU Instancing** coché, sinon le build retire les variantes instanciées et rien ne s'affiche (le spawner l'affiche en avertissement).

## Limites

- Shaders Built-in uniquement (porter les shaders de rendu pour URP ; le C# et les compute shaders ne changent pas).
- Un seul comportement par Mesh Spawner (le premier activé).
- Bulles transparentes non triées entre elles (alpha prémultiplié : quasi invisible).
- Changement de LOD sans fondu.
- Rendu seulement, pas de collisions : le gameplay utilise `Placements` (lus sur GPU à la demande : à garder plutôt qu'à relire), `FloatOnWaves.GetLayerDepth` et `OceanWaves.SampleDisplacement`.
- Une reconstruction attend le GPU (lecture des compteurs) : à éviter à chaque image en jeu.
- 64 draws maximum (variantes × LODs) par spawner, 4 LODs par variante.
- Zone : 4 096 sommets maximum ; au-delà de 64, le test de la zone se voit dans le temps GPU (2 048 sommets, 240 000 instances : 5,4 ms par image et par caméra avec Clip To Volume). Un point pile sur un bord compte comme dehors. La destruction n'a lieu que quand le spawner est rendu (hors champ, rien n'est testé) ; Bubble Columns avec Clip To Volume : une bulle en boucle qui sort du volume ne revient qu'à la reconstruction.

## Contenu

```
Runtime/   MeshSpawner (composant) et ses classes internes (voir « Organisation du code »),
           MeshSpawnerBehavior, FloatOnWaves, BubbleColumns (+ BubbleBursts, BubbleColumnBuilder), StaticInstances,
           BubbleBurstEmitter, OceanWaves, NyxDefaultAssets (éditeur), NyxDemoWater, NyxFlyCamera
Shaders/   Sargassum, Bubble, Lit, DemoWater, ShaderLibrary/ (bibliothèques HLSL, voir « Organisation du code »),
           Resources/NyxGpuInstancing/ : un compute shader de culling par comportement (chargé automatiquement),
           InstancePlacement et InstanceVariants (construction), BubbleColumnsBuild (colonnes de bulles)
Editor/    inspecteur du Mesh Spawner, poignées de la zone (vue Scene), outils, menus de création, scène de démo,
           test de fumée
Documentation~/Optimisations.md   journal des optimisations, mesures et images avant / après
Defaults/  touffes, bulles, rochers, texture de fronde, matériaux
```

Noms propres au package (namespace `Nyx.GpuInstancing`, shaders `Nyx/GPU Instancing/*`, menus `Nyx/GPU Instancing`) : il cohabite avec la version d'origine `Assets/NyxInstancing/` sans conflit.

## Organisation du code

Le `MeshSpawner` est une coque mince : réglages sérialisés, API publique, cycle de vie Unity, orchestration de la reconstruction et du rendu. Le travail est fait par des classes C# sans état Unity, chacune avec une seule responsabilité. Chaque classe qui travaille sur GPU a son compute shader ou sa bibliothèque HLSL, et chaque calcul fait des deux côtés a son double en C#.

| Partie | C# | GPU |
|---|---|---|
| Polygone de la zone : contour courbe (Catmull-Rom), boîte, rectangle | `ZonePolygon` | — |
| Bord de la zone : règle pair-impair, distance signée, bord naturel | `ZoneEdge` (public) | `InstancingZone.hlsl` |
| Bruit du bord naturel | `ValueNoise` | `InstancingNoise.hlsl` |
| Volume de la zone, figé : appartenance, bounds, paramètres des noyaux | `ZoneVolume` | — |
| Zone envoyée aux noyaux (sommets, boîte, hauteurs, bord), pour le placement et la destruction | `ZoneGpu` | — |
| Variantes : LODs, variantes dessinables, limites | `VariantPlan` | `InstancingVariants.hlsl` |
| Placement : candidats (grille, amas, liste), filtre de la zone, instance par défaut | `PlacementSetup` | `InstancingPlacement.hlsl` |
| Placement : comptage, somme préfixe, écriture à leur rang | `GpuPlacement` | `InstancePlacement.compute` |
| Comptage par variante des instances écrites par un comportement | `GpuVariantCounter` | `InstanceVariants.compute` |
| Buffer d'instances : construction GPU ou C#, placements relus à la demande | `InstanceBuilder` | — |
| Caméra : frustum (deux yeux), distance, LOD, taille à l'écran, ombres, VR ; rejet CPU du spawner | `CameraCulling` | `InstancingCamera.hlsl` |
| Culling par instance et compactage des visibles, arguments indirects | `InstanceRenderer` | `InstancingCulling.hlsl` (inclus par le compute du comportement) |
| Jeux de buffers de culling partagés entre caméras, arguments par caméra | `CullingBufferPool` | — |
| Draws (variante × LOD) et table des variantes | `DrawBuckets` | — |
| Bounds des draws (instances, émissions en vol, limite du volume) | `DrawBounds` | — |
| Copies de matériaux hors de la texture de profondeur | `DepthlessMaterials` | — |
| Dispatch « une instance par thread », lecture GPU, identifiants, marqueurs du Profiler | `SpawnerGpu` | `InstancingDispatch.hlsl` |
| Bulles : émissions ponctuelles (tampon circulaire, bounds en vol) | `BubbleBursts` | — |
| Bulles : colonnes en boucle construites sur GPU | `BubbleColumnBuilder` | `BubbleColumnsBuild.compute` |

Éditeur : `MeshSpawnerEditor` (inspecteur), `ZoneHandles` (zone dans la vue Scene), `ZoneContour` (tracé du bord naturel), `MeshSpawnerTools` (opérations par script).

Règles à suivre en modifiant le package :

- **Composant mince** : un MonoBehaviour garde les réglages, l'API et le cycle de vie ; le travail va dans une classe interne qui ne fait qu'une chose et libère ce qu'elle alloue.
- **Données sur le chemin chaud** : structs, `NativeArray`, `ComputeBuffer`, aucune allocation par image (le benchmark le vérifie), aucun appel virtuel ni interface par instance. L'abstraction se place au niveau d'un spawner et d'une caméra.
- **GPU d'abord** : le compute shader fait le travail ; un calcul fait aussi en C# (zone, hasard) sert à l'éditeur, au gameplay et aux tests.
- **Un fichier, un rôle** : une classe C# ou une bibliothèque HLSL par responsabilité, un compute shader par étape GPU (culling, placement, comptage, construction d'un comportement). Les noms vont par paires : `ZoneEdge` / `InstancingZone.hlsl`, `CameraCulling` / `InstancingCamera.hlsl`, `GpuPlacement` / `InstancePlacement.compute`.
- **Parité testée** : tout calcul fait à la fois en HLSL et en C# a un test d'accord dans le test de fumée (modèle : `ZoneEdge` / `InstancingZone.hlsl`).
- **Comportements = stratégie** : le mouvement vient d'un `MeshSpawnerBehavior`, jamais d'un `if` sur le type dans le spawner.
- **Sérialisation stable** : ne pas renommer ni déplacer un champ sérialisé sans `[FormerlySerializedAs]` ; les scènes existantes doivent s'ouvrir telles quelles.
- **Refactor sans changement de résultat** : mêmes instances, mêmes images. Outil : `Assets/DevTools/Editor/RefactorParityCheck.cs` (`SaveReference` avant, `Compare` après : placements, draws, mémoire, zone et image identiques, instances à quelques ulp près), puis test de fumée et benchmark.
