using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Nyx.GpuInstancing
{
    /// <summary>Placement d'une instance (position monde au repos, lacet, échelle, variante, graine).</summary>
    [Serializable]
    public struct MeshPlacement
    {
        [Tooltip("Position monde au repos. Un comportement peut en ignorer une partie (FloatOnWaves : la hauteur vient de l'eau).")]
        public Vector3 position;
        [Tooltip("Rotation autour de Y (degrés).")]
        public float yaw;
        public float scale;
        [Tooltip("Index dans la liste Variants.")]
        public int variant;
        [Range(0f, 1f)] public float seed;
    }

    /// <summary>
    /// Instanciateur GPU (Unity 2021.3, pipeline Built-in) : des milliers de copies de meshes, culling GPU par caméra,
    /// 1 Graphics.DrawMeshInstancedIndirect par (variante × LOD), quel que soit le nombre d'instances.
    ///
    /// Le mouvement des instances vient d'un composant <see cref="MeshSpawnerBehavior"/> sur le même objet
    /// (FloatOnWaves, BubbleColumns, StaticInstances, ou le vôtre) : il fournit le compute shader qui calcule la
    /// position finale de chaque instance, ses paramètres, et éventuellement ses propres instances.
    /// Le MeshSpawner gère le reste : meshes et LODs, placement (zone, amas, liste), culling (frustum des deux yeux,
    /// distance, petits objets), buffers, VR, ombres.
    ///
    /// Construction (MeshSpawnerGpuBuild.cs) : sur GPU, par compute shader. Placement (grille, amas, liste), filtrage par la
    /// zone et données d'instance sont écrits directement dans le buffer d'instances ; le CPU ne prépare que quelques
    /// paramètres et lit quelques compteurs (taille des buffers, boîte des positions). Un comportement qui écrit ses
    /// instances en C# reçoit les placements lus sur GPU, et ses instances sont envoyées ensuite.
    ///
    /// Mémoire :
    ///  - CPU : rien de proportionnel au nombre d'instances n'est retenu. Les placements ne sont lus (28 o chacun, mémoire
    ///    native) qu'à la demande (Placements, figer le placement) ou pour un comportement qui écrit ses instances en C#.
    ///  - GPU : instances (64 o chacune) + un jeu de buffers de culling partagé par les caméras rendues l'une après
    ///    l'autre (visibles × 64 o + index par draw × 4 o). Une caméra rendue PENDANT le rendu d'une autre (réflexion
    ///    d'eau) prend un second jeu : les draws de la première gardent leurs données. Pendant la reconstruction seulement :
    ///    masque des candidats (1 bit chacun) et, pour une liste, ses placements (28 o chacun).
    ///  - Instances écrites en C# : N × 64 o en mémoire native le temps de l'envoi (ComputeBuffer.SetData).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Nyx/GPU Instancing/Mesh Spawner")]
    public sealed partial class MeshSpawner : MonoBehaviour
    {
        public enum PlacementSource
        {
            /// <summary>Généré à partir des paramètres + graine (déterministe, rien de sérialisé dans la scène).</summary>
            Procedural,
            /// <summary>Liste explicite (SetPlacements, ou menu contextuel « Figer le placement »).</summary>
            List
        }

        /// <summary>Origine des instances d'une reconstruction (diagnostic).</summary>
        public enum UploadMethod
        {
            None,
            /// <summary>Écrites en C# (comportement qui surcharge CreateInstance(s) ou BuildInstances), envoyées par ComputeBuffer.SetData.</summary>
            SetData,
            /// <summary>Construites sur GPU, directement dans le buffer d'instances : rien n'est envoyé.</summary>
            BuiltOnGpu
        }

        public const int MaxLods = 4;
        public const int MaxBuckets = 64;           // = NYX_MAX_BUCKETS (InstancingCulling.hlsl)
        const int ThreadGroupSize = 64;             // = NYX_THREADS
        const int MaxGroupsPerDimension = 65535;
        const int ArgsPerDraw = 5;
        const int MaxGeneratedInstances = 2_000_000;

        /// <summary>
        /// Sommets maximum du polygone de la zone. Le test de la zone (placement, et à chaque image avec Clip To Volume) coûte
        /// en proportion du nombre de sommets : 64 sommets 0,7 ms, 1 024 sommets 8 ms pour 1 000 000 d'instances (GTX 1060).
        /// Pour un contour naturel, préférer quelques points + ZoneSmooth / ZoneEdgeNoise / ZoneEdgeFeather.
        /// </summary>
        public const int MaxZonePoints = 4096;

        /// <summary>Sommets du contour courbe (ZoneSmooth) : chaque segment est découpé jusqu'à ce total.</summary>
        public const int SmoothZonePoints = 64;
        const float ZoneHeightTolerance = 1e-3f;   // une instance posée sur la face du haut est dedans

        [Header("Meshes")]
        [Tooltip("Variantes : 1 à 4 meshes (LOD0 en premier), distances de LOD, matériau compatible avec le comportement.")]
        [SerializeField] List<InstanceVariant> variants = new List<InstanceVariant>();

        [Header("Zone : polygone extrudé vers le bas")]
        [Tooltip("Centre de la face du haut, par rapport à la position de cet objet (axes du monde). Le placement se fait à cette hauteur.")]
        [SerializeField] Vector3 zoneCenter;
        [Tooltip("Sommets de la face du haut (X, Z en m) par rapport au centre, dans l'ordre du contour. Concave possible, " +
                 "3 à 64 sommets. Moins de 3 : rectangle de l'ancienne taille de zone.")]
        [SerializeField] List<Vector2> zonePoints = new List<Vector2>();
        [Tooltip("Profondeur du volume sous la face du haut (m).")]
        [SerializeField, Min(0f)] float zoneDepth = 10f;
        [Tooltip("Détruire les instances qui sortent du volume : hors du volume au placement, elles ne sont pas créées ; " +
                 "en mouvement, une instance vue hors du volume est détruite jusqu'à la prochaine reconstruction.")]
        [SerializeField] bool clipToVolume;   // false pour les spawners enregistrés avant la zone ; Reset() met true

        [Header("Zone : bord naturel")]
        [Tooltip("Contour courbe : les sommets sont reliés par une courbe qui passe par chacun d'eux (Catmull-Rom centripète), " +
                 "au lieu de segments droits. Quelques points suffisent pour une forme naturelle.")]
        [SerializeField] bool zoneSmooth;
        [Tooltip("Irrégularité du bord (m) : le contour est déplacé jusqu'à ± cette distance par un bruit fractal, comme une côte. " +
                 "0 = contour exact. Coût GPU : celui d'un polygone de 64 sommets, quel que soit le nombre de détails.")]
        [SerializeField, Min(0f)] float zoneEdgeNoise;
        [Tooltip("Taille des irrégularités du bord (m).")]
        [SerializeField, Min(0.5f)] float zoneEdgeNoiseScale = 15f;
        [Tooltip("Lisière (m) : la densité du placement baisse jusqu'à zéro sur cette distance avant le bord : bord clairsemé " +
                 "plutôt que coupé net. 0 = bord net.")]
        [SerializeField, Min(0f)] float zoneEdgeFeather;

        [Header("Placement (si le comportement l'utilise)")]
        [SerializeField] PlacementSource source = PlacementSource.Procedural;
        [SerializeField] List<MeshPlacement> placements = new List<MeshPlacement>();
        [SerializeField] int randomSeed = 1234;
        [Tooltip("Ancienne zone rectangulaire, centrée sur l'objet : remplacée par le polygone de la zone (migration).")]
        [SerializeField, HideInInspector] Vector2 areaSize = new Vector2(200f, 200f);
        [Tooltip("Instances pour 100 m², sur une grille perturbée (couverture régulière, sans amas ni trous).")]
        [SerializeField, Min(0f)] float densityPer100m2 = 2f;
        [SerializeField] Vector2 scaleRange = new Vector2(0.7f, 1.4f);
        [Tooltip("Amas d'instances autour de ces points (position XZ lue à la génération). Pour des algues au-dessus de mines, " +
                 "mettre aussi des leurres, sinon la présence d'algues trahit la mine.")]
        [SerializeField] List<Transform> clusterAnchors = new List<Transform>();
        [SerializeField, Min(0f)] float clusterRadius = 5f;
        [SerializeField, Min(0)] int clumpsPerCluster = 30;

        [Header("Rendu")]
        [SerializeField] int renderLayer;
        [SerializeField] ShadowCastingMode castShadows = ShadowCastingMode.Off;
        [SerializeField] bool receiveShadows = true;
        [Tooltip("Ne pas dessiner les instances dans la texture de profondeur de la caméra, que Unity remplit avant l'image dès que " +
                 "le soleil projette des ombres. Pour un grand champ de végétation alpha-testée, cette passe coûte presque autant que " +
                 "l'image (démo : 12,6 -> 8,6 ms GPU). En contrepartie, les instances ne reçoivent plus d'ombres, et les effets qui " +
                 "lisent la profondeur (ombres en espace écran, profondeur de champ, eau qui lit la profondeur) ne les voient plus. " +
                 "Les matériaux sont dessinés par des copies dont la file de rendu passe au-dessus de 2500 ; les assets ne changent pas.")]
        [SerializeField] bool excludeFromDepthTexture;

        [Header("Culling GPU")]
        [Tooltip("Distance max d'affichage (m). Sous l'eau, inutile de dépasser la visibilité du brouillard.")]
        [SerializeField, Min(1f)] float maxDrawDistance = 150f;
        [Tooltip("> 1 : les LODs détaillés restent visibles plus loin. < 1 : bascule plus tôt.")]
        [SerializeField, Min(0.01f)] float lodBias = 1f;
        [Tooltip("Rayon projeté minimal, en fraction de la demi-hauteur d'écran. 0,001 ≈ 1 px pour 2000 px de haut.")]
        [SerializeField, Range(0f, 0.02f)] float minScreenSize = 0.001f;
        [Tooltip("Marge ajoutée aux bounds (espace local du mesh) pour couvrir la déformation du vertex shader.")]
        [SerializeField, Min(0f)] float boundsPadding = 0.1f;
        [Tooltip("Avec ombres : extrusion (m) des bounds le long de la lumière, pour garder les instances hors champ dont l'ombre est visible.")]
        [SerializeField, Min(0f)] float shadowCullingExtrusion;
        [Tooltip("Lumière des ombres. Vide = soleil de la scène (Lighting > Sun Source).")]
        [SerializeField] Light shadowLight;

        [Header("Caméras / VR")]
        [SerializeField] StereoInstancingMode stereoInstancing = StereoInstancingMode.Auto;
        [SerializeField] bool renderInSceneView = true;
        [Tooltip("Log 1x/s du nombre d'instances visibles par draw (lecture GPU asynchrone, sans blocage).")]
        [SerializeField] bool logVisibleCounts;

        /// <summary>
        /// Jeu de buffers de culling. Partagé par les caméras rendues l'une après l'autre (vue Game puis vue Scene...) :
        /// la caméra suivante ne lance son culling qu'après les draws de la précédente. Une caméra rendue pendant le rendu
        /// d'une autre (réflexion d'eau via Camera.Render) prend un autre jeu.
        /// </summary>
        sealed class CullingBuffers
        {
            public ComputeBuffer visible;        // données de rendu des instances visibles, compactées
            public ComputeBuffer visibleIndex;   // par draw : index dans 'visible' (zone = pire cas du draw)
            public ComputeBuffer counters;       // par draw + total (remis à zéro par NyxWriteArgs)
            public MaterialPropertyBlock[] blocks;

            public long Bytes => Size(visible) + Size(visibleIndex) + Size(counters);

            public void Release()
            {
                visible?.Release();
                visibleIndex?.Release();
                counters?.Release();
                visible = visibleIndex = counters = null;
            }
        }

        /// <summary>État propre à une caméra : ses arguments indirects (lus aussi par TryReadVisibleCounts) et le jeu qu'elle utilise.</summary>
        sealed class CameraState
        {
            public ComputeBuffer args;           // bloc caméra puis bloc ombre
            public CullingBuffers inUse;         // pris entre le début et la fin du rendu de la caméra
            public uint lastMultiplier = 1;      // diagnostic : multiplicateur VR du dernier rendu
            public bool skippedLastRender;       // diagnostic : spawner rejeté en entier côté CPU au dernier rendu
        }

        /// <summary>Vue en lecture seule des placements (lus sur GPU à la première demande, mémoire native du spawner), sans copie.</summary>
        sealed class PlacementList : IReadOnlyList<MeshPlacement>
        {
            readonly MeshSpawner _owner;

            public PlacementList(MeshSpawner owner) => _owner = owner;

            public int Count
            {
                get
                {
                    NativeArray<MeshPlacement> placements = _owner.EnsurePlacements();
                    return placements.IsCreated ? placements.Length : 0;
                }
            }

            public MeshPlacement this[int index] => _owner.EnsurePlacements()[index];

            public IEnumerator<MeshPlacement> GetEnumerator()
            {
                NativeArray<MeshPlacement> placements = _owner.EnsurePlacements();
                int count = placements.IsCreated ? placements.Length : 0;
                for (int i = 0; i < count; i++)
                    yield return _owner._placements[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        // --- Données communes à toutes les caméras (reconstruites hors rendu) ---
        ComputeBuffer _instanceBuffer;
        ComputeBuffer _variantBuffer;
        ComputeBuffer _bucketOffsetBuffer;
        ComputeShader _cullingShader;
        MeshSpawnerBehavior _behavior;
        NativeArray<MeshPlacement> _placements;
        PlacementList _placementList;
        Mesh[] _bucketMeshes = Array.Empty<Mesh>();
        Material[] _bucketMaterials = Array.Empty<Material>();
        int[] _bucketSubMeshes = Array.Empty<int>();
        string[] _bucketLabels = Array.Empty<string>();
        uint[] _bucketOffsets = Array.Empty<uint>();
        uint[] _argsTemplate = Array.Empty<uint>();
        int _visibleCapacity;
        int _indexCapacity;
        int _bucketCount;
        int _kernelCull = -1;
        int _kernelWriteArgs = -1;
        bool _built;
        bool _dirty = true;
        bool _warnedUnsupported;
        bool _warnedNoBehavior;
        float _nextLogTime;
        Bounds _buildBounds;
        Bounds? _dynamicBounds;
        Bounds _worldBounds;
        readonly Dictionary<Camera, CameraState> _cameras = new Dictionary<Camera, CameraState>();
        readonly List<CullingBuffers> _allBuffers = new List<CullingBuffers>();
        readonly Stack<CullingBuffers> _freeBuffers = new Stack<CullingBuffers>();
        readonly List<Camera> _deadCameras = new List<Camera>();
        readonly Vector4[] _planes = new Vector4[12];
        readonly List<Vector3> _anchorPositions = new List<Vector3>();
        readonly Dictionary<Material, Material> _depthlessMaterials = new Dictionary<Material, Material>();   // original -> copie hors profondeur

        // --- Volume de la zone, figé à la reconstruction (placement, destruction sur GPU, bounds) ---
        ComputeBuffer _zoneVertexBuffer;   // sommets monde (x, z) ; 1 élément vide sans destruction par le volume
        int _zonePointCountGpu;       // 0 = pas de destruction par le volume
        Vector4 _zoneBoundsGpu;       // min x, min z, max x, max z
        Vector2 _zoneHeightGpu;       // bas, haut (monde, tolérance comprise)
        Vector4 _zoneNaturalGpu;      // bord naturel : amplitude, 1 / taille, (lisière : 0 pour la destruction)
        Vector4 _zoneOriginGpu;       // centre (x, z) et graine du bruit
        Bounds? _zoneDrawBounds;      // volume agrandi de la taille des meshes : limite les bounds du draw
        int _deadVariant;             // index de la variante désactivée (= nombre de variantes à la reconstruction)


        // ------------------------------------------------------------------------------------------
        // API
        // ------------------------------------------------------------------------------------------

        public int InstanceCount { get; private set; }

        /// <summary>Draw calls par caméra pour la passe principale (hors cascades d'ombre). Indépendant du nombre d'instances.</summary>
        public int DrawCallsPerCamera => _bucketCount;

        /// <summary>Mémoire GPU occupée : instances, jeux de buffers de culling, arguments de chaque caméra (octets).</summary>
        public long GpuMemoryBytes
        {
            get
            {
                long bytes = Size(_instanceBuffer) + Size(_variantBuffer) + Size(_bucketOffsetBuffer);
                foreach (CullingBuffers buffers in _allBuffers)
                    bytes += buffers.Bytes;
                foreach (CameraState state in _cameras.Values)
                    bytes += Size(state.args);
                return bytes;
            }
        }

        /// <summary>
        /// Mémoire native CPU retenue entre deux reconstructions (octets) : les placements, seulement s'ils ont été lus
        /// (Placements) ou fournis à un comportement qui écrit ses instances en C#. 0 sinon.
        /// </summary>
        public long CpuMemoryBytes => _placements.IsCreated ? (long)_placements.Length * System.Runtime.InteropServices.Marshal.SizeOf<MeshPlacement>() : 0L;

        /// <summary>Jeux de buffers de culling alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int CullingBufferSets => _allBuffers.Count;

        /// <summary>Origine des instances de la dernière reconstruction : construites sur GPU, ou écrites en C# et envoyées (diagnostic).</summary>
        public UploadMethod LastUploadMethod { get; private set; }

        /// <summary>
        /// Temps commun au culling GPU, aux vagues et aux émissions (s). Time.time ne repart pas de zéro
        /// au chargement d'une scène : les bulles en vol d'un spawner conservé restent cohérentes.
        /// </summary>
        public static float CurrentTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        /// <summary>Comportement actif (premier MeshSpawnerBehavior activé sur cet objet), null si aucun.</summary>
        public MeshSpawnerBehavior Behavior => FindBehavior();

        public IReadOnlyList<InstanceVariant> Variants => variants;

        /// <summary>
        /// Placements de la dernière reconstruction (vide si le comportement fournit ses propres instances). Construits sur
        /// GPU : à la première lecture après une reconstruction, le GPU refait le placement et le CPU le lit (bloquant,
        /// 28 o par instance gardés en mémoire native jusqu'à la prochaine reconstruction). Pour un placement en liste, la
        /// liste est relue telle qu'elle est au moment de la lecture.
        /// </summary>
        public IReadOnlyList<MeshPlacement> Placements => _placementList ??= new PlacementList(this);

        /// <summary>Libellé de chaque draw (« v0/LOD1 »...), dans l'ordre de TryReadVisibleCounts.</summary>
        public IReadOnlyList<string> DrawLabels => _bucketLabels;

        /// <summary>Buffer d'instances à jour : les mises à jour partielles (UploadInstances) sont possibles.</summary>
        public bool IsBuilt => _built && !_dirty;

        /// <summary>
        /// Demande une reconstruction (placements, variantes, buffers). Elle a lieu au prochain LateUpdate,
        /// jamais au milieu d'un rendu : les draws déjà soumis gardent des buffers valides.
        /// </summary>
        public void SetDirty()
        {
            _dirty = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();   // garantit un LateUpdate en mode édition
#endif
        }

        /// <summary>Reconstruction immédiate (tests, création par code). Jamais pendant un rendu : préférer SetDirty().</summary>
        public void RebuildNow() => Rebuild();

        /// <summary>Centre monde de la face du haut de la zone (position de l'objet + décalage).</summary>
        public Vector3 ZoneCenter => transform.position + zoneCenter;

        /// <summary>Décalage du centre de la face du haut par rapport à l'objet.</summary>
        public Vector3 ZoneCenterOffset => zoneCenter;

        /// <summary>
        /// Sommets de la face du haut (X, Z) par rapport au centre. Avec moins de 3 sommets enregistrés (spawner antérieur à
        /// la zone), rectangle de l'ancienne taille de zone : même placement qu'avant.
        /// </summary>
        public IReadOnlyList<Vector2> ZonePoints => zonePoints.Count >= 3 ? zonePoints : RectanglePoints(areaSize);

        /// <summary>Profondeur du volume sous la face du haut (m).</summary>
        public float ZoneDepth => zoneDepth;

        /// <summary>
        /// Contour effectif de la face du haut (X, Z) par rapport au centre : les sommets, ou la courbe qui passe par eux avec
        /// ZoneSmooth (SmoothZonePoints sommets environ). C'est le polygone du placement et de la destruction, avant le bruit du bord.
        /// </summary>
        public IReadOnlyList<Vector2> ZoneOutline => zoneSmooth ? ZoneShape.Smooth(ZonePoints, SmoothZonePoints) : ZonePoints;

        /// <summary>Contour courbe passant par les sommets (voir ZoneOutline).</summary>
        public bool ZoneSmooth
        {
            get => zoneSmooth;
            set => SetZoneField(ref zoneSmooth, value);
        }

        /// <summary>Irrégularité du bord (m) : déplacement du contour par un bruit fractal. 0 = contour exact.</summary>
        public float ZoneEdgeNoise
        {
            get => zoneEdgeNoise;
            set => SetZoneField(ref zoneEdgeNoise, Mathf.Max(0f, value));
        }

        /// <summary>Taille des irrégularités du bord (m).</summary>
        public float ZoneEdgeNoiseScale
        {
            get => zoneEdgeNoiseScale;
            set => SetZoneField(ref zoneEdgeNoiseScale, Mathf.Max(0.5f, value));
        }

        /// <summary>Lisière (m) : la densité du placement baisse jusqu'à zéro sur cette distance avant le bord.</summary>
        public float ZoneEdgeFeather
        {
            get => zoneEdgeFeather;
            set => SetZoneField(ref zoneEdgeFeather, Mathf.Max(0f, value));
        }

        void SetZoneField<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            SetDirty();
        }

        /// <summary>Graine du bruit du bord (celle du placement), sur 24 bits : passée au GPU dans un flottant.</summary>
        uint ZoneNoiseSeed => (uint)randomSeed & 0xFFFFFFu;

        /// <summary>
        /// Bord de la zone en (x, z) monde, &lt;= 0 dedans (même calcul que le GPU) ; 'feather' : compter la lisière comme
        /// bord (distance calculée même sans bruit).
        /// </summary>
        float ZoneEdge(Vector2 xz, IReadOnlyList<Vector2> worldOutline, bool feather)
        {
            bool natural = zoneEdgeNoise > 0f || (feather && zoneEdgeFeather > 0f);
            Vector3 top = ZoneCenter;
            return ZoneShape.Edge(xz, worldOutline, zoneEdgeNoise, 1f / zoneEdgeNoiseScale, natural, new Vector2(top.x, top.z), ZoneNoiseSeed);
        }

        /// <summary>Instances hors de la texture de profondeur de la caméra (voir l'infobulle du réglage) : plus d'ombres reçues.</summary>
        public bool ExcludeFromDepthTexture
        {
            get => excludeFromDepthTexture;
            set
            {
                if (excludeFromDepthTexture == value)
                    return;
                excludeFromDepthTexture = value;
                SetDirty();
            }
        }

        /// <summary>Destruction des instances hors du volume (au placement, puis sur GPU jusqu'à la prochaine reconstruction).</summary>
        public bool ClipToVolume
        {
            get => clipToVolume;
            set
            {
                if (clipToVolume == value)
                    return;
                clipToVolume = value;
                SetDirty();
            }
        }

        /// <summary>
        /// DIAGNOSTIC : avec ClipToVolume, le mouvement du comportement (vagues, dandinement...) dépasse la hauteur du volume :
        /// les instances qui en sortent seront détruites. Calculé à la reconstruction.
        /// </summary>
        public bool MotionExceedsVolumeHeight { get; private set; }

        /// <summary>
        /// Remplace la zone : centre de la face du haut (par rapport à l'objet), sommets (X, Z) par rapport à ce centre,
        /// dans l'ordre du contour (3 à MaxZonePoints), profondeur vers le bas. Coût : une reconstruction.
        /// </summary>
        public void SetZone(Vector3 centerOffset, IEnumerable<Vector2> points, float depth)
        {
            var list = new List<Vector2>(points);
            if (list.Count < 3 || list.Count > MaxZonePoints)
                throw new ArgumentException($"La zone doit avoir de 3 à {MaxZonePoints} sommets ({list.Count}).", nameof(points));
            zoneCenter = centerOffset;
            zonePoints = list;
            zoneDepth = Mathf.Max(0f, depth);
            SetDirty();
        }

        /// <summary>Sommets d'un rectangle centré (X, Z), dans l'ordre du contour.</summary>
        public static List<Vector2> RectanglePoints(Vector2 size)
        {
            Vector2 h = size * 0.5f;
            return new List<Vector2> { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) };
        }

        /// <summary>
        /// Point monde dans le volume actuel de la zone : hauteur, contour (courbe et bord naturel compris), même règle que le
        /// placement et le GPU, à l'arrondi près pour un point à quelques µm du bord. La lisière ne compte pas. Pour le gameplay.
        /// </summary>
        public bool VolumeContains(Vector3 worldPosition)
        {
            Vector3 top = ZoneCenter;
            if (worldPosition.y > top.y + ZoneHeightTolerance || worldPosition.y < top.y - zoneDepth - ZoneHeightTolerance)
                return false;
            return ZoneEdge(new Vector2(worldPosition.x, worldPosition.z), WorldOutline(), false) <= 0f;
        }

        /// <summary>Contour effectif en coordonnées monde (x, z).</summary>
        public List<Vector2> WorldOutline()
        {
            IReadOnlyList<Vector2> outline = ZoneOutline;
            Vector3 top = ZoneCenter;
            var world = new List<Vector2>(outline.Count);
            foreach (Vector2 point in outline)
                world.Add(new Vector2(top.x + point.x, top.z + point.y));
            return world;
        }

        /// <summary>
        /// Bord de la zone en (x, z) monde, comme sur GPU : &lt;= 0 dedans. Avec lisière : distance au bord, en mètres
        /// (la densité du placement vaut min(1, -bord / lisière)). Pour les outils (contour dans la vue Scene).
        /// </summary>
        public float ZoneEdgeAt(Vector2 worldXZ, IReadOnlyList<Vector2> worldOutline, bool withFeather) => ZoneEdge(worldXZ, worldOutline, withFeather);

        /// <summary>Remplace les variantes (meshes, LODs, matériaux).</summary>
        public void SetVariants(IEnumerable<InstanceVariant> newVariants)
        {
            variants = new List<InstanceVariant>(newVariants);
            SetDirty();
        }

        /// <summary>Remplace le placement (passe en mode List). Coût : une reconstruction (upload de N × 64 octets).</summary>
        public void SetPlacements(IEnumerable<MeshPlacement> newPlacements)
        {
            placements.Clear();
            placements.AddRange(newPlacements);
            source = PlacementSource.List;
            SetDirty();
        }

        /// <summary>
        /// Pour les comportements qui modifient des instances sans reconstruction (ex. Emit des bulles) :
        /// envoie data[dataStart..+count] au GPU à partir de l'instance 'start' (count × 64 octets, sans copie CPU).
        /// Sans effet si le buffer n'est pas à jour (la prochaine reconstruction relira les données du comportement).
        /// </summary>
        public void UploadInstances(int start, GpuInstanceData[] data, int dataStart, int count)
        {
            if (!IsBuilt || count <= 0 || start < 0 || start + count > InstanceCount)
                return;
            _instanceBuffer.SetData(data, dataStart, start, count);
        }

        /// <summary>Bounds supplémentaires qui évoluent sans reconstruction (ex. émissions en vol). null = aucune.</summary>
        public void SetDynamicBounds(Bounds? bounds)
        {
            _dynamicBounds = bounds;
            UpdateWorldBounds();
        }

        /// <summary>
        /// DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera'.
        /// Lecture GPU bloquante (attend la fin du GPU) : à ne jamais appeler en jeu.
        /// Renvoie false si cette caméra n'a encore jamais dessiné ce spawner.
        /// </summary>
        public bool TryReadVisibleCounts(Camera camera, List<int> countsPerDraw)
        {
            countsPerDraw.Clear();
            if (camera == null || !_cameras.TryGetValue(camera, out CameraState state) || state.args == null)
                return false;

            var args = new uint[state.args.count];
            if (!state.skippedLastRender)
                state.args.GetData(args);
            uint multiplier = state.lastMultiplier == 0 ? 1u : state.lastMultiplier;
            for (int b = 0; b < _bucketCount; b++)
                countsPerDraw.Add((int)(args[b * ArgsPerDraw + 1] / multiplier));
            return true;
        }

        /// <summary>
        /// DIAGNOSTIC / TESTS : les destination.Length premières instances telles que le GPU les a reçues.
        /// Lecture GPU bloquante : à ne jamais appeler en jeu. false si le spawner n'est pas construit.
        /// </summary>
        public bool TryReadInstances(GpuInstanceData[] destination)
        {
            if (!_built || destination == null || _instanceBuffer == null || !_instanceBuffer.IsValid())
                return false;
            _instanceBuffer.GetData(destination, 0, 0, Mathf.Min(destination.Length, InstanceCount));
            return true;
        }

        [ContextMenu("Figer le placement procédural dans la liste")]
        public void FreezePlacement()
        {
            if (!SystemInfo.supportsComputeShaders || !LoadBuildShader())
            {
                Debug.LogError($"{name} : placement impossible sans compute shaders ({BuildShaderResource}).", this);
                return;
            }
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "Figer le placement");
#endif
            NativeArray<MeshPlacement> generated = ReadPlacements(PreparePlacement(), Allocator.Persistent);
            placements = new List<MeshPlacement>(generated.ToArray());
            generated.Dispose();
            source = PlacementSource.List;
            SetDirty();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        // ------------------------------------------------------------------------------------------
        // Cycle de vie
        // ------------------------------------------------------------------------------------------

        void OnEnable()
        {
            SetDirty();
            Camera.onPreCull += OnCameraPreCull;                                    // Built-in
            Camera.onPostRender += OnCameraPostRender;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;   // SRP (les shaders fournis restent Built-in)
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            OceanWaves.Changed += SetDirty;
        }

        void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            OceanWaves.Changed -= SetDirty;
            ReleaseRenderResources();
            ReleaseInstanceBuffer();
            _zoneVertexBuffer?.Release();
            _zoneVertexBuffer = null;
            DisposePlacements();
        }

        void OnValidate()
        {
            if (scaleRange.y < scaleRange.x)
                scaleRange.y = scaleRange.x;
            if (zonePoints.Count > MaxZonePoints)
                zonePoints.RemoveRange(MaxZonePoints, zonePoints.Count - MaxZonePoints);
            SetDirty();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Nouveau spawner : zone rectangulaire éditable et destruction par le volume. Les spawners enregistrés avant la zone
        /// n'ont ni sommets ni destruction : même placement qu'avant. Le comportement ajouté ensuite peut changer ces défauts.
        /// </summary>
        void Reset()
        {
            zonePoints = RectanglePoints(areaSize);
            clipToVolume = true;
            SetDirty();
        }
#endif

        void Update()
        {
            // Mode édition uniquement : le placement et le volume suivent l'objet et les ancres pendant que le level designer
            // les déplace. En jeu, ils sont figés (une mine qui flotte ne doit pas régénérer le champ à chaque frame) :
            // appeler SetDirty() après un déplacement volontaire.
            if (Application.isPlaying)
                return;
            MeshSpawnerBehavior behavior = FindBehavior();
            if (behavior == null)
                return;
            bool procedural = behavior.UsesPlacement && source == PlacementSource.Procedural;
            if (!procedural && !clipToVolume)
                return;

            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                SetDirty();
                return;
            }
            if (!procedural)
                return;
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

        /// <summary>Reconstruction différée, ressources des caméras détruites, jeux de buffers non rendus. Toujours hors rendu.</summary>
        void LateUpdate()
        {
            if (_dirty)
                Rebuild();

            foreach (KeyValuePair<Camera, CameraState> pair in _cameras)
            {
                if (pair.Key == null)
                    _deadCameras.Add(pair.Key);
                else
                    ReturnBuffers(pair.Value);   // aucun rendu en cours ici : un jeu resté pris (fin de rendu manquée) est libre
            }
            foreach (Camera camera in _deadCameras)
            {
                CameraState state = _cameras[camera];
                ReturnBuffers(state);
                state.args?.Release();
                _cameras.Remove(camera);
            }
            _deadCameras.Clear();
        }

        void OnCameraPreCull(Camera camera) => RenderForCamera(camera);

        void OnCameraPostRender(Camera camera) => EndCamera(camera);

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera) => RenderForCamera(camera);

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera) => EndCamera(camera);

        void EndCamera(Camera camera)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                ReturnBuffers(state);
        }

        MeshSpawnerBehavior FindBehavior()
        {
            if (_behavior != null && _behavior.enabled)
                return _behavior;
            _behavior = null;
            foreach (MeshSpawnerBehavior candidate in GetComponents<MeshSpawnerBehavior>())
            {
                if (candidate.enabled)
                {
                    _behavior = candidate;
                    break;
                }
            }
            return _behavior;
        }

        // ------------------------------------------------------------------------------------------
        // Rendu par caméra
        // ------------------------------------------------------------------------------------------

        void RenderForCamera(Camera camera)
        {
            if (!isActiveAndEnabled || !ShouldRenderFor(camera))
                return;
            using ProfilerMarker.AutoScope profilerScope = Markers.Render.Auto();

            if (!SystemInfo.supportsComputeShaders)
            {
                if (!_warnedUnsupported)
                    Debug.LogWarning($"{name} : compute shaders non supportés sur cette plateforme, rendu désactivé.", this);
                _warnedUnsupported = true;
                return;
            }

            // Première construction seulement : aucun draw de ce spawner n'est encore en vol.
            // Les reconstructions suivantes attendent LateUpdate (on dessine avec les données courantes d'ici là).
            // Après un échec (_dirty retombé à false), on ne réessaie qu'au prochain SetDirty : pas d'erreur à chaque frame.
            if (!_built && _dirty)
                Rebuild();
            MeshSpawnerBehavior behavior = FindBehavior();
            if (!_built || InstanceCount == 0 || _bucketCount == 0 || behavior == null)
                return;

            ComputeShader cs = _cullingShader;
            uint multiplier = GetInstanceMultiplier(camera);
            bool stereo = SetCameraParameters(cs, camera, multiplier, out Vector3 shadowSweep);
            CameraState state = GetCameraState(camera);

            // Spawner entièrement hors du frustum ou trop loin : ni dispatch, ni draw.
            Bounds cullBounds = _worldBounds;
            cullBounds.center += shadowSweep;
            cullBounds.extents += Abs(shadowSweep);
            if (!BoundsVisible(cullBounds, stereo, camera.transform.position))
            {
                state.skippedLastRender = true;
                return;
            }

            CullingBuffers buffers = state.inUse ??= TakeBuffers();
            state.lastMultiplier = multiplier;
            state.skippedLastRender = false;

            cs.SetFloat(ShaderIds.Time, CurrentTime);
            cs.SetInt(ShaderIds.InstanceCount, InstanceCount);
            cs.SetInt(ShaderIds.BucketCount, _bucketCount);
            cs.SetInt(ShaderIds.DeadVariant, _deadVariant);
            cs.SetInt(ShaderIds.ZonePointCount, _zonePointCountGpu);
            cs.SetBuffer(_kernelCull, ShaderIds.ZoneVertices, _zoneVertexBuffer);
            if (_zonePointCountGpu > 0)
            {
                cs.SetVector(ShaderIds.ZoneBounds, _zoneBoundsGpu);
                cs.SetVector(ShaderIds.ZoneHeight, _zoneHeightGpu);
                cs.SetVector(ShaderIds.ZoneNatural, _zoneNaturalGpu);
                cs.SetVector(ShaderIds.ZoneOrigin, _zoneOriginGpu);
            }
            behavior.SetShaderParameters(cs, camera);

            cs.SetBuffer(_kernelCull, ShaderIds.Instances, _instanceBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Variants, _variantBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.BucketOffsets, _bucketOffsetBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Visible, buffers.visible);
            cs.SetBuffer(_kernelCull, ShaderIds.VisibleIndex, buffers.visibleIndex);
            cs.SetBuffer(_kernelCull, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Args, state.args);

            DispatchInstances(cs, _kernelCull, InstanceCount);
            cs.Dispatch(_kernelWriteArgs, 1, 1, 1);   // _bucketCount <= 64 : un seul groupe

            // En Single Pass Instanced, le nombre d'instances est doublé pour les passes caméra, mais la carte
            // d'ombre n'est pas stéréo : elle utilise son propre bloc d'arguments (non doublé) via un draw ShadowsOnly.
            bool shadows = castShadows != ShadowCastingMode.Off;
            bool splitShadows = shadows && multiplier > 1;
            ShadowCastingMode mainShadows = splitShadows ? ShadowCastingMode.Off : castShadows;
            bool receive = receiveShadows && !excludeFromDepthTexture;
            SyncDepthlessMaterials();
            for (int b = 0; b < _bucketCount; b++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    _bucketMeshes[b], _bucketSubMeshes[b], _bucketMaterials[b], _worldBounds,
                    state.args, b * ArgsPerDraw * sizeof(uint), buffers.blocks[b],
                    mainShadows, receive, renderLayer, camera, LightProbeUsage.Off);

                if (splitShadows)
                {
                    Graphics.DrawMeshInstancedIndirect(
                        _bucketMeshes[b], _bucketSubMeshes[b], _bucketMaterials[b], _worldBounds,
                        state.args, (_bucketCount + b) * ArgsPerDraw * sizeof(uint), buffers.blocks[b],
                        ShadowCastingMode.ShadowsOnly, false, renderLayer, camera, LightProbeUsage.Off);
                }
            }

            if (logVisibleCounts)
                LogVisibleCounts(camera, state, multiplier);
        }

        bool ShouldRenderFor(Camera camera)
        {
            if ((camera.cullingMask & (1 << renderLayer)) == 0)
                return false;

            switch (camera.cameraType)
            {
                case CameraType.Game:
                case CameraType.VR:
                    return true;            // inclut les caméras de réflexion d'eau (rendues comme caméras Game)
                case CameraType.SceneView:
                    return renderInSceneView;
                default:
                    return false;           // aperçus, sondes de réflexion
            }
        }

        CameraState GetCameraState(Camera camera)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                return state;
            state = new CameraState { args = new ComputeBuffer(_argsTemplate.Length, sizeof(uint), ComputeBufferType.IndirectArguments) };
            state.args.SetData(_argsTemplate);
            _cameras.Add(camera, state);
            return state;
        }

        /// <summary>Jeu de buffers libre, ou un nouveau si toutes les caméras en cours de rendu en tiennent un.</summary>
        CullingBuffers TakeBuffers()
        {
            if (_freeBuffers.Count > 0)
                return _freeBuffers.Pop();

            // Création seulement (jamais de libération ici : on peut être au milieu du rendu d'une autre caméra)
            var buffers = new CullingBuffers
            {
                visible = new ComputeBuffer(Mathf.Max(_visibleCapacity, 1), GpuInstanceData.Stride, ComputeBufferType.Default),
                visibleIndex = new ComputeBuffer(Mathf.Max(_indexCapacity, 1), sizeof(uint), ComputeBufferType.Default),
                counters = new ComputeBuffer(_bucketCount + 1, sizeof(uint), ComputeBufferType.Default),
                blocks = new MaterialPropertyBlock[_bucketCount]
            };
            buffers.counters.SetData(new uint[_bucketCount + 1]);
            for (int b = 0; b < _bucketCount; b++)
            {
                var block = new MaterialPropertyBlock();
                block.SetBuffer(ShaderIds.RenderData, buffers.visible);
                block.SetBuffer(ShaderIds.RenderIndex, buffers.visibleIndex);
                block.SetFloat(ShaderIds.BucketOffset, _bucketOffsets[b]);
                buffers.blocks[b] = block;
            }
            _allBuffers.Add(buffers);
            return buffers;
        }

        void ReturnBuffers(CameraState state)
        {
            if (state.inUse == null)
                return;
            _freeBuffers.Push(state.inUse);
            state.inUse = null;
        }

        /// <summary>Plans du frustum (2 yeux en stéréo), distance, taille mini, extrusion d'ombre, multiplicateur VR.</summary>
        bool SetCameraParameters(ComputeShader cs, Camera camera, uint multiplier, out Vector3 shadowSweep)
        {
            bool stereo = camera.stereoEnabled;
            float projectionScale;
            if (stereo)
            {
                Matrix4x4 left = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 right = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                ExtractFrustumPlanes(left * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), 0);
                ExtractFrustumPlanes(right * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), 6);
                projectionScale = Mathf.Min(left.m11, right.m11);
            }
            else
            {
                Matrix4x4 projection = camera.projectionMatrix;
                ExtractFrustumPlanes(projection * camera.worldToCameraMatrix, 0);
                Array.Copy(_planes, 0, _planes, 6, 6);
                projectionScale = projection.m11;
            }

            float maxDistance = Mathf.Min(maxDrawDistance, camera.farClipPlane);
            float sizeRatio = camera.orthographic ? 0f : minScreenSize / Mathf.Max(projectionScale, 1e-4f);

            shadowSweep = Vector3.zero;
            Light light = shadowLight != null ? shadowLight : RenderSettings.sun;
            if (castShadows != ShadowCastingMode.Off && shadowCullingExtrusion > 0f && light != null)
                shadowSweep = light.transform.forward * (shadowCullingExtrusion * 0.5f);

            Vector3 cameraPosition = camera.transform.position;
            cs.SetVectorArray(ShaderIds.FrustumPlanes, _planes);
            cs.SetInt(ShaderIds.PlaneSetCount, stereo ? 2 : 1);
            cs.SetVector(ShaderIds.CameraPosition, new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, maxDistance * maxDistance));
            cs.SetVector(ShaderIds.CullParams, new Vector4(1f / (lodBias * lodBias), sizeRatio * sizeRatio, 0f, 0f));
            cs.SetVector(ShaderIds.ShadowSweep, shadowSweep);
            cs.SetInt(ShaderIds.InstanceMultiplier, (int)multiplier);
            return stereo;
        }

        /// <summary>
        /// En Single Pass Instanced, chaque instance est dessinée 2 fois (une par œil) : le nombre d'instances
        /// des arguments indirects doit être doublé à la main (SetInstanceMultiplier ne s'applique pas aux draws indirects).
        /// En multiview (Android : PICO, Quest), l'œil vient de gl_ViewID : pas de doublement.
        /// </summary>
        uint GetInstanceMultiplier(Camera camera)
        {
            if (!camera.stereoEnabled)
                return 1;

            switch (stereoInstancing)
            {
                case StereoInstancingMode.ForceSinglePassInstanced:
                    return 2;
                case StereoInstancingMode.ForceOff:
                    return 1;
                default:
                    if (SystemInfo.supportsMultiview)
                        return 1;
                    return XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass ? 1u : 2u;
            }
        }

        /// <summary>Plans de Gribb-Hartmann (convention OpenGL de Unity : z clip dans [-w, w]), normales vers l'intérieur.</summary>
        void ExtractFrustumPlanes(Matrix4x4 viewProjection, int offset)
        {
            Vector4 r0 = viewProjection.GetRow(0);
            Vector4 r1 = viewProjection.GetRow(1);
            Vector4 r2 = viewProjection.GetRow(2);
            Vector4 r3 = viewProjection.GetRow(3);
            _planes[offset + 0] = NormalizePlane(r3 + r0);   // gauche
            _planes[offset + 1] = NormalizePlane(r3 - r0);   // droite
            _planes[offset + 2] = NormalizePlane(r3 + r1);   // bas
            _planes[offset + 3] = NormalizePlane(r3 - r1);   // haut
            _planes[offset + 4] = NormalizePlane(r3 + r2);   // proche
            _planes[offset + 5] = NormalizePlane(r3 - r2);   // lointain
        }

        static Vector4 NormalizePlane(Vector4 plane)
        {
            float length = Mathf.Sqrt(plane.x * plane.x + plane.y * plane.y + plane.z * plane.z);
            return length > 0f ? plane / length : plane;
        }

        bool BoundsVisible(Bounds bounds, bool stereo, Vector3 cameraPosition)
        {
            if (bounds.SqrDistance(cameraPosition) > maxDrawDistance * maxDrawDistance)
                return false;
            return AabbInsidePlanes(bounds.center, bounds.extents, 0) || (stereo && AabbInsidePlanes(bounds.center, bounds.extents, 6));
        }

        bool AabbInsidePlanes(Vector3 center, Vector3 extents, int offset)
        {
            for (int p = 0; p < 6; p++)
            {
                Vector4 plane = _planes[offset + p];
                float distance = plane.x * center.x + plane.y * center.y + plane.z * center.z + plane.w;
                float radius = Mathf.Abs(plane.x) * extents.x + Mathf.Abs(plane.y) * extents.y + Mathf.Abs(plane.z) * extents.z;
                if (distance < -radius)
                    return false;
            }
            return true;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        void UpdateWorldBounds()
        {
            _worldBounds = _buildBounds;
            if (_dynamicBounds.HasValue)
                _worldBounds.Encapsulate(_dynamicBounds.Value);
            if (_zoneDrawBounds.HasValue)
            {
                // Les instances détruites hors du volume ne sont jamais dessinées : le draw tient dans le volume
                Bounds zone = _zoneDrawBounds.Value;
                Vector3 min = Vector3.Max(_worldBounds.min, zone.min);
                Vector3 max = Vector3.Max(min, Vector3.Min(_worldBounds.max, zone.max));
                _worldBounds.SetMinMax(min, max);
            }
        }

        // ------------------------------------------------------------------------------------------
        // Construction (toujours hors rendu, sauf la toute première)
        // ------------------------------------------------------------------------------------------

        void Rebuild()
        {
            using ProfilerMarker.AutoScope profilerScope = Markers.Rebuild.Auto();
            ReleaseRenderResources();
            _dirty = false;
            DisposePlacements();
            _placementSetup = null;
            LastUploadMethod = UploadMethod.None;

            MeshSpawnerBehavior behavior = FindBehavior();
            if (behavior == null)
            {
                if (!_warnedNoBehavior)
                    Debug.LogWarning($"{name} : aucun comportement (MeshSpawnerBehavior) sur l'objet, rien n'est dessiné. " +
                                     "Ajouter par exemple Static Instances, Float On Waves ou Bubble Columns.", this);
                _warnedNoBehavior = true;
                ReleaseInstanceBuffer();
                return;
            }
            _warnedNoBehavior = false;
            if (!SystemInfo.supportsComputeShaders)
                return;   // signalé au rendu : rien ne serait dessiné
            if (!LoadBuildShader())
            {
                Debug.LogError($"{name} : compute shader {BuildShaderResource} introuvable ou invalide (dossier Resources du package).", this);
                return;
            }
            _cullingShader = behavior.CullingShader;
            if (_cullingShader == null)
            {
                Debug.LogError($"{name} : le comportement {behavior.GetType().Name} ne fournit pas de compute shader de culling.", this);
                return;
            }
            _kernelCull = _cullingShader.FindKernel("NyxCull");
            _kernelWriteArgs = _cullingShader.FindKernel("NyxWriteArgs");

            int variantCount = variants.Count;
            if (variantCount > MaxVariants)
            {
                Debug.LogError($"{name} : {variantCount} variantes > {MaxVariants}. Réduisez les variantes.", this);
                return;
            }
            var lodCounts = new int[variantCount];
            int bucketCount = 0;
            _validVariantsLow = _validVariantsHigh = 0;
            for (int v = 0; v < variantCount; v++)
            {
                lodCounts[v] = LodCount(variants[v]);
                bucketCount += lodCounts[v];
                if (lodCounts[v] == 0)
                    continue;
                if (v < 32)
                    _validVariantsLow |= 1u << v;
                else
                    _validVariantsHigh |= 1u << (v - 32);
                // Avec « Instancing Variants : Strip Unused » (réglage par défaut), un build supprime les variantes
                // instanciées des shaders dont aucun matériau n'a l'instancing coché : rien ne s'afficherait.
                if (!variants[v].material.enableInstancing)
                    Debug.LogWarning($"{name} : cochez « Enable GPU Instancing » sur le matériau '{variants[v].material.name}', sinon rien ne s'affichera dans les builds.", this);
            }
            if (bucketCount > MaxBuckets)
            {
                Debug.LogError($"{name} : {bucketCount} draws (variantes × LODs) > {MaxBuckets}. Réduisez les variantes ou les LODs.", this);
                return;
            }

            CacheZoneVolume();
            _deadVariant = variantCount;
            _zoneDrawBounds = null;
            MotionExceedsVolumeHeight = false;
            SetVariantParameters(s_buildShader);

            // Instances construites sur GPU (ou écrites en C# puis envoyées), et leur nombre par variante. Une variante
            // invalide (mesh/matériau manquant) pointe vers une variante "désactivée" : les index restent stables.
            var perVariant = new NativeArray<int>(variantCount, Allocator.Temp);
            try
            {
                int instanceCount;
                using (Markers.BuildInstances.Auto())
                    instanceCount = behavior.UsesPlacement ? BuildPlacedInstances(behavior, perVariant) : BuildBehaviorInstances(behavior, perVariant);
                UpdateWorldBounds();
                BuildBuckets(behavior, lodCounts, perVariant);
                InstanceCount = instanceCount;
                _built = true;
            }
            finally
            {
                perVariant.Dispose();
            }
        }

        /// <summary>
        /// Un "bucket" = un draw = (variante, LOD). Chaque draw a sa zone d'index (4 octets par instance), dimensionnée pour
        /// le pire cas (toutes les instances de la variante dans ce LOD) ; les données de rendu (64 octets) ne sont écrites
        /// qu'une fois par instance visible, toutes variantes confondues.
        /// </summary>
        void BuildBuckets(MeshSpawnerBehavior behavior, int[] lodCounts, NativeArray<int> perVariant)
        {
            int variantCount = lodCounts.Length;
            int bucketCount = 0;
            for (int v = 0; v < variantCount; v++)
                bucketCount += lodCounts[v];   // <= MaxBuckets (vérifié par Rebuild)

            _bucketMeshes = new Mesh[bucketCount];
            _bucketMaterials = new Material[bucketCount];
            _bucketSubMeshes = new int[bucketCount];
            _bucketLabels = new string[bucketCount];
            _bucketOffsets = new uint[Mathf.Max(bucketCount, 1)];
            _argsTemplate = new uint[Mathf.Max(bucketCount, 1) * ArgsPerDraw * 2];   // bloc caméra + bloc ombre
            var variantInfos = new GpuVariantInfo[variantCount + 1];
            int bucket = 0;
            uint indexSlots = 0;
            int validInstances = 0;

            for (int v = 0; v < variantCount; v++)
            {
                if (lodCounts[v] == 0)
                {
                    variantInfos[v] = GpuVariantInfo.Disabled;
                    continue;
                }

                InstanceVariant variant = variants[v];
                int lods = lodCounts[v];
                validInstances += perVariant[v];
                Bounds local = LocalBounds(variant, lods);
                float padding = behavior.GetBoundsPadding(variant, local, boundsPadding);
                float cull = variant.cullDistance > 0f ? variant.cullDistance * variant.cullDistance : float.MaxValue;
                variantInfos[v] = new GpuVariantInfo
                {
                    boundsCenter = local.center,
                    boundsExtents = local.extents + Vector3.one * padding,
                    lodDistancesSq = new Vector4(LodStartSq(variant, 1, lods), LodStartSq(variant, 2, lods), LodStartSq(variant, 3, lods), cull),
                    firstBucket = (uint)bucket,
                    lodCount = (uint)lods
                };

                for (int lod = 0; lod < lods; lod++, bucket++)
                {
                    Mesh mesh = variant.lodMeshes[lod];
                    int subMesh = Mathf.Clamp(variant.subMeshIndex, 0, mesh.subMeshCount - 1);
                    _bucketMeshes[bucket] = mesh;
                    _bucketMaterials[bucket] = DrawMaterial(variant.material);
                    _bucketSubMeshes[bucket] = subMesh;
                    _bucketLabels[bucket] = $"v{v}/LOD{lod}";
                    _bucketOffsets[bucket] = indexSlots;
                    indexSlots += (uint)perVariant[v];

                    for (int block = 0; block < 2; block++)
                    {
                        int a = (block * bucketCount + bucket) * ArgsPerDraw;
                        _argsTemplate[a + 0] = mesh.GetIndexCount(subMesh);
                        _argsTemplate[a + 1] = 0;
                        _argsTemplate[a + 2] = mesh.GetIndexStart(subMesh);
                        _argsTemplate[a + 3] = mesh.GetBaseVertex(subMesh);
                        _argsTemplate[a + 4] = 0;
                    }
                }
            }
            variantInfos[variantCount] = GpuVariantInfo.Disabled;

            _variantBuffer = new ComputeBuffer(variantInfos.Length, GpuVariantInfo.Stride, ComputeBufferType.Default);
            _bucketOffsetBuffer = new ComputeBuffer(_bucketOffsets.Length, sizeof(uint), ComputeBufferType.Default);
            _variantBuffer.SetData(variantInfos);
            _bucketOffsetBuffer.SetData(_bucketOffsets);
            _visibleCapacity = validInstances;
            _indexCapacity = (int)indexSlots;
            _bucketCount = bucketCount;
        }

        /// <summary>Sommets de la zone en coordonnées monde (x, z) et leur boîte (min x, min z, max x, max z).</summary>
        NativeArray<float2> ZonePolygonWorld(Allocator allocator, out float4 bounds)
        {
            IReadOnlyList<Vector2> points = ZoneOutline;
            Vector3 top = ZoneCenter;
            var polygon = new NativeArray<float2>(points.Count, allocator, NativeArrayOptions.UninitializedMemory);
            bounds = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int i = 0; i < points.Count; i++)
            {
                var p = new float2(top.x + points[i].x, top.z + points[i].y);
                polygon[i] = p;
                bounds = new float4(math.min(bounds.xy, p), math.max(bounds.zw, p));
            }
            return polygon;
        }

        /// <summary>Quatre sommets aux coins de leur boîte, reliés par des arêtes parallèles aux axes (pas un nœud papillon).</summary>
        static bool IsAxisAlignedRectangle(NativeArray<float2> polygon, float4 bounds)
        {
            if (polygon.Length != 4)
                return false;
            int corners = 0;
            for (int i = 0; i < 4; i++)
            {
                float2 p = polygon[i];
                float2 next = polygon[(i + 1) & 3];
                bool cornerX = p.x == bounds.x || p.x == bounds.z;
                bool cornerZ = p.y == bounds.y || p.y == bounds.w;
                if (!cornerX || !cornerZ || (p.x != next.x && p.y != next.y))
                    return false;
                corners |= 1 << ((p.x == bounds.z ? 1 : 0) + (p.y == bounds.w ? 2 : 0));
            }
            return corners == 15;
        }

        /// <summary>Boîte monde du volume de la zone (polygone × profondeur).</summary>
        Bounds ZoneVolumeBounds()
        {
            NativeArray<float2> polygon = ZonePolygonWorld(Allocator.Temp, out float4 b);
            polygon.Dispose();
            float topY = ZoneCenter.y;
            var bounds = new Bounds();
            bounds.SetMinMax(new Vector3(b.x - zoneEdgeNoise, topY - zoneDepth, b.y - zoneEdgeNoise), new Vector3(b.z + zoneEdgeNoise, topY, b.w + zoneEdgeNoise));
            return bounds;
        }

        /// <summary>Paramètres du bord naturel pour le GPU (_NyxZoneNatural, _NyxZoneOrigin) ; lisière comprise ou non.</summary>
        void ZoneNaturalGpu(bool feather, out Vector4 natural, out Vector4 origin)
        {
            natural = new Vector4(zoneEdgeNoise, 1f / zoneEdgeNoiseScale, feather ? zoneEdgeFeather : 0f, 0f);
            Vector3 top = ZoneCenter;
            origin = new Vector4(top.x, top.z, ZoneNoiseSeed, 0f);
        }

        /// <summary>Volume figé pour la destruction sur GPU (posé à chaque dispatch de culling) : rien si ClipToVolume est décoché.</summary>
        void CacheZoneVolume()
        {
            _zonePointCountGpu = 0;
            _zoneVertexBuffer?.Release();   // détruit par Unity après les dispatchs déjà soumis qui le lisent
            _zoneVertexBuffer = null;
            if (!clipToVolume)
            {
                _zoneVertexBuffer = new ComputeBuffer(1, 2 * sizeof(float));   // le noyau de culling le déclare : toujours lié
                return;
            }
            NativeArray<float2> polygon = ZonePolygonWorld(Allocator.Temp, out float4 b);
            _zoneVertexBuffer = new ComputeBuffer(polygon.Length, 2 * sizeof(float));
            _zoneVertexBuffer.SetData(polygon);
            _zonePointCountGpu = polygon.Length;
            polygon.Dispose();
            _zoneBoundsGpu = new Vector4(b.x - zoneEdgeNoise, b.y - zoneEdgeNoise, b.z + zoneEdgeNoise, b.w + zoneEdgeNoise);
            ZoneNaturalGpu(false, out _zoneNaturalGpu, out _zoneOriginGpu);   // destruction : bord seulement, sans lisière
            float topY = ZoneCenter.y;
            _zoneHeightGpu = new Vector2(topY - zoneDepth - ZoneHeightTolerance, topY + ZoneHeightTolerance);
        }

        /// <summary>Plus grand rayon des meshes (depuis leur pivot), toutes variantes et LODs confondus.</summary>
        public float MaxVariantRadius()
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

        static int LodCount(InstanceVariant variant)
        {
            if (variant == null || variant.material == null || variant.lodMeshes == null)
                return 0;
            int count = 0;
            while (count < variant.lodMeshes.Length && count < MaxLods && variant.lodMeshes[count] != null)
                count++;
            return count;
        }

        static Bounds LocalBounds(InstanceVariant variant, int lods)
        {
            Bounds bounds = variant.lodMeshes[0].bounds;
            for (int i = 1; i < lods; i++)
                bounds.Encapsulate(variant.lodMeshes[i].bounds);
            return bounds;
        }

        /// <summary>Distance² de début du LOD 'lod' (seuils forcés croissants), +infini si ce LOD n'existe pas.</summary>
        static float LodStartSq(InstanceVariant variant, int lod, int lodCount)
        {
            if (lod >= lodCount || variant.lodDistances == null || variant.lodDistances.Length < lod)
                return float.MaxValue;
            float distance = 0f;
            for (int i = 0; i < lod; i++)
                distance = Mathf.Max(distance, variant.lodDistances[i]);
            return distance * distance;
        }

        static long Size(ComputeBuffer buffer) => buffer != null && buffer.IsValid() ? (long)buffer.count * buffer.stride : 0L;

        /// <summary>Buffers liés aux draws et aux caméras (le buffer d'instances est gardé pour être réutilisé).</summary>
        void ReleaseRenderResources()
        {
            _variantBuffer?.Release();
            _bucketOffsetBuffer?.Release();
            _variantBuffer = _bucketOffsetBuffer = null;
            foreach (CullingBuffers buffers in _allBuffers)
                buffers.Release();
            _allBuffers.Clear();
            _freeBuffers.Clear();
            foreach (CameraState state in _cameras.Values)
                state.args?.Release();
            _cameras.Clear();
            _bucketCount = 0;
            InstanceCount = 0;
            _built = false;
            _dirty = true;
            foreach (Material copy in _depthlessMaterials.Values)
            {
                if (copy == null)
                    continue;
                if (Application.isPlaying)
                    Destroy(copy);   // après la frame : aucun draw en cours ne la référence plus
                else
                    DestroyImmediate(copy);
            }
            _depthlessMaterials.Clear();
        }

        /// <summary>Plus haute file de rendu dessinée dans la texture de profondeur de la caméra (pipeline Built-in).</summary>
        const int DepthTextureMaxQueue = 2500;

        /// <summary>
        /// Matériau d'un draw : l'original, ou avec Exclude From Depth Texture sa copie dont la file de rendu passe au-dessus de
        /// 2500 (Unity n'y dessine que les files jusqu'à 2500). Copie gardée jusqu'à la prochaine reconstruction.
        /// </summary>
        Material DrawMaterial(Material material)
        {
            if (!excludeFromDepthTexture || material.renderQueue > DepthTextureMaxQueue)
                return material;
            if (!_depthlessMaterials.TryGetValue(material, out Material copy) || copy == null)
            {
                copy = new Material(material) { name = material.name + " (hors profondeur)", hideFlags = HideFlags.HideAndDontSave };
                _depthlessMaterials[material] = copy;
            }
            copy.renderQueue = DepthTextureMaxQueue + 1;
            return copy;
        }

        /// <summary>Les copies hors profondeur suivent les réglages de leur matériau d'origine (modifiés dans l'inspecteur, animés...).</summary>
        void SyncDepthlessMaterials()
        {
            foreach (KeyValuePair<Material, Material> pair in _depthlessMaterials)
            {
                Material original = pair.Key;
                Material copy = pair.Value;
                if (original == null || copy == null)
                    continue;
                if (copy.shader != original.shader)
                    copy.shader = original.shader;
                copy.CopyPropertiesFromMaterial(original);   // propriétés, mots-clés... et file de rendu
                copy.enableInstancing = original.enableInstancing;
                copy.renderQueue = DepthTextureMaxQueue + 1;
            }
        }

        void ReleaseInstanceBuffer()
        {
            _instanceBuffer?.Release();
            _instanceBuffer = null;
        }

        // ------------------------------------------------------------------------------------------
        // Envoi au GPU
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Envoie les instances écrites en C# (comportement qui surcharge CreateInstance(s) ou BuildInstances) dans
        /// _instanceBuffer. ComputeBuffer.SetData : copie par le pilote, puis transfert DMA. Mesuré plus rapide, jusqu'à la
        /// fin du GPU, qu'une écriture directe dans un buffer SubUpdates copié par un compute shader (lecture lente de la
        /// mémoire visible du CPU par le GPU).
        /// </summary>
        void UploadAll(NativeArray<GpuInstanceData> instances)
        {
            _instanceBuffer.SetData(instances);
            LastUploadMethod = UploadMethod.SetData;
        }

        void DisposePlacements()
        {
            if (_placements.IsCreated)
                _placements.Dispose();
            _placements = default;
        }

        // ------------------------------------------------------------------------------------------
        // Diagnostic
        // ------------------------------------------------------------------------------------------

        void LogVisibleCounts(Camera camera, CameraState state, uint multiplier)
        {
            if (!SystemInfo.supportsAsyncGPUReadback || camera.cameraType == CameraType.SceneView)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextLogTime)
                return;
            _nextLogTime = now + 1f;

            int bucketCount = _bucketCount;
            int instanceCount = InstanceCount;
            string[] labels = _bucketLabels;
            long memory = GpuMemoryBytes;
            string cameraName = camera.name;

            AsyncGPUReadback.Request(state.args, request =>
            {
                if (request.hasError || this == null)
                    return;
                var data = request.GetData<uint>();
                var text = new StringBuilder(160);
                text.Append(name).Append(" [").Append(cameraName).Append("] : ").Append(instanceCount).Append(" instances, ")
                    .Append(bucketCount).Append(" draw(s), ").Append(memory / 1024).Append(" Ko GPU | visibles :");
                uint total = 0;
                for (int b = 0; b < bucketCount && b * ArgsPerDraw + 1 < data.Length; b++)
                {
                    uint visible = data[b * ArgsPerDraw + 1] / multiplier;
                    total += visible;
                    text.Append(' ').Append(labels[b]).Append('=').Append(visible);
                }
                text.Append(" | total ").Append(total);
                Debug.Log(text.ToString(), this);
            });
        }

        /// <summary>Sections nommées dans le Profiler. En vue Timeline, les jobs Burst de chaque section apparaissent sur les workers.</summary>
        static class Markers
        {
            public static readonly ProfilerMarker Rebuild = new ProfilerMarker("MeshSpawner.Rebuild");
            public static readonly ProfilerMarker BuildInstances = new ProfilerMarker("MeshSpawner.BuildInstances");
            public static readonly ProfilerMarker Upload = new ProfilerMarker("MeshSpawner.Upload");
            public static readonly ProfilerMarker Render = new ProfilerMarker("MeshSpawner.Render");
        }

        static class ShaderIds
        {
            public static readonly int Instances = Shader.PropertyToID("_NyxInstances");
            public static readonly int Variants = Shader.PropertyToID("_NyxVariants");
            public static readonly int BucketOffsets = Shader.PropertyToID("_NyxBucketOffsets");
            public static readonly int Visible = Shader.PropertyToID("_NyxVisible");
            public static readonly int VisibleIndex = Shader.PropertyToID("_NyxVisibleIndex");
            public static readonly int Counters = Shader.PropertyToID("_NyxCounters");
            public static readonly int Args = Shader.PropertyToID("_NyxArgs");
            public static readonly int InstanceCount = Shader.PropertyToID("_NyxInstanceCount");
            public static readonly int BucketCount = Shader.PropertyToID("_NyxBucketCount");
            public static readonly int GroupsX = Shader.PropertyToID("_NyxGroupsX");
            public static readonly int InstanceMultiplier = Shader.PropertyToID("_NyxInstanceMultiplier");
            public static readonly int PlaneSetCount = Shader.PropertyToID("_NyxPlaneSetCount");
            public static readonly int FrustumPlanes = Shader.PropertyToID("_NyxFrustumPlanes");
            public static readonly int CameraPosition = Shader.PropertyToID("_NyxCameraPosition");
            public static readonly int CullParams = Shader.PropertyToID("_NyxCullParams");
            public static readonly int ShadowSweep = Shader.PropertyToID("_NyxShadowSweep");
            public static readonly int Time = Shader.PropertyToID("_NyxTime");
            public static readonly int RenderData = Shader.PropertyToID("_NyxRenderData");
            public static readonly int RenderIndex = Shader.PropertyToID("_NyxRenderIndex");
            public static readonly int BucketOffset = Shader.PropertyToID("_NyxBucketOffset");
            public static readonly int DeadVariant = Shader.PropertyToID("_NyxDeadVariant");
            public static readonly int ZonePointCount = Shader.PropertyToID("_NyxZonePointCount");
            public static readonly int ZoneVertices = Shader.PropertyToID("_NyxZoneVertices");
            public static readonly int ZoneBounds = Shader.PropertyToID("_NyxZoneBounds");
            public static readonly int ZoneHeight = Shader.PropertyToID("_NyxZoneHeight");
            public static readonly int ZoneNatural = Shader.PropertyToID("_NyxZoneNatural");
            public static readonly int ZoneOrigin = Shader.PropertyToID("_NyxZoneOrigin");
        }
    }
}
