using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nyx.Instancing
{
    /// <summary>Placement d'une touffe de sargasses (position au repos sur l'eau).</summary>
    [Serializable]
    public struct SargassumPlacement
    {
        [Tooltip("Position XZ monde (la hauteur vient de l'eau).")]
        public Vector2 position;
        [Tooltip("Rotation autour de Y (degrés).")]
        public float yaw;
        public float scale;
        public int variant;
        [Range(0f, 1f)] public float seed;
    }

    /// <summary>
    /// Champ de sargasses flottantes, toutes variantes confondues en 1 seul dispatch de culling GPU,
    /// puis 1 draw call par (variante × LOD). Flottaison, inclinaison et dérive calculées sur GPU,
    /// ondulation des frondes dans le vertex shader. Rien n'est renvoyé par le CPU à chaque frame.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Nyx/Instancing/Sargassum Field")]
    public sealed class SargassumField : InstancedFieldBase
    {
        public enum PlacementSource
        {
            /// <summary>Généré à partir des paramètres + graine (déterministe, rien de sérialisé dans la scène).</summary>
            Procedural,
            /// <summary>Liste explicite (SetPlacements, ou menu contextuel « Figer le placement »).</summary>
            List
        }

        const int MaxGeneratedInstances = 2_000_000;
        const string ConformKeyword = "_NYX_CONFORM_WAVES";

        [Header("Variantes (touffes)")]
        [SerializeField] List<InstanceVariant> variants = new List<InstanceVariant>();

        [Header("Placement")]
        [SerializeField] PlacementSource source = PlacementSource.Procedural;
        [SerializeField] List<SargassumPlacement> placements = new List<SargassumPlacement>();

        [Header("Génération procédurale")]
        [SerializeField] int randomSeed = 1234;
        [Tooltip("Zone rectangulaire (m) centrée sur cet objet.")]
        [SerializeField] Vector2 areaSize = new Vector2(200f, 200f);
        [Tooltip("Touffes pour 100 m², réparties sur une grille perturbée (couverture régulière, sans amas ni trous).")]
        [SerializeField, Min(0f)] float densityPer100m2 = 2f;
        [SerializeField] Vector2 scaleRange = new Vector2(0.7f, 1.4f);
        [Tooltip("Amas de touffes autour de ces points : les mines... ET des leurres, sinon la présence d'algues trahit la mine. " +
                 "Position XZ lue à la génération : utiliser le point d'ancrage au repos, pas la mine animée.")]
        [SerializeField] List<Transform> clusterAnchors = new List<Transform>();
        [SerializeField, Min(0f)] float clusterRadius = 5f;
        [SerializeField, Min(0)] int clumpsPerCluster = 30;

        [Header("Flottaison")]
        [Tooltip("Décalage vertical par rapport à la surface (m). Négatif = partiellement immergé.")]
        [SerializeField] float floatOffset = -0.02f;
        [Tooltip("Épaisseur de la couche d'algues (m). 0 = une seule touffe d'épaisseur, en surface. Sinon chaque touffe flotte " +
                 "à une profondeur fixe entre 0 et cette valeur (en plus de Float Offset), dérivée de sa graine : déterministe, " +
                 "valable aussi pour les placements figés ou fournis par SetPlacements.")]
        [SerializeField, Min(0f)] float layerThickness;
        [Tooltip("Dandinement propre à chaque touffe, en plus des vagues (m).")]
        [SerializeField, Min(0f)] float bobAmplitude = 0.02f;
        [SerializeField, Min(0f)] float bobSpeed = 1.3f;
        [Tooltip("0 = reste horizontale, 1 = suit exactement la pente de la vague.")]
        [SerializeField, Range(0f, 1f)] float tiltFollow = 0.85f;
        [Tooltip("Rayon de dérive autour de la position de repos (m) : vivant, mais la mine reste couverte.")]
        [SerializeField, Min(0f)] float wanderRadius = 0.2f;
        [SerializeField, Min(0f)] float wanderSpeed = 0.15f;

        static readonly int SargassumFloatId = Shader.PropertyToID("_SargassumFloat");
        static readonly int SargassumWanderId = Shader.PropertyToID("_SargassumWander");

        readonly List<SargassumPlacement> _generated = new List<SargassumPlacement>();
        readonly List<Vector3> _anchorPositions = new List<Vector3>();

        /// <summary>Placements effectivement utilisés au dernier rendu (utile au gameplay).</summary>
        public IReadOnlyList<SargassumPlacement> Placements => source == PlacementSource.List ? (IReadOnlyList<SargassumPlacement>)placements : _generated;

        protected override string DefaultCullingShaderName => "NyxSargassumCulling";

        /// <summary>Remplace le placement (passe en mode List). Coût : une reconstruction (upload de N × 64 octets).</summary>
        public void SetPlacements(IEnumerable<SargassumPlacement> newPlacements)
        {
            placements.Clear();
            placements.AddRange(newPlacements);
            source = PlacementSource.List;
            SetDirty();
        }

        [ContextMenu("Figer le placement procédural dans la liste")]
        void BakeProceduralToList()
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "Figer le placement des sargasses");
#endif
            GeneratePlacements(_generated);
            placements = new List<SargassumPlacement>(_generated);
            source = PlacementSource.List;
            SetDirty();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        void Update()
        {
            // Mode édition uniquement : le placement suit le champ et les ancres pendant que le level designer les déplace.
            // En jeu, le placement est figé (une mine qui flotte ne doit pas régénérer le champ à chaque frame) :
            // appeler SetDirty() après un déplacement volontaire.
            if (Application.isPlaying || source != PlacementSource.Procedural)
                return;

            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                SetDirty();
                return;
            }
            for (int i = 0; i < clusterAnchors.Count && i < _anchorPositions.Count; i++)
            {
                Transform anchor = clusterAnchors[i];
                if (anchor == null)
                    continue;
                float dx = anchor.position.x - _anchorPositions[i].x;
                float dz = anchor.position.z - _anchorPositions[i].z;
                if (dx * dx + dz * dz > 1e-4f)   // XZ seulement : seule la position horizontale sert au placement
                {
                    SetDirty();
                    return;
                }
            }
        }

        /// <summary>
        /// Profondeur (m) de la touffe dans la couche d'algues, sous la surface, en plus de Float Offset.
        /// Entre 0 et Layer Thickness ; même valeur que celle utilisée par le GPU (utile au gameplay).
        /// </summary>
        public float GetLayerDepth(SargassumPlacement placement) => layerThickness * LayerDepthFraction(placement.seed);

        /// <summary>
        /// 0..1 dérivé de la graine par hachage entier : décorrélé de la teinte et de la phase, qui utilisent la graine
        /// telle quelle. Aucun tirage aléatoire en plus : le placement des scènes existantes ne bouge pas.
        /// </summary>
        static float LayerDepthFraction(float seed)
        {
            uint h = (uint)(Mathf.Clamp01(seed) * 16777215f);
            h ^= h >> 16;
            h *= 0x7feb352dU;
            h ^= h >> 15;
            h *= 0x846ca68bU;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777215f;
        }

        protected override IReadOnlyList<InstanceVariant> GetVariants() => variants;

        protected override Bounds BuildInstances(List<GpuInstanceData> output)
        {
            IReadOnlyList<SargassumPlacement> list = placements;
            if (source == PlacementSource.Procedural)
            {
                GeneratePlacements(_generated);
                list = _generated;
            }

            if (list.Count == 0)
                return new Bounds(transform.position, Vector3.one);

            int variantCount = Mathf.Max(variants.Count, 1);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            float maxScale = 0f;

            for (int i = 0; i < list.Count; i++)
            {
                SargassumPlacement p = list[i];
                // Matrice de repos : lacet + échelle, translation XZ. La hauteur et l'inclinaison viennent des vagues (GPU).
                Matrix4x4 rest = Matrix4x4.TRS(new Vector3(p.position.x, 0f, p.position.y), Quaternion.Euler(0f, p.yaw, 0f), Vector3.one * p.scale);
                GpuInstanceData data = GpuInstanceData.FromMatrix(rest, Mathf.Clamp(p.variant, 0, variantCount - 1), p.seed);
                data.custom.y = GetLayerDepth(p);
                output.Add(data);
                min = Vector2.Min(min, p.position);
                max = Vector2.Max(max, p.position);
                maxScale = Mathf.Max(maxScale, Mathf.Abs(p.scale));
            }

            // Bounds du draw : toutes les positions possibles (taille des touffes + houle + dérive + dandinement + couche, vers le bas)
            float meshRadius = (MaxVariantRadius() + boundsPadding) * maxScale;
            float horizontal = meshRadius + OceanWaves.MaxHorizontalDisplacement + wanderRadius;
            float vertical = meshRadius + OceanWaves.MaxVerticalDisplacement + bobAmplitude + Mathf.Abs(floatOffset);
            if (AnyVariantConformsToWaves())
                vertical += OceanWaves.MaxVerticalDisplacement;   // écart vague au sommet / vague au pivot
            float waterLevel = OceanWaves.WaterLevel;
            var bounds = new Bounds();
            bounds.SetMinMax(
                new Vector3(min.x - horizontal, waterLevel - vertical - layerThickness, min.y - horizontal),
                new Vector3(max.x + horizontal, waterLevel + vertical, max.y + horizontal));
            return bounds;
        }

        protected override void SetSystemParameters(ComputeShader shader, Camera camera)
        {
            OceanWaves.ApplyTo(shader);
            shader.SetVector(SargassumFloatId, new Vector4(floatOffset, bobAmplitude, bobSpeed, tiltFollow));
            shader.SetVector(SargassumWanderId, new Vector4(wanderRadius, wanderSpeed, 0f, 0f));
        }

        /// <summary>Avec « Épouser les vagues », un sommet peut s'écarter de pente × rayon : la boîte de culling doit le couvrir.</summary>
        protected override float GetBoundsPadding(InstanceVariant variant, Bounds localBounds)
        {
            float padding = base.GetBoundsPadding(variant, localBounds);
            if (ConformsToWaves(variant))
                padding += OceanWaves.MaxSlope * localBounds.extents.magnitude;
            return padding;
        }

        static bool ConformsToWaves(InstanceVariant variant) =>
            variant?.material != null && variant.material.IsKeywordEnabled(ConformKeyword);

        bool AnyVariantConformsToWaves()
        {
            foreach (InstanceVariant variant in variants)
            {
                if (ConformsToWaves(variant))
                    return true;
            }
            return false;
        }

        float MaxVariantRadius()
        {
            float radius = 0.5f;
            foreach (InstanceVariant variant in variants)
            {
                if (variant?.lodMeshes == null)
                    continue;
                foreach (Mesh mesh in variant.lodMeshes)
                {
                    if (mesh != null)
                        radius = Mathf.Max(radius, mesh.bounds.center.magnitude + mesh.bounds.extents.magnitude);
                }
            }
            return radius;
        }

        void GeneratePlacements(List<SargassumPlacement> output)
        {
            output.Clear();
            var random = new System.Random(randomSeed);
            int variantCount = Mathf.Max(variants.Count, 1);
            Vector3 center = transform.position;

            // Grille perturbée : 1 touffe par cellule, position aléatoire dans la cellule. O(n), sans amas ni trous.
            if (densityPer100m2 > 0f && areaSize.x > 0f && areaSize.y > 0f)
            {
                float cell = Mathf.Sqrt(100f / densityPer100m2);
                int cellsX = Mathf.Max(1, Mathf.RoundToInt(areaSize.x / cell));
                int cellsZ = Mathf.Max(1, Mathf.RoundToInt(areaSize.y / cell));
                if ((long)cellsX * cellsZ > MaxGeneratedInstances)
                {
                    Debug.LogWarning($"{name} : {(long)cellsX * cellsZ} touffes demandées, limité à {MaxGeneratedInstances}.", this);
                    float reduction = Mathf.Sqrt((float)MaxGeneratedInstances / ((long)cellsX * cellsZ));
                    cellsX = Mathf.Max(1, (int)(cellsX * reduction));
                    cellsZ = Mathf.Max(1, (int)(cellsZ * reduction));
                }

                float stepX = areaSize.x / cellsX;
                float stepZ = areaSize.y / cellsZ;
                float x0 = center.x - areaSize.x * 0.5f;
                float z0 = center.z - areaSize.y * 0.5f;
                for (int z = 0; z < cellsZ; z++)
                {
                    for (int x = 0; x < cellsX; x++)
                    {
                        var position = new Vector2(x0 + (x + Next(random)) * stepX, z0 + (z + Next(random)) * stepZ);
                        output.Add(RandomPlacement(random, position, variantCount));
                    }
                }
            }

            // Amas autour des ancres (mines et leurres), répartition uniforme sur le disque
            _anchorPositions.Clear();
            foreach (Transform anchor in clusterAnchors)
            {
                _anchorPositions.Add(anchor != null ? anchor.position : Vector3.zero);
                if (anchor == null)
                    continue;
                var anchorXZ = new Vector2(anchor.position.x, anchor.position.z);
                for (int i = 0; i < clumpsPerCluster; i++)
                {
                    float angle = Next(random) * Mathf.PI * 2f;
                    float distance = clusterRadius * Mathf.Sqrt(Next(random));
                    var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    output.Add(RandomPlacement(random, anchorXZ + offset, variantCount));
                }
            }
        }

        SargassumPlacement RandomPlacement(System.Random random, Vector2 position, int variantCount)
        {
            return new SargassumPlacement
            {
                position = position,
                yaw = Next(random) * 360f,
                scale = Mathf.Lerp(scaleRange.x, scaleRange.y, Next(random)),
                variant = random.Next(variantCount),
                seed = Next(random)
            };
        }

        static float Next(System.Random random) => (float)random.NextDouble();
    }
}
