# NyxInstancing : instanciateur GPU (Unity 2021.3, Built-in, PC + VR)

Rendu de milliers de meshes identiques ou presque identiques (bulles sous-marines, sargasses flottantes) avec un nombre de draw calls **qui ne dépend pas du nombre d'instances**.

| | Coût par caméra et par frame |
|---|---|
| CPU | 2 dispatchs compute + 1 `DrawMeshInstancedIndirect` par (variante × LOD). Aucune donnée d'instance envoyée. |
| GPU | 1 thread de culling par instance, puis rendu des seules instances visibles |
| Draw calls | Sargasses 3 variantes × 3 LODs = **9** ; bulles 1 × 3 LODs = **3**. Pour 100 ou 1 000 000 d'instances. |

Avec 1 seule variante et 1 seul LOD, c'est **exactement 1 draw call par passe** pour tout le champ.

## Principe

1. Les instances sont envoyées **une seule fois** au GPU (64 octets chacune).
2. À chaque frame, pour chaque caméra, un compute shader :
   - calcule la position finale (flottaison sur la houle, montée des bulles...) ;
   - rejette ce qui est hors champ (frustum des **deux yeux** en VR), trop loin ou plus petit qu'un pixel ;
   - choisit le LOD selon la distance ;
   - écrit les instances visibles, compactées, dans une zone par draw.
3. Un second noyau écrit le nombre d'instances dans les arguments indirects.
4. `Graphics.DrawMeshInstancedIndirect` dessine chaque (variante × LOD) en un appel.

Le comptage utilise des atomiques en mémoire partagée : 1 atomique globale par groupe de 64 threads au lieu d'une par instance (64× moins de contention).

## Contenu

```
NyxInstancing/
  Runtime/
    InstancedFieldBase.cs   cœur : buffers, culling par caméra, draws, VR
    SargassumField.cs       champ de sargasses (placement procédural, amas autour des mines)
    BubbleField.cs          bulles : colonnes en boucle + Emit() ponctuel
    BubbleBurstEmitter.cs   exemple : expirations d'un plongeur
    OceanWaves.cs           houle partagée GPU / shaders / gameplay
    IcosphereBuilder.cs     sphères légères pour les bulles (12 / 42 / 162 sommets)
    GpuInstanceData.cs      structures de données
  Shaders/
    Sargassum.shader        surface shader : alpha-test, double face, ondulation, contre-jour
    Bubble.shader           transparent : bord argenté, reflet soleil, brouillard
    ShaderLibrary/          includes partagés (données, setup, culling, vagues)
    Resources/              compute shaders de culling (chargés automatiquement)
```

## Installation

1. Copier le dossier `NyxInstancing` dans `Assets/`.
2. Créer 2 matériaux :
   - `Nyx/Instancing/Sargassum` : texture des algues avec alpha (RGB + A) ;
   - `Nyx/Instancing/Bubble`.
3. **Cocher « Enable GPU Instancing » sur ces matériaux.** Sinon, avec le réglage par défaut *Graphics > Instancing Variants : Strip Unused*, le build supprime les variantes instanciées et rien ne s'affiche. Le champ affiche un avertissement si la case est décochée.
4. Ajouter **un** composant `Ocean Waves` dans la scène : niveau de l'eau et 4 vagues. Sans lui, l'eau est plate à Y = 0.

### Sargasses

1. GameObject au centre de la zone, composant `Sargassum Field`.
2. **Variants** : pour chaque touffe, LOD0 à LOD3 (1 mesh minimum), le matériau, les distances de LOD.
3. **Génération procédurale** : zone, densité (touffes / 100 m²), échelle, graine. La génération est déterministe et rien n'est sérialisé dans la scène.
4. **Layer Thickness** (section Flottaison) : épaisseur de la couche d'algues, en mètres.
   - 0 par défaut : une seule touffe d'épaisseur, en surface.
   - Au-delà : chaque touffe flotte à une profondeur fixe entre 0 et cette valeur, pour des touffes empilées.
   - La profondeur est dérivée de la graine de la touffe. Elle est donc déterministe, et identique pour un placement figé ou fourni par `SetPlacements`. Le placement des touffes ne change pas.
   - Coût nul : la profondeur tient dans les 64 octets de l'instance, et le nombre de draws reste le même.
   - Côté gameplay : `GetLayerDepth(placement)` renvoie la même valeur que le GPU.
