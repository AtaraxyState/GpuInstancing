using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// Ce composant garde les réglages, l'API et le cycle de vie Unity, et orchestre des classes sans état Unity :
    ///  - zone : <see cref="ZoneVolume"/> (volume figé), <see cref="ZonePolygon"/> (contour), <see cref="ZoneEdge"/> (bord,
    ///    même calcul que le GPU), <see cref="ZoneGpu"/> (paramètres des noyaux) ;
    ///  - construction : <see cref="VariantPlan"/> (variantes), <see cref="PlacementSetup"/> (placement),
    ///    <see cref="InstanceBuilder"/> (buffer d'instances) avec <see cref="GpuPlacement"/> et <see cref="GpuVariantCounter"/> ;
    ///  - rendu : <see cref="InstanceRenderer"/> (par caméra) avec <see cref="CameraCulling"/>, <see cref="CullingBufferPool"/>,
    ///    <see cref="DrawBuckets"/> ; <see cref="DrawBounds"/> (bounds des draws).
    /// Chaque classe GPU a son compute shader ou sa bibliothèque HLSL (README, « Organisation du code »).
    ///
    /// Mémoire : CPU, rien de proportionnel au nombre d'instances n'est retenu (placements lus seulement à la demande) ;
    /// GPU, instances (64 o chacune) + un jeu de buffers de culling partagé par les caméras. Détails dans InstanceBuilder
    /// et InstanceRenderer.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Nyx/GPU Instancing/Mesh Spawner")]
    public sealed class MeshSpawner : MonoBehaviour
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

        /// <summary>Variantes maximum d'un spawner (= NYX_MAX_VARIANTS, InstancingVariants.hlsl).</summary>
        public const int MaxVariants = 64;

        /// <summary>
        /// Sommets maximum du polygone de la zone. Le test de la zone (placement, et à chaque image avec Clip To Volume) coûte
        /// en proportion du nombre de sommets : 64 sommets 0,7 ms, 1 024 sommets 8 ms pour 1 000 000 d'instances (GTX 1060).
        /// Pour un contour naturel, préférer quelques points + ZoneSmooth / ZoneEdgeNoise / ZoneEdgeFeather.
        /// </summary>
        public const int MaxZonePoints = 4096;

        /// <summary>Sommets du contour courbe (ZoneSmooth) : chaque segment est découpé jusqu'à ce total.</summary>
        public const int SmoothZonePoints = 64;

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
                 "plutôt que coupé net. 0 = bord net. Placement procédural seulement : une liste (placement figé compris) n'est " +
                 "pas clairsemée.")]
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

        readonly InstanceBuilder _builder = new InstanceBuilder();
        readonly InstanceRenderer _renderer = new InstanceRenderer();
        readonly DrawBounds _bounds = new DrawBounds();
        readonly List<Vector3> _anchorPositions = new List<Vector3>();   // ancres au dernier placement (suivies en mode édition)
        readonly List<string> _variantWarnings = new List<string>();
        MeshSpawnerBehavior _behavior;
        bool _built;
        bool _dirty = true;
        bool _warnedUnsupported;
        bool _warnedNoBehavior;

        // ------------------------------------------------------------------------------------------
        // API
        // ------------------------------------------------------------------------------------------

        public int InstanceCount { get; private set; }

        /// <summary>Draw calls par caméra pour la passe principale (hors cascades d'ombre). Indépendant du nombre d'instances.</summary>
        public int DrawCallsPerCamera => _renderer.Draws.Count;

        /// <summary>Mémoire GPU occupée : instances, jeux de buffers de culling, arguments de chaque caméra (octets).</summary>
        public long GpuMemoryBytes => _builder.GpuBytes + _renderer.GpuBytes;

        /// <summary>
        /// Mémoire native CPU retenue entre deux reconstructions (octets) : les placements, seulement s'ils ont été lus
        /// (Placements) ou fournis à un comportement qui écrit ses instances en C#. 0 sinon.
        /// </summary>
        public long CpuMemoryBytes => _builder.CpuMemoryBytes;

        /// <summary>Jeux de buffers de culling alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int CullingBufferSets => _renderer.BufferSets;

        /// <summary>Origine des instances de la dernière reconstruction : construites sur GPU, ou écrites en C# et envoyées (diagnostic).</summary>
        public UploadMethod LastUploadMethod => _builder.LastUploadMethod;

        /// <summary>
        /// Temps commun au culling GPU, aux vagues et aux émissions (s). Time.time ne repart pas de zéro
        /// au chargement d'une scène : les bulles en vol d'un spawner conservé restent cohérentes.
        /// </summary>
        public static float CurrentTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        /// <summary>
        /// Instances placées construites sur GPU (par défaut). false : les placements générés sur GPU sont lus par le CPU,
        /// le comportement écrit les instances en C# (CreateInstances), puis elles sont envoyées au GPU : bien plus lent, pour
        /// comparer ou déboguer. Un comportement qui surcharge CreateInstance ou CreateInstances passe toujours par le C#.
        /// </summary>
        public static bool CreateInstancesOnGpu { get; set; } = true;

        /// <summary>
        /// Lance 'kernel' avec un thread par instance (groupes de 64 threads, NYX_THREADS) : au-delà de 65 535 groupes, ils
        /// sont répartis en 2D et _NyxGroupsX permet au noyau de retrouver son index
        /// (index = (groupe.y × _NyxGroupsX + groupe.x) × 64 + thread). Pour les noyaux de construction des comportements.
        /// </summary>
        public static void DispatchInstances(ComputeShader shader, int kernel, int count) => SpawnerGpu.DispatchInstances(shader, kernel, count);

        /// <summary>Comportement actif (premier MeshSpawnerBehavior activé sur cet objet), null si aucun.</summary>
        public MeshSpawnerBehavior Behavior => FindBehavior();

        public IReadOnlyList<InstanceVariant> Variants => variants;

        /// <summary>Origine du placement : procédural, ou liste (SetPlacements, placement figé).</summary>
        public PlacementSource Source => source;

        /// <summary>
        /// Placements de la dernière reconstruction (vide si le comportement fournit ses propres instances). Construits sur
        /// GPU : à la première lecture après une reconstruction, le GPU refait le placement et le CPU le lit (bloquant,
        /// 28 o par instance gardés en mémoire native jusqu'à la prochaine reconstruction). Pour un placement en liste, la
        /// liste est relue telle qu'elle est au moment de la lecture.
        /// </summary>
        public IReadOnlyList<MeshPlacement> Placements => _builder.Placements;

        /// <summary>Libellé de chaque draw (« v0/LOD1 »...), dans l'ordre de TryReadVisibleCounts.</summary>
        public IReadOnlyList<string> DrawLabels => _renderer.Draws.Labels;

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
        public IReadOnlyList<Vector2> ZoneOutline => Zone.Outline;

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

        /// <summary>
        /// Lisière (m) : la densité du placement procédural baisse jusqu'à zéro sur cette distance avant le bord. Sans effet
        /// sur une liste (placement figé compris) : ses instances sont gardées telles quelles.
        /// </summary>
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

        /// <summary>Volume actuel de la zone (réglages et position de l'objet). Graine du bruit du bord : celle du placement, sur 24 bits.</summary>
        ZoneVolume Zone => new ZoneVolume(ZoneCenter, ZonePoints, zoneSmooth, zoneDepth, zoneEdgeNoise, zoneEdgeNoiseScale, zoneEdgeFeather,
                                          (uint)randomSeed & 0xFFFFFFu);

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
        public bool VolumeContains(Vector3 worldPosition) => Zone.Contains(worldPosition);

        /// <summary>Contour effectif en coordonnées monde (x, z).</summary>
        public List<Vector2> WorldOutline() => Zone.WorldOutline();

        /// <summary>
        /// Bord de la zone en (x, z) monde, comme sur GPU : &lt;= 0 dedans. Avec lisière : distance au bord, en mètres
        /// (la densité du placement vaut min(1, -bord / lisière)). Pour les outils (contour dans la vue Scene).
        /// </summary>
        public float ZoneEdgeAt(Vector2 worldXZ, IReadOnlyList<Vector2> worldOutline, bool withFeather) => Zone.Edge(worldXZ, worldOutline, withFeather);

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
            _builder.Instances.SetData(data, dataStart, start, count);
        }

        /// <summary>Bounds supplémentaires qui évoluent sans reconstruction (ex. émissions en vol). null = aucune.</summary>
        public void SetDynamicBounds(Bounds? bounds) => _bounds.SetDynamic(bounds);

        /// <summary>
        /// DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera'.
        /// Lecture GPU bloquante (attend la fin du GPU) : à ne jamais appeler en jeu.
        /// Renvoie false si cette caméra n'a encore jamais dessiné ce spawner.
        /// </summary>
        public bool TryReadVisibleCounts(Camera camera, List<int> countsPerDraw) => _renderer.TryReadVisibleCounts(camera, countsPerDraw);

        /// <summary>
        /// DIAGNOSTIC / TESTS : les destination.Length premières instances telles que le GPU les a reçues.
        /// Lecture GPU bloquante : à ne jamais appeler en jeu. false si le spawner n'est pas construit.
        /// </summary>
        public bool TryReadInstances(GpuInstanceData[] destination)
        {
            ComputeBuffer instances = _builder.Instances;
            if (!_built || destination == null || instances == null || !instances.IsValid())
                return false;
            instances.GetData(destination, 0, 0, Mathf.Min(destination.Length, InstanceCount));
            return true;
        }

        [ContextMenu("Figer le placement procédural dans la liste")]
        public void FreezePlacement()
        {
            if (!SystemInfo.supportsComputeShaders || !GpuPlacement.LoadShader())
            {
                Debug.LogError($"{name} : placement impossible sans compute shaders ({GpuPlacement.ShaderResource}).", this);
                return;
            }
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "Figer le placement");
#endif
            NativeArray<MeshPlacement> generated = GpuPlacement.ReadPlacements(PreparePlacement(), Allocator.Persistent);
            placements = new List<MeshPlacement>(generated.ToArray());
            generated.Dispose();
            source = PlacementSource.List;
            SetDirty();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
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
            _renderer.Release();
            _builder.Release();
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
            _renderer.EndFrame();
        }

        void OnCameraPreCull(Camera camera) => RenderForCamera(camera);

        void OnCameraPostRender(Camera camera) => _renderer.EndCamera(camera);

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera) => RenderForCamera(camera);

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera) => _renderer.EndCamera(camera);

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
            if (!isActiveAndEnabled)
                return;
            CullingSettings settings = ReadCullingSettings();
            if (!InstanceRenderer.ShouldRender(camera, settings))
                return;
            using ProfilerMarker.AutoScope profilerScope = SpawnerMarkers.Render.Auto();

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
            if (!_built || InstanceCount == 0 || _renderer.Draws.Count == 0 || behavior == null)
                return;

            _renderer.Render(this, camera, settings, behavior, _builder.Instances, InstanceCount, _bounds.World);
        }

        CullingSettings ReadCullingSettings() => new CullingSettings
        {
            renderLayer = renderLayer,
            castShadows = castShadows,
            receiveShadows = receiveShadows,
            excludeFromDepthTexture = excludeFromDepthTexture,
            maxDrawDistance = maxDrawDistance,
            lodBias = lodBias,
            minScreenSize = minScreenSize,
            shadowCullingExtrusion = shadowCullingExtrusion,
            shadowLight = shadowLight,
            stereoInstancing = stereoInstancing,
            renderInSceneView = renderInSceneView,
            logVisibleCounts = logVisibleCounts
        };

        // ------------------------------------------------------------------------------------------
        // Reconstruction (toujours hors rendu, sauf la toute première)
        // ------------------------------------------------------------------------------------------

        void Rebuild()
        {
            using ProfilerMarker.AutoScope profilerScope = SpawnerMarkers.Rebuild.Auto();
            ReleaseRenderResources();
            _dirty = false;
            _builder.BeginRebuild();

            MeshSpawnerBehavior behavior = FindBehavior();
            if (behavior == null)
            {
                if (!_warnedNoBehavior)
                    Debug.LogWarning($"{name} : aucun comportement (MeshSpawnerBehavior) sur l'objet, rien n'est dessiné. " +
                                     "Ajouter par exemple Static Instances, Float On Waves ou Bubble Columns.", this);
                _warnedNoBehavior = true;
                _builder.Release();
                return;
            }
            _warnedNoBehavior = false;
            if (!SystemInfo.supportsComputeShaders)
                return;   // signalé au rendu : rien ne serait dessiné
            if (!InstanceBuilder.LoadShaders())
            {
                Debug.LogError($"{name} : compute shaders {InstanceBuilder.ShaderResources} introuvables ou invalides (dossier Resources du package).", this);
                return;
            }
            if (!_renderer.UseShader(behavior.CullingShader))
            {
                Debug.LogError($"{name} : le comportement {behavior.GetType().Name} ne fournit pas de compute shader de culling.", this);
                return;
            }
            VariantPlan plan = PlanVariants();
            if (plan == null)
                return;

            _renderer.SetZone(clipToVolume ? Zone.ToGpu(withFeather: false) : ZoneGpu.Disabled());   // destruction : bord, sans lisière
            MotionExceedsVolumeHeight = false;

            // Instances construites sur GPU (ou écrites en C# puis envoyées), et leur nombre par variante. Une variante
            // invalide (mesh/matériau manquant) pointe vers une variante "désactivée" : les index restent stables.
            var perVariant = new NativeArray<int>(plan.Count, Allocator.Temp);
            try
            {
                int instanceCount;
                using (SpawnerMarkers.BuildInstances.Auto())
                    instanceCount = behavior.UsesPlacement ? BuildPlacedInstances(behavior, plan, perVariant) : BuildBehaviorInstances(behavior, plan, perVariant);
                _renderer.Draws.Build(variants, plan, perVariant, behavior, boundsPadding, excludeFromDepthTexture);
                InstanceCount = instanceCount;
                _built = true;
            }
            finally
            {
                perVariant.Dispose();
            }
        }

        /// <summary>Variantes de la reconstruction. null, avec une erreur, si les limites de variantes ou de draws sont dépassées.</summary>
        VariantPlan PlanVariants()
        {
            _variantWarnings.Clear();
            VariantPlan plan = VariantPlan.Create(variants, _variantWarnings, out string error);
            foreach (string warning in _variantWarnings)
                Debug.LogWarning($"{name} : {warning}", this);
            if (plan == null)
                Debug.LogError($"{name} : {error}", this);
            return plan;
        }

        /// <summary>
        /// Instances du placement (zone, amas, liste). Bounds : boîte des pivots élargie de la taille des meshes et du mouvement
        /// du comportement ; avec Clip To Volume, les draws sont en plus limités au volume.
        /// </summary>
        int BuildPlacedInstances(MeshSpawnerBehavior behavior, VariantPlan plan, NativeArray<int> perVariant)
        {
            int count = _builder.BuildPlaced(behavior, PreparePlacement(), plan, perVariant, out Bounds positions, out float maxScale);
            if (count == 0)
            {
                _bounds.SetEmpty(transform.position);
                return 0;
            }
            float meshExtent = (MaxVariantRadius() + boundsPadding) * maxScale;
            MotionExceedsVolumeHeight = _bounds.SetPlaced(behavior, positions, meshExtent, clipToVolume ? Zone.WorldBounds() : (Bounds?)null);
            return count;
        }

        /// <summary>Instances propres au comportement (UsesPlacement = false), avec leurs bounds.</summary>
        int BuildBehaviorInstances(MeshSpawnerBehavior behavior, VariantPlan plan, NativeArray<int> perVariant)
        {
            int count = _builder.BuildFromBehavior(behavior, plan, perVariant, out Bounds bounds);
            _bounds.SetBehavior(bounds);
            return count;
        }

        /// <summary>Paramètres du placement d'après les réglages. Retient la position des ancres (suivies en mode édition).</summary>
        PlacementSetup PreparePlacement()
        {
            _anchorPositions.Clear();
            foreach (Transform anchor in clusterAnchors)
                _anchorPositions.Add(anchor != null ? anchor.position : Vector3.zero);

            var settings = new PlacementSettings
            {
                fromList = source == PlacementSource.List,
                list = placements,
                seed = randomSeed,
                variantCount = variants.Count,
                densityPer100m2 = densityPer100m2,
                scaleRange = scaleRange,
                clusterAnchors = clusterAnchors,
                clusterRadius = clusterRadius,
                clumpsPerCluster = clumpsPerCluster,
                clip = clipToVolume
            };
            return PlacementSetup.Create(Zone, settings, this);
        }

        /// <summary>Draws et ressources des caméras (le buffer d'instances est gardé pour être réutilisé).</summary>
        void ReleaseRenderResources()
        {
            _renderer.ReleaseBuffers();
            InstanceCount = 0;
            _built = false;
            _dirty = true;
        }
    }
}
