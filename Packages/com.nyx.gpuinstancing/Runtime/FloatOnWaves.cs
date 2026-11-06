using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Comportement : les instances flottent sur la houle d'<see cref="OceanWaves"/> (sargasses, débris, bouées).
    /// Flottaison, inclinaison sur la pente, dandinement, dérive et profondeur dans la couche calculés sur GPU (instances
    /// par défaut, construites sur GPU) ; la hauteur des placements est ignorée (elle vient de l'eau). Changer un réglage
    /// ne demande que de nouveaux paramètres au compute shader. Matériau conseillé : Nyx/GPU Instancing/Sargassum.
    /// </summary>
    [AddComponentMenu("Nyx/GPU Instancing/Behaviors/Float On Waves")]
    public sealed class FloatOnWaves : MeshSpawnerBehavior
    {
        const string ConformKeyword = "_NYX_CONFORM_WAVES";

        [Tooltip("Décalage vertical par rapport à la surface (m). Négatif = partiellement immergé.")]
        [SerializeField] float floatOffset = -0.02f;
        [Tooltip("Épaisseur de la couche (m). 0 = une seule instance d'épaisseur, en surface. Sinon chaque instance flotte " +
                 "à une profondeur fixe entre 0 et cette valeur (en plus de Float Offset), dérivée de sa graine : déterministe, " +
                 "valable aussi pour les placements figés ou fournis par SetPlacements.")]
        [SerializeField, Min(0f)] float layerThickness;
        [Tooltip("Dandinement propre à chaque instance, en plus des vagues (m).")]
        [SerializeField, Min(0f)] float bobAmplitude = 0.02f;
        [SerializeField, Min(0f)] float bobSpeed = 1.3f;
        [Tooltip("0 = reste horizontale, 1 = suit exactement la pente de la vague.")]
        [SerializeField, Range(0f, 1f)] float tiltFollow = 0.85f;
        [Tooltip("Rayon de dérive autour de la position de repos (m) : vivant, mais une mine en dessous reste couverte.")]
        [SerializeField, Min(0f)] float wanderRadius = 0.2f;
        [SerializeField, Min(0f)] float wanderSpeed = 0.15f;

        static readonly int FloatId = Shader.PropertyToID("_SargassumFloat");
        static readonly int WanderId = Shader.PropertyToID("_SargassumWander");

        protected override string CullingShaderResource => "NyxGpuInstancing/FloatOnWaves";

        /// <summary>
        /// Profondeur (m) de l'instance dans la couche, sous la surface, en plus de Float Offset.
        /// Entre 0 et Layer Thickness ; même valeur que celle utilisée par le GPU (utile au gameplay).
        /// </summary>
        public float GetLayerDepth(MeshPlacement placement) => layerThickness * LayerDepthFraction(placement.seed);

        /// <summary>Toutes les positions possibles : taille des instances + houle + dérive + dandinement + couche (vers le bas).</summary>
        public override Bounds GetPlacementBounds(Bounds positions, float meshRadius)
        {
            float horizontal = meshRadius + OceanWaves.MaxHorizontalDisplacement + wanderRadius;
            float vertical = meshRadius + OceanWaves.MaxVerticalDisplacement + bobAmplitude + Mathf.Abs(floatOffset);
            if (AnyVariantConformsToWaves())
                vertical += OceanWaves.MaxVerticalDisplacement;   // écart vague au sommet / vague au pivot
            float waterLevel = OceanWaves.WaterLevel;
            var bounds = new Bounds();
            bounds.SetMinMax(
                new Vector3(positions.min.x - horizontal, waterLevel - vertical - layerThickness, positions.min.z - horizontal),
                new Vector3(positions.max.x + horizontal, waterLevel + vertical, positions.max.z + horizontal));
            return bounds;
        }

        public override void SetShaderParameters(ComputeShader shader, Camera camera)
        {
            OceanWaves.ApplyTo(shader);
            shader.SetVector(FloatId, new Vector4(floatOffset, bobAmplitude, bobSpeed, tiltFollow));
            shader.SetVector(WanderId, new Vector4(wanderRadius, wanderSpeed, layerThickness, 0f));
        }

        /// <summary>Avec « Épouser les vagues », un sommet peut s'écarter de pente × rayon : la boîte de culling doit le couvrir.</summary>
        public override float GetBoundsPadding(InstanceVariant variant, Bounds localBounds, float defaultPadding)
        {
            float padding = defaultPadding;
            if (ConformsToWaves(variant))
                padding += OceanWaves.MaxSlope * localBounds.extents.magnitude;
            return padding;
        }

        /// <summary>
        /// Pose le volume de la zone sur l'eau sans toucher au polygone : face du haut au-dessus des crêtes (+ 0,5 m), fond
        /// sous les creux et la couche d'algues (+ 1,5 m). Sinon les touffes soulevées par la houle sortiraient du volume et
        /// seraient détruites. La hauteur de la face du haut n'influence pas les touffes (elle vient de l'eau).
        /// </summary>
        public void FitZoneToWater()
        {
            MeshSpawner spawner = Spawner;
            if (spawner == null)
                return;
            Bounds motion = GetPlacementBounds(new Bounds(spawner.ZoneCenter, Vector3.zero), 0f);
            float top = motion.max.y + 0.5f;
            float bottom = motion.min.y - 1.5f;
            Vector3 offset = spawner.ZoneCenterOffset;
            offset.y = top - spawner.transform.position.y;
            spawner.SetZone(offset, spawner.ZonePoints, top - bottom);
        }

#if UNITY_EDITOR
        public override List<InstanceVariant> CreateDefaultVariants() => NyxDefaultAssets.SargassumVariants();

        protected override void Reset()
        {
            base.Reset();
            if (Spawner == null)
                return;
            UnityEditor.Undo.RecordObject(Spawner, "Zone sur l'eau");
            FitZoneToWater();
            UnityEditor.EditorUtility.SetDirty(Spawner);
        }
#endif

        static bool ConformsToWaves(InstanceVariant variant) =>
            variant?.material != null && variant.material.IsKeywordEnabled(ConformKeyword);

        bool AnyVariantConformsToWaves()
        {
            MeshSpawner spawner = Spawner;
            if (spawner == null)
                return false;
            foreach (InstanceVariant variant in spawner.Variants)
            {
                if (ConformsToWaves(variant))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 0..1 dérivé de la graine par hachage entier : décorrélé de la teinte et de la phase, qui utilisent la graine
        /// telle quelle. Aucun tirage aléatoire en plus : le placement des scènes existantes ne bouge pas.
        /// </summary>
        static float LayerDepthFraction(float seed)
        {
            uint h = (uint)(math.saturate(seed) * 16777215f);
            h ^= h >> 16;
            h *= 0x7feb352dU;
            h ^= h >> 15;
            h *= 0x846ca68bU;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777215f;
        }
    }
}