5. **Cluster Anchors** : amas de touffes autour de points donnés. Mettre les mines **et des leurres** : si les algues n'apparaissent qu'au-dessus des mines, le joueur apprend à les repérer. Utiliser le point d'ancrage au repos, pas la mine animée.
6. En mode édition, le champ se régénère quand on déplace l'objet ou une ancre. En jeu, le placement est figé : appeler `SetDirty()` après un déplacement volontaire.

Menu contextuel « Figer le placement procédural dans la liste » : pour retoucher le placement à la main, ou le fournir par code (`SetPlacements`).

### Bulles

1. Composant `Bubble Field`, matériau Bubble. Les sphères sont générées automatiquement.
2. **Emitters** : colonnes en boucle infinie (évents, fuites, ambiance). **Coût CPU par frame : 0**, tout est une fonction du temps sur GPU.
3. **Sources mobiles** (plongeur, drone, hélice) : `Emit(position, nombre, dispersion)`, ou le composant `Bubble Burst Emitter` sur le détendeur (14 bulles toutes les 4,5 s par défaut). Coût : upload de 64 octets par bulle émise, rien ensuite.
4. Physique :
   - montée de 0,22 à 0,32 m/s ;
   - spirale ;
   - aplatissement et oscillation des grosses bulles ;
   - éclatement à la surface, vagues comprises ;
   - **loi de Boyle** : une bulle partie de 20 m arrive en surface avec un rayon × 1,44.

### Synchroniser le shader d'eau (important)

Les sargasses suivent la fonction de houle de `OceanWaves.hlsl`. Pour qu'elles collent exactement à l'eau, le shader d'eau doit utiliser la même fonction :

```hlsl
#include "Assets/NyxInstancing/Shaders/ShaderLibrary/OceanWaves.hlsl"

// dans le vertex shader de l'eau (mesh plat posé au niveau de l'eau)
float3 positionWS = mul(unity_ObjectToWorld, v.vertex).xyz;
float3 normalWS;
positionWS += NyxWaveDisplacement(positionWS.xz, _NyxWaveTime, normalWS);
```

Côté gameplay (mines, bouées, bateau), même calcul sur CPU :

```csharp
Vector3 rest = new Vector3(x, OceanWaves.WaterLevel, z);
transform.position = rest + OceanWaves.SampleDisplacement(new Vector2(x, z), out Vector3 normal);
transform.up = normal;
```

Si l'eau vient d'un asset (Crest, KWS...), deux options : régler `Ocean Waves` pour imiter sa houle, ou remplacer le corps de `NyxWaveDisplacement` par l'échantillonnage de l'asset.

## VR

- **Culling** : union des frustums des deux yeux, en un seul dispatch.
- **Single Pass Instanced (PC VR)** : chaque instance doit être dessinée 2 fois. `SetInstanceMultiplier` ne s'applique pas aux draws indirects, donc le nombre d'instances est doublé dans les arguments.
  - Si les instances n'apparaissent que dans un œil : `Stereo Instancing = ForceSinglePassInstanced`.
  - Si elles clignotent ou sont en double : `ForceOff`.
- **Multiview (PICO, Quest)** : l'œil vient de `gl_ViewID`, donc pas de doublement. C'est détecté automatiquement (`SystemInfo.supportsMultiview`).
- **Ombres en Single Pass Instanced** : la carte d'ombre n'est pas stéréo. Les ombres passent donc par un draw `ShadowsOnly` séparé, avec un nombre d'instances non doublé. C'est automatique.

## Budget (formules + exemple)

