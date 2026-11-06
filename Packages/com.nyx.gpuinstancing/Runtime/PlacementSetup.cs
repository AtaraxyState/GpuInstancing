using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nyx.GpuInstancing
{
    /// <summary>Réglages du placement lus sur le spawner (section Placement du MeshSpawner).</summary>
    internal struct PlacementSettings
    {
        public bool fromList;
        public List<MeshPlacement> list;   // fromList : liste du spawner (relue telle quelle)
        public int seed;
        public int variantCount;
        public float densityPer100m2;
        public Vector2 scaleRange;
        public List<Transform> clusterAnchors;
        public float clusterRadius;
        public int clumpsPerCluster;
        public bool clip;                  // tous les candidats testés contre le volume (polygone et hauteur)
    }

    /// <summary>
    /// Paramètres du placement sur GPU (InstancingPlacement.hlsl), figés à la reconstruction : candidats (grille perturbée sur
    /// la boîte de la zone, 1 par case, puis amas autour des ancres ; ou liste) et zone qui les filtre. GpuPlacement refait le
    /// même calcul pour relire les placements (MeshSpawner.Placements).
    /// </summary>
    internal sealed class PlacementSetup
    {
        const int MaxGeneratedInstances = 2_000_000;

        public int candidates;
        public bool fromList;
        public List<MeshPlacement> list;   // fromList : liste du spawner (relue telle quelle)
        public int gridCount;
        public int cellsX = 1;
        public Vector4 grid;               // coin min (x, z), pas (x, z)
        public Vector4 shape;              // hauteur, rayon des amas, échelle min, échelle max
        public int seed;
        public int clumps = 1;
        public int randomVariants = 1;
        public int tested;                 // candidats [0, n) testés contre le polygone
        public bool clip;
        public Vector4[] anchors = Array.Empty<Vector4>();
        public ZoneGpu zone;               // contour, boîte, hauteurs, bord naturel (lisière : placement procédural seulement)

        /// <summary>
        /// Candidats : grille perturbée sur la boîte du polygone de la zone (élargie du bruit du bord), puis amas autour des
        /// ancres ; ou liste. Hauteur : face du haut. Lit la position des ancres (thread principal). 'context' : objet des logs.
        /// </summary>
        public static PlacementSetup Create(in ZoneVolume zone, in PlacementSettings settings, Object context)
        {
            var setup = new PlacementSetup();
            Vector3 top = zone.Top;
            // Lisière : densité du placement procédural. Une liste (placement figé compris) a déjà ses instances : la
            // clairsemer retirerait, à chaque reconstruction, une partie de celles que la lisière a déjà gardées.
            setup.zone = zone.ToGpu(withFeather: !settings.fromList);
            // Bord naturel : le contour peut sortir du polygone de ± l'amplitude du bruit, la grille et la boîte aussi
            Vector4 gridBounds = setup.zone.Bounds;
            bool natural = zone.IsNatural(true);
            setup.clip = settings.clip;
            setup.seed = settings.seed;
            setup.randomVariants = Mathf.Max(settings.variantCount, 1);
            setup.shape = new Vector4(top.y, settings.clusterRadius, settings.scaleRange.x, settings.scaleRange.y);

            if (settings.fromList)
            {
                setup.fromList = true;
                setup.list = settings.list;
                setup.candidates = settings.list.Count;
                return setup;
            }

            var size = new Vector2(gridBounds.z - gridBounds.x, gridBounds.w - gridBounds.y);
            int cellsX = 0, cellsZ = 0;
            float stepX = 0f, stepZ = 0f;
            if (settings.densityPer100m2 > 0f && size.x > 0f && size.y > 0f)
            {
                float cell = Mathf.Sqrt(100f / settings.densityPer100m2);
                cellsX = Mathf.Max(1, Mathf.RoundToInt(size.x / cell));
                cellsZ = Mathf.Max(1, Mathf.RoundToInt(size.y / cell));
                if ((long)cellsX * cellsZ > MaxGeneratedInstances)
                {
                    Debug.LogWarning($"{context.name} : {(long)cellsX * cellsZ} cases de grille demandées sur la boîte de la zone, limité à {MaxGeneratedInstances}.", context);
                    float reduction = Mathf.Sqrt((float)MaxGeneratedInstances / ((long)cellsX * cellsZ));
                    cellsX = Mathf.Max(1, (int)(cellsX * reduction));
                    cellsZ = Mathf.Max(1, (int)(cellsZ * reduction));
                }
                stepX = size.x / cellsX;
                stepZ = size.y / cellsZ;
            }

            var anchors = new List<Vector4>(settings.clusterAnchors.Count);
            foreach (Transform anchor in settings.clusterAnchors)
            {
                if (anchor != null)
                    anchors.Add(anchor.position);
            }
            setup.anchors = anchors.ToArray();
            setup.gridCount = cellsX * cellsZ;
            setup.cellsX = Mathf.Max(cellsX, 1);
            setup.grid = new Vector4(gridBounds.x, gridBounds.y, stepX, stepZ);
            setup.clumps = Mathf.Max(settings.clumpsPerCluster, 1);
            setup.candidates = setup.gridCount + anchors.Count * Mathf.Max(settings.clumpsPerCluster, 0);
            // Rectangle aligné sur les axes : la grille est dedans par construction. Ne pas la tester évite de perdre les
            // points arrondis pile sur le bord (loin de l'origine) : même placement qu'avec l'ancienne zone. Sans bord naturel,
            // la boîte de la zone est exactement celle du polygone.
            setup.tested = !natural && ZonePolygon.IsAxisAlignedRectangle(setup.zone.Vertices, gridBounds) ? 0 : setup.gridCount;
            return setup;
        }
    }
}
