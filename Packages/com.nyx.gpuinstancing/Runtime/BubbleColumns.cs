using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>Source de bulles : colonne en boucle (émetteur) ou réglages des émissions ponctuelles.</summary>
    [Serializable]
    public class BubbleEmitter
    {
        [Tooltip("Optionnel : repère du point d'émission (lu à la construction ; en jeu, appeler SetDirty() après l'avoir déplacé). " +
                 "Sinon : Local Position, relative à cet objet. Pour une source mobile (plongeur, drone), utiliser Emit().")]
        public Transform anchor;
        public Vector3 localPosition;
        [Tooltip("Rayon horizontal de la zone d'émission (m).")]
        [Min(0f)] public float radius = 0.15f;
        [Tooltip("Nombre de bulles en vol en même temps pour cet émetteur.")]
        [Min(0)] public int bubbleCount = 40;
        [Tooltip("Rayon d'une bulle arrivée en surface (m). Plus petite au fond (loi de Boyle).")]
        public Vector2 surfaceRadius = new Vector2(0.004f, 0.012f);
        [Tooltip("Vitesse de montée (m/s). Bulles de quelques mm : environ 0,2 à 0,35 m/s.")]
        public Vector2 riseSpeed = new Vector2(0.22f, 0.32f);
        [Tooltip("Rayon de la trajectoire en spirale (m).")]
        [Min(0f)] public float spiralRadius = 0.015f;
        [Tooltip("Tours de spirale par seconde.")]
        [Min(0f)] public float spiralFrequency = 1.2f;
    }

    /// <summary>
    /// Comportement : bulles sous-marines. Position, taille (loi de Boyle), spirale, oscillation de forme et éclatement
    /// sont des fonctions du temps évaluées sur GPU (BubbleColumns.compute) : aucune donnée n'est renvoyée par le CPU à
    /// chaque frame.
    ///  - Émetteurs : colonnes en boucle infinie (évents, fuites, ambiance). Coût CPU par frame : 0.
    ///                Construites à la reconstruction sur GPU (<see cref="BubbleColumnBuilder"/>, une graine par bulle) : le
    ///                CPU n'envoie que la description des colonnes.
    ///  - Emit()    : bulles ponctuelles (respiration de plongeur, hélice, impact), tampon circulaire (<see cref="BubbleBursts"/>).
    ///                Coût : upload de count × 64 octets au moment de l'appel.
    /// N'utilise pas le placement du MeshSpawner. Matériau : Nyx/GPU Instancing/Bubble (variante 0).
    /// </summary>
    [AddComponentMenu("Nyx/GPU Instancing/Behaviors/Bubble Columns")]
    public sealed class BubbleColumns : MeshSpawnerBehavior
    {
        [Header("Émetteurs en boucle")]
        [SerializeField] List<BubbleEmitter> emitters = new List<BubbleEmitter> { new BubbleEmitter() };

        [Header("Émissions ponctuelles (Emit)")]
        [Tooltip("Taille du tampon circulaire = nombre max de bulles ponctuelles en vol. Les plus anciennes sont recyclées.")]
        [SerializeField, Min(0)] int burstCapacity = 1024;
        [Tooltip("Tailles, vitesses et spirale utilisées par Emit() (anchor, position, rayon et nombre ignorés).")]
        [SerializeField] BubbleEmitter burstSettings = new BubbleEmitter();

        [Header("Forme et animation")]
        [SerializeField, Range(0f, 0.5f)] float maxFlatness = 0.3f;
        [Tooltip("Rayon (m) à partir duquel la bulle est franchement aplatie.")]
        [SerializeField, Min(0.0001f)] float flatnessRadius = 0.01f;
        [SerializeField, Min(0f)] float shapeWobbleSpeed = 14f;
        [Tooltip("Distance sous la surface sur laquelle la bulle éclate (m).")]
        [SerializeField, Min(0.001f)] float popDistance = 0.04f;
        [Tooltip("Durée d'apparition (s).")]
        [SerializeField, Min(0.001f)] float growInTime = 0.12f;
        [SerializeField] int randomSeed = 4321;

        static readonly int BubbleShapeId = Shader.PropertyToID("_BubbleShape");
        static readonly int BubbleTimingId = Shader.PropertyToID("_BubbleTiming");

        readonly BubbleBursts _bursts = new BubbleBursts();
        readonly List<Vector3> _emitterPositions = new List<Vector3>();
        int _ambientCount;

        protected override string CullingShaderResource => "NyxGpuInstancing/BubbleColumns";

        public override bool UsesPlacement => false;

        /// <summary>Les colonnes montent jusqu'à la surface et bouclent : un volume qui ne les englobe pas les détruirait toutes.</summary>
        public override bool ClipToVolumeByDefault => false;

        public IList<BubbleEmitter> Emitters => emitters;

        /// <summary>
        /// Émet 'count' bulles autour de 'position' (dans un rayon 'spread'). Sans effet hors de l'eau.
        /// Exemple : expiration d'un plongeur toutes les 4 s -> Emit(detendeur.position, 12, 0.03f).
        /// </summary>
        public void Emit(Vector3 position, int count, float spread = 0.05f)
        {
            float waterLevel = OceanWaves.WaterLevel;
            if (count <= 0 || burstCapacity <= 0 || waterLevel - position.y <= 0.01f)
                return;

            if (_bursts.Resize(burstCapacity))
                SetDirty();   // la taille du buffer GPU change : reconstruction au prochain rendu
            float now = MeshSpawner.CurrentTime;
            count = Mathf.Min(count, burstCapacity);
            int first = _bursts.Emit(position, count, spread, burstSettings ?? new BubbleEmitter(), randomSeed, now, waterLevel,
                                     OceanWaves.MaxVerticalDisplacement);
            RefreshBurstBounds(now);

            // Seules les cases modifiées partent au GPU (2 plages si le tampon circulaire boucle)
            int firstPart = Mathf.Min(count, burstCapacity - first);
            UploadBurst(first, firstPart);
            if (count > firstPart)
                UploadBurst(0, count - firstPart);
        }

        /// <summary>Bulles des colonnes valides (sous l'eau) + tampon des émissions ponctuelles.</summary>
        public override int GetInstanceCount()
        {
            int count = 0;
            float waterLevel = OceanWaves.WaterLevel;
            foreach (BubbleEmitter emitter in emitters)
            {
                if (emitter != null && EmitsBubbles(emitter, EmitterPosition(emitter), waterLevel))
                    count += emitter.bubbleCount;
            }
            return count + burstCapacity;
        }

        /// <summary>
        /// Colonnes en boucle construites sur GPU (BubbleColumnBuilder) directement dans le buffer d'instances : le CPU lit les
        /// positions des émetteurs (Transforms) et n'envoie que la description des colonnes. Chaque bulle a une phase
        /// aléatoire : la colonne est pleine dès t = 0. Puis le tampon des émissions ponctuelles (conservé d'une reconstruction
        /// à l'autre ; ses bounds sont gérées à part, voir RefreshBurstBounds).
        /// </summary>
        public override bool BuildInstancesOnGpu(ComputeBuffer instances, int count, out Bounds bounds)
        {
            bounds = new Bounds(transform.position, Vector3.zero);
            if (!BubbleColumnBuilder.LoadShader())
            {
                Debug.LogError($"{name} : compute shader {BubbleColumnBuilder.ShaderResource} introuvable ou invalide (dossier Resources du package).", this);
                return false;
            }

            float waterLevel = OceanWaves.WaterLevel;
            float waveMargin = OceanWaves.MaxVerticalDisplacement;
            var columns = new BubbleColumnBuilder.Column[emitters.Count];
            int columnCount = 0;
            int written = 0;
            _emitterPositions.Clear();
            for (int e = 0; e < emitters.Count; e++)
            {
                BubbleEmitter emitter = emitters[e];
                Vector3 origin = emitter != null ? EmitterPosition(emitter) : Vector3.zero;
                _emitterPositions.Add(origin);
                if (emitter == null || !EmitsBubbles(emitter, origin, waterLevel))
                    continue;
                float travel = waterLevel - origin.y;
                columns[columnCount++] = new BubbleColumnBuilder.Column
                {
                    origin = origin,
                    radius = emitter.radius,
                    surfaceRadius = emitter.surfaceRadius,
                    riseSpeed = emitter.riseSpeed,
                    spiralRadius = emitter.spiralRadius,
                    spiralFrequency = emitter.spiralFrequency,
                    travel = travel + waveMargin,
                    firstInstance = written,
                    emitterIndex = (uint)e
                };
                written += emitter.bubbleCount;

                float side = 2f * (emitter.radius + emitter.spiralRadius + emitter.surfaceRadius.y * 2f);
                bounds.Encapsulate(new Bounds(
                    new Vector3(origin.x, (origin.y + waterLevel) * 0.5f, origin.z),
                    new Vector3(side, travel + 2f * waveMargin, side)));
            }
            _ambientCount = written;
            _bursts.Resize(burstCapacity);
            if (written + _bursts.Capacity != count)
                Debug.LogWarning($"{name} : {written + _bursts.Capacity} bulles construites pour {count} annoncées (émetteur déplacé entre les deux ?).", this);

            if (written > 0 && written <= count)
                BubbleColumnBuilder.Build(instances, columns, columnCount, written, randomSeed);
            int burst = Mathf.Min(_bursts.Capacity, count - written);
            if (burst > 0)
                instances.SetData(_bursts.Buffer, 0, written, burst);
            return true;
        }

        static bool EmitsBubbles(BubbleEmitter emitter, Vector3 origin, float waterLevel) =>
            emitter.bubbleCount > 0 && waterLevel - origin.y > 0.05f;

        public override void SetShaderParameters(ComputeShader shader, Camera camera)
        {
            OceanWaves.ApplyTo(shader);
            shader.SetVector(BubbleShapeId, new Vector4(maxFlatness, flatnessRadius, shapeWobbleSpeed, popDistance));
            shader.SetVector(BubbleTimingId, new Vector4(growInTime, 0f, 0f, 0f));
        }

#if UNITY_EDITOR
        public override List<InstanceVariant> CreateDefaultVariants() => NyxDefaultAssets.BubbleVariants();
#endif

        protected override void OnDisable()
        {
            base.OnDisable();
            _bursts.ClearZones();
            if (Spawner != null)
                Spawner.SetDynamicBounds(null);
        }

        void Update()
        {
            // Mode édition uniquement : les colonnes suivent les émetteurs pendant qu'on les place.
            // En jeu, une reconstruction change la période des bulles et les ferait sauter : SetDirty() explicite,
            // et Emit() / BubbleBurstEmitter pour toute source mobile.
            if (Application.isPlaying)
                return;
            for (int e = 0; e < emitters.Count && e < _emitterPositions.Count; e++)
            {
                BubbleEmitter emitter = emitters[e];
                if (emitter != null && (EmitterPosition(emitter) - _emitterPositions[e]).sqrMagnitude > 1e-6f)
                {
                    SetDirty();
                    return;
                }
            }
        }

        void LateUpdate()
        {
            if (_bursts.HasZones)
                RefreshBurstBounds(MeshSpawner.CurrentTime);
        }

        /// <summary>Oublie les émissions dont toutes les bulles ont éclaté et donne au spawner les bounds de celles en vol.</summary>
        void RefreshBurstBounds(float now)
        {
            Bounds? bounds = _bursts.Bounds(now);
            if (Spawner != null)
                Spawner.SetDynamicBounds(bounds);
        }

        /// <summary>Cases du tampon -> GPU. Si le spawner n'est pas à jour, la prochaine reconstruction reprendra le tampon tel quel.</summary>
        void UploadBurst(int burstIndex, int count)
        {
            if (Spawner != null)
                Spawner.UploadInstances(_ambientCount + burstIndex, _bursts.Buffer, burstIndex, count);
        }

        Vector3 EmitterPosition(BubbleEmitter emitter) =>
            emitter.anchor != null ? emitter.anchor.TransformPoint(emitter.localPosition) : transform.TransformPoint(emitter.localPosition);
    }
}