| | Formule | 30 000 sargasses (3 var. × 3 LODs) | 3 024 bulles (2 000 en boucle + 1 024 ponctuelles) |
|---|---|---|---|
| Buffer d'instances | N × 64 o | 1,9 Mo | 0,19 Mo |
| Visibles, **par caméra** | N × LODs × 64 o | 5,8 Mo | 0,58 Mo |
| Draw calls (passe principale) | variantes × LODs | 9 | 3 |
| Draw calls ombres | variantes × LODs × cascades | 36 avec 4 cascades | 0 (pas d'ombre) |

- En éditeur, la vue Scene est une caméra de plus.
- Une caméra de réflexion d'eau aussi : chaque caméra a ses propres buffers. Sans cela, une réflexion rendue pendant le rendu principal écraserait ses résultats.

Ordre de grandeur théorique du culling, pour 30 000 sargasses :

- **Calcul** : ≈ 150 opérations par instance (4 vagues + matrice + 12 plans), soit ≈ 4,5 MFLOP. C'est négligeable, même sur GPU mobile.
- **Mémoire** : au plus ≈ 128 octets lus et écrits par instance, soit ≈ 3,8 Mo par frame et par caméra.
  - PC à 300 Go/s : ≈ 13 µs.
  - Snapdragon XR2 (≈ 35 à 45 Go/s) : ≈ 0,1 ms.

À confirmer au Profiler GPU / RenderDoc : chercher les dispatchs `NyxCull` et `NyxWriteArgs`.

**Diagnostic** : cocher `Log Visible Counts`. La console affiche, 1 fois par seconde et par caméra :

- les instances totales ;
- les draws ;
- la mémoire GPU ;
- les instances visibles par variante/LOD.

La lecture GPU est asynchrone, sans blocage.

## Réglages recommandés

- `Max Draw Distance` : sous l'eau, inutile de dépasser la visibilité du brouillard (souvent 20 à 40 m pour les bulles).
- `Min Screen Size` : 0,001 (≈ 1 px) ; monter à 0,002 ou 0,003 sur casque autonome.
- Ombres des sargasses : `Off` par défaut. Si `On` : `Shadow Culling Extrusion` ≈ profondeur visible sous la surface, pour garder les ombres des touffes hors champ.
- Rendu **Forward** (standard en VR). Le shader de sargasses exclut le deferred, et `noforwardadd` évite qu'une lampe ponctuelle ajoute une passe sur tout le champ.

## Limites connues

- Shaders **Built-in** uniquement. Pour passer à URP, seuls `Sargassum.shader` et `Bubble.shader` sont à porter ; le C# et les compute shaders ne changent pas.
- Les bulles transparentes ne sont pas triées entre elles. L'alpha prémultiplié rend l'effet quasi invisible.
- Changement de LOD sans fondu (pop). Régler les distances en conséquence.
- Rendu seulement : pas de collisions. Le gameplay utilise `SargassumField.Placements`, `SargassumField.GetLayerDepth` et `OceanWaves.SampleDisplacement`.
- 64 draws maximum (variantes × LODs) par champ, 4 LODs maximum par variante.
- Fusionner des variantes de géométrie différente en un seul draw (*vertex pulling*) est possible, mais coûte ≈ 3 à 5× plus de travail vertex (plus de cache post-transform). Pour 3 à 5 variantes, garder 1 draw par variante est plus rapide.

## Vérifications effectuées

- Compute shaders compilés et validés en SPIR-V (glslang + spirv-val).
- Shaders de rendu compilés contre les CGIncludes Unity dans 5 variantes : procédural, Single Pass Instanced, brouillard, *épouser les vagues*, non instancié.
- C# compilé contre les signatures de l'API 2021.3, en configuration éditeur et player.
- 29 tests logiques :
  - winding des icosphères ;
  - plans du frustum ;
  - normales de Gerstner comparées aux dérivées numériques ;
  - LODs ;
  - tailles des structures ;
  - tampon circulaire d'`Emit`.

**Reste à valider dans Unity** : import des shaders, rendu Single Pass Instanced sur casque, ombres.

## Test automatisé dans Unity

`Editor/NyxSmokeTest.cs` construit une scène de test et vérifie, sur le GPU réel :

- la compilation des shaders et des compute shaders ;
- les instances visibles par draw ;
- les LODs ;
- le rendu, par différence d'image avec et sans le champ ;
- `Emit()` ;
- les ombres.

Rapport et captures dans `Logs/NyxSmokeTest/`. Trois façons de le lancer :

- menu **Tools > Nyx > Instancing Smoke Test** ;
- en batchmode : `-executeMethod Nyx.Instancing.EditorTests.NyxSmokeTest.RunFromCommandLine`, sans `-nographics` ;
- Unity ouvert, sans clic : créer le fichier `Logs/NyxSmokeTest/run.request`.

Pour le diagnostic, `TryReadVisibleCounts(camera, liste)` relit les instances visibles par draw. C'est une lecture GPU bloquante, réservée au debug.
