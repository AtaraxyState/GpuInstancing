# GpuInstancing

Projet Unity **2021.3.40f1**, pipeline **Built-in**, qui développe et teste un instanciateur GPU pour des sargasses flottantes et des bulles sous-marines : culling GPU par caméra (compute shader) et `Graphics.DrawMeshInstancedIndirect`, avec un nombre de draw calls qui ne dépend pas du nombre d'instances.

| Dossier | Contenu |
|---|---|
| `Packages/com.nyx.gpuinstancing/` | **Le package réutilisable** : sargasses, bulles, houle, assets par défaut, menus de création, test de fumée. Voir son [README](Packages/com.nyx.gpuinstancing/README.md). |
| `Assets/NyxGpuInstancingDemo/` | Scène de démo du package. |
| `Assets/NyxInstancing/` | Version d'origine (même technique, hors package), gardée telle quelle. Voir son [README](Assets/NyxInstancing/README.md). |
| `Assets/DevTools/` | Caméra libre de la scène de test d'origine, déclencheur de tests sans clic. |

## Ouvrir la démo

1. Unity Hub > **Add** > ce dossier, avec Unity 2021.3.40f1.
2. Ouvrir `Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity`, puis **Play**.
3. Caméra libre : clic droit maintenu + souris, ZQSD (ou WASD), A/E pour descendre/monter, Maj pour accélérer, molette pour la vitesse.

La scène se reconstruit à tout moment : **Tools > Nyx > GPU Instancing > Create Demo Scene**.

## Utiliser le package dans un autre projet

Dans `Packages/manifest.json` du projet :

```json
"com.nyx.gpuinstancing": "https://github.com/AtaraxyState/GpuInstancing.git?path=/Packages/com.nyx.gpuinstancing"
```

Le dépôt est privé : git doit pouvoir y accéder sur la machine (même identifiants que pour le cloner). Ajouter `#<commit ou tag>` à la fin de l'URL pour figer une version. Autre option : copier le dossier `Packages/com.nyx.gpuinstancing` dans `Packages/` du projet.

Ensuite : menus **GameObject > Nyx > GPU Instancing**, et **Tools > Nyx > GPU Instancing > Run Smoke Test** pour vérifier l'installation sur la machine cible.

## Tests

| Version | Menu | Rapport et captures |
|---|---|---|
| Package | Tools > Nyx > GPU Instancing > Run Smoke Test | `Logs/NyxGpuInstancingTest/` |
| Origine | Tools > Nyx > Instancing Smoke Test | `Logs/NyxSmokeTest/` |

Chaque test reconstruit sa scène dans `Assets/NyxGpuInstancingTest/` ou `Assets/NyxInstancingTest/` (dossiers régénérés, ignorés par git).

Sans clic, Unity ouvert (pour un agent) : créer un fichier vide dans le dossier de logs.

- `Logs/NyxGpuInstancingTest/` : `resolve.request` (relit `Packages/`), `refresh.request` (recompile), `run.request` (test), `demo.request` (scène de démo) ;
- `Logs/NyxSmokeTest/` : `refresh.request`, `run.request`.

Le résultat arrive dans `status.txt` (`done failures=N` ou `blocked ...`).
