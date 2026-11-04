using System;
using System.Collections.Generic;
using Unity.Collections;
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
    /// sont des fonctions du temps évaluées sur GPU : aucune donnée n'est renvoyée par le CPU à chaque frame.
    ///  - Émetteurs : colonnes en boucle infinie (évents, fuites, ambiance). Coût CPU par frame : 0.
    ///  - Emit()    : bulles ponctuelles (respiration de plongeur, hélice, impact), tampon circulaire.
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

        /// <summary>Zone couverte par les émissions ponctuelles d'une demi-seconde, jusqu'à éclatement de leur dernière bulle.</summary>
        struct BurstZone
        {
            public Bounds bounds;
            public float createdAt;
            public float expiresAt;
        }

        const float BurstZoneMergeWindow = 0.5f;

        readonly List<Vector3> _emitterPositions = new List<Vector3>();
        readonly List<BurstZone> _burstZones = new List<BurstZone>();
        GpuInstanceData[] _burst = Array.Empty<GpuInstanceData>();
        int _burstCursor;
        int _ambientCount;
        System.Random _emitRandom;

        protected override string CullingShaderResource => "NyxGpuInstancing/BubbleColumns";

        public override bool UsesPlacement => false;

        public IList<BubbleEmitter> Emitters => emitters;

        /// <summary>
        /// Émet 'count' bulles autour de 'position' (dans un rayon 'spread'). Sans effet hors de l'eau.
        /// Exemple : expiration d'un plongeur toutes les 4 s -> Emit(detendeur.position, 12, 0.03f).
        /// </summary>
        public void Emit(Vector3 position, int count, float spread = 0.05f)
        {
            float waterLevel = OceanWaves.WaterLevel;
            float travel = waterLevel - position.y;
            if (count <= 0 || burstCapacity <= 0 || travel <= 0.01f)
                return;

            if (EnsureBurstBuffer())
                SetDirty();   // la taille du buffer GPU change : reconstruction au prochain rendu
            if (_emitRandom == null)
                _emitRandom = new System.Random(randomSeed ^ 0x5bd1e995);

            BubbleEmitter settings = burstSettings ?? new BubbleEmitter();
            float now = MeshSpawner.CurrentTime;
            float margin = travel + spread + OceanWaves.MaxVerticalDisplacement;
            count = Mathf.Min(count, burstCapacity);
            int first = _burstCursor;
            float longestLife = 0f;

            for (int i = 0; i < count; i++)
            {
                float speed = Mathf.Max(Lerp(settings.riseSpeed, Next(_emitRandom)), 0.01f);
                Vector2 offset = RandomInDisk(_emitRandom) * spread;
                var spawn = new Vector3(position.x + offset.x, position.y + (Next(_emitRandom) - 0.5f) * spread, position.z + offset.y);
                float lifetime = margin / speed;
                longestLife = Mathf.Max(longestLife, lifetime);
                _burst[_burstCursor] = MakeBubble(
                    spawn, now, Lerp(settings.surfaceRadius, Next(_emitRandom)), speed, lifetime, false,
                    settings.spiralRadius, settings.spiralFrequency * Mathf.Lerp(0.7f, 1.3f, Next(_emitRandom)), Next(_emitRandom));
                _burstCursor = (_burstCursor + 1) % burstCapacity;
            }

            // Bounds du draw : colonne de l'émission jusqu'à la surface, oubliée quand ses bulles ont éclaté.
            // Les émissions rapprochées (< 0,5 s) partagent une zone : quelques dizaines de zones au plus.
            float side = 2f * (spread + settings.spiralRadius + settings.surfaceRadius.y * 2f + 0.05f);
            var column = new Bounds(
                new Vector3(position.x, (position.y + waterLevel) * 0.5f, position.z),
                new Vector3(side, travel + 2f * spread + 2f * OceanWaves.MaxVerticalDisplacement + 0.2f, side));
            int last = _burstZones.Count - 1;
            if (last >= 0 && now - _burstZones[last].createdAt < BurstZoneMergeWindow)
            {
                BurstZone zone = _burstZones[last];
                zone.bounds.Encapsulate(column);
                zone.expiresAt = Mathf.Max(zone.expiresAt, now + longestLife);
                _burstZones[last] = zone;
            }
            else
            {
                _burstZones.Add(new BurstZone { bounds = column, createdAt = now, expiresAt = now + longestLife });
            }
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
                if (EmitsBubbles(emitter, waterLevel))
                    count += emitter.bubbleCount;
            }
            return count + burstCapacity;
        }

        public override Bounds BuildInstances(NativeArray<GpuInstanceData> output)
        {
            var random = new System.Random(randomSeed);
            float waterLevel = OceanWaves.WaterLevel;
            float waveMargin = OceanWaves.MaxVerticalDisplacement;
            var bounds = new Bounds(transform.position, Vector3.zero);
            int written = 0;

            // 1. Colonnes en boucle : chaque bulle a une phase aléatoire, la colonne est pleine dès t = 0
            _emitterPositions.Clear();
            foreach (BubbleEmitter emitter in emitters)
            {
                Vector3 origin = emitter != null ? EmitterPosition(emitter) : Vector3.zero;
                _emitterPositions.Add(origin);
                if (!EmitsBubbles(emitter, waterLevel))
                    continue;
                float travel = waterLevel - origin.y;

                for (int i = 0; i < emitter.bubbleCount; i++)
                {
                    float speed = Mathf.Max(Lerp(emitter.riseSpeed, Next(random)), 0.01f);
                    float period = (travel + waveMargin) / speed;
                    Vector2 offset = RandomInDisk(random) * emitter.radius;
                    var spawn = new Vector3(origin.x + offset.x, origin.y, origin.z + offset.y);
                    output[written++] = MakeBubble(
                        spawn, -Next(random) * period, Lerp(emitter.surfaceRadius, Next(random)), speed, period, true,
                        emitter.spiralRadius, emitter.spiralFrequency * Mathf.Lerp(0.7f, 1.3f, Next(random)), Next(random));
                }

                float side = 2f * (emitter.radius + emitter.spiralRadius + emitter.surfaceRadius.y * 2f);
                bounds.Encapsulate(new Bounds(
                    new Vector3(origin.x, (origin.y + waterLevel) * 0.5f, origin.z),
                    new Vector3(side, travel + 2f * waveMargin, side)));
            }
            _ambientCount = written;

            // 2. Tampon circulaire des émissions ponctuelles (conservé d'une reconstruction à l'autre ;
            //    ses bounds sont gérées à part, par zones qui expirent : voir RefreshBurstBounds)
            EnsureBurstBuffer();
            NativeArray<GpuInstanceData>.Copy(_burst, 0, output, written, _burst.Length);
            return bounds;
        }

        bool EmitsBubbles(BubbleEmitter emitter, float waterLevel) =>
            emitter != null && emitter.bubbleCount > 0 && waterLevel - EmitterPosition(emitter).y > 0.05f;

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
            _burstZones.Clear();
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
            if (_burstZones.Count > 0)
                RefreshBurstBounds(MeshSpawner.CurrentTime);
        }

        /// <summary>Oublie les zones dont toutes les bulles ont éclaté et recalcule les bounds dynamiques du draw.</summary>
        void RefreshBurstBounds(float now)
        {
            for (int i = _burstZones.Count - 1; i >= 0; i--)   // boucle manuelle : pas d'allocation par frame
            {
                if (_burstZones[i].expiresAt < now)
                    _burstZones.RemoveAt(i);
            }
            if (Spawner == null)
                return;
            if (_burstZones.Count == 0)
            {
                Spawner.SetDynamicBounds(null);
                return;
            }
            Bounds union = _burstZones[0].bounds;
            for (int i = 1; i < _burstZones.Count; i++)
                union.Encapsulate(_burstZones[i].bounds);
            Spawner.SetDynamicBounds(union);
        }

        /// <summary>Adapte le tampon circulaire à burstCapacity. Renvoie true s'il a été redimensionné.</summary>
        bool EnsureBurstBuffer()
        {
            if (_burst.Length == burstCapacity)
                return false;
            var resized = new GpuInstanceData[burstCapacity];
            for (int i = 0; i < resized.Length; i++)
                resized[i] = i < _burst.Length ? _burst[i] : DeadBubble();
            _burst = resized;
            _burstCursor = 0;
            return true;
        }

        /// <summary>Cases du tampon -> GPU. Si le spawner n'est pas à jour, la prochaine reconstruction reprendra _burst tel quel.</summary>
        void UploadBurst(int burstIndex, int count)
        {
            if (Spawner != null)
                Spawner.UploadInstances(_ambientCount + burstIndex, _burst, burstIndex, count);
        }

        Vector3 EmitterPosition(BubbleEmitter emitter) =>
            emitter.anchor != null ? emitter.anchor.TransformPoint(emitter.localPosition) : transform.TransformPoint(emitter.localPosition);

        static GpuInstanceData MakeBubble(Vector3 spawn, float spawnTime, float surfaceRadius, float speed, float lifetime,
                                          bool loop, float spiralRadius, float spiralFrequency, float seed)
        {
            return new GpuInstanceData
            {
                row0 = new Vector4(spawn.x, spawn.y, spawn.z, spawnTime),
                row1 = new Vector4(surfaceRadius, speed, lifetime, loop ? 1f : 0f),
                row2 = new Vector4(spiralRadius, spiralFrequency, seed, 0f),
                custom = Vector4.zero   // variante 0
            };
        }

        /// <summary>Case libre du tampon : durée de vie nulle, jamais dessinée.</summary>
        static GpuInstanceData DeadBubble() => MakeBubble(Vector3.zero, -1e6f, 0f, 0f, 0f, false, 0f, 0f, 0f);

        static Vector2 RandomInDisk(System.Random random)
        {
            float angle = Next(random) * Mathf.PI * 2f;
            float distance = Mathf.Sqrt(Next(random));
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        }

        static float Lerp(Vector2 range, float t) => Mathf.Lerp(range.x, range.y, t);

        static float Next(System.Random random) => (float)random.NextDouble();
    }
}
