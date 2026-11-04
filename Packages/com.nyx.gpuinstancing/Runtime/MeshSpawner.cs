using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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
    /// Mémoire :
    ///  - CPU : construction dans des NativeArray par des jobs Burst, libérées dès l'envoi au GPU. Seuls les placements
    ///    restent (28 octets par instance, mémoire native) ; rien de proportionnel au nombre d'instances dans le tas managé.
    ///  - GPU : instances (64 o chacune) + un jeu de buffers de culling partagé par les caméras rendues l'une après
    ///    l'autre (visibles × 64 o + index par draw × 4 o). Une caméra rendue PENDANT le rendu d'une autre (réflexion
    ///    d'eau) prend un second jeu : les draws de la première gardent leurs données.
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

        public const int MaxLods = 4;
        public const int MaxBuckets = 64;           // = NYX_MAX_BUCKETS (InstancingCulling.hlsl)
        const int ThreadGroupSize = 64;             // = NYX_THREADS
        const int MaxGroupsPerDimension = 65535;
        const int ArgsPerDraw = 5;
        const int MaxGeneratedInstances = 2_000_000;

        [Header("Meshes")]
        [Tooltip("Variantes : 1 à 4 meshes (LOD0 en premier), distances de LOD, matériau compatible avec le comportement.")]
        [SerializeField] List<InstanceVariant> variants = new List<InstanceVariant>();

        [Header("Placement (si le comportement l'utilise)")]
        [SerializeField] PlacementSource source = PlacementSource.Procedural;
        [SerializeField] List<MeshPlacement> placements = new List<MeshPlacement>();
        [SerializeField] int randomSeed = 1234;
        [Tooltip("Zone rectangulaire (m) centrée sur cet objet, à sa hauteur.")]
        [SerializeField] Vector2 areaSize = new Vector2(200f, 200f);
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

        /// <summary>Vue en lecture seule des placements (mémoire native du spawner), sans copie.</summary>
        sealed class PlacementList : IReadOnlyList<MeshPlacement>
        {
            readonly MeshSpawner _owner;

            public PlacementList(MeshSpawner owner) => _owner = owner;

            public int Count => _owner._placements.IsCreated ? _owner._placements.Length : 0;

            public MeshPlacement this[int index] => _owner._placements[index];

            public IEnumerator<MeshPlacement> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
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

        /// <summary>Mémoire native CPU retenue entre deux reconstructions : les placements (octets).</summary>
        public long CpuMemoryBytes => _placements.IsCreated ? (long)_placements.Length * System.Runtime.InteropServices.Marshal.SizeOf<MeshPlacement>() : 0L;

        /// <summary>Jeux de buffers de culling alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int CullingBufferSets => _allBuffers.Count;

        /// <summary>
        /// Temps commun au culling GPU, aux vagues et aux émissions (s). Time.time ne repart pas de zéro
        /// au chargement d'une scène : les bulles en vol d'un spawner conservé restent cohérentes.
        /// </summary>
        public static float CurrentTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        /// <summary>Comportement actif (premier MeshSpawnerBehavior activé sur cet objet), null si aucun.</summary>
        public MeshSpawnerBehavior Behavior => FindBehavior();

        public IReadOnlyList<InstanceVariant> Variants => variants;

        /// <summary>
        /// Placements de la dernière reconstruction (vide si le comportement fournit ses propres instances). Lus sans copie
        /// dans la mémoire native du spawner : valables jusqu'à la prochaine reconstruction.
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

        [ContextMenu("Figer le placement procédural dans la liste")]
        public void FreezePlacement()
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "Figer le placement");
#endif
            GeneratePlacements();
            placements = new List<MeshPlacement>(_placements.Length);
            for (int i = 0; i < _placements.Length; i++)
                placements.Add(_placements[i]);
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
            DisposePlacements();
        }

        void OnValidate()
        {
            if (scaleRange.y < scaleRange.x)
                scaleRange.y = scaleRange.x;
            SetDirty();
        }

        void Update()
        {
            // Mode édition uniquement : le placement suit l'objet et les ancres pendant que le level designer les déplace.
            // En jeu, le placement est figé (une mine qui flotte ne doit pas régénérer le champ à chaque frame) :
            // appeler SetDirty() après un déplacement volontaire.
            if (Application.isPlaying || source != PlacementSource.Procedural)
                return;
            MeshSpawnerBehavior behavior = FindBehavior();
            if (behavior == null || !behavior.UsesPlacement)
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
            behavior.SetShaderParameters(cs, camera);

            cs.SetBuffer(_kernelCull, ShaderIds.Instances, _instanceBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Variants, _variantBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.BucketOffsets, _bucketOffsetBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Visible, buffers.visible);
            cs.SetBuffer(_kernelCull, ShaderIds.VisibleIndex, buffers.visibleIndex);
            cs.SetBuffer(_kernelCull, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Args, state.args);

            int groups = (InstanceCount + ThreadGroupSize - 1) / ThreadGroupSize;
            int groupsX = Mathf.Min(groups, MaxGroupsPerDimension);
            int groupsY = (groups + groupsX - 1) / groupsX;
            cs.SetInt(ShaderIds.GroupsX, groupsX);
            cs.Dispatch(_kernelCull, groupsX, groupsY, 1);
            cs.Dispatch(_kernelWriteArgs, 1, 1, 1);   // _bucketCount <= 64 : un seul groupe

            // En Single Pass Instanced, le nombre d'instances est doublé pour les passes caméra, mais la carte
            // d'ombre n'est pas stéréo : elle utilise son propre bloc d'arguments (non doublé) via un draw ShadowsOnly.
            bool shadows = castShadows != ShadowCastingMode.Off;
            bool splitShadows = shadows && multiplier > 1;
            ShadowCastingMode mainShadows = splitShadows ? ShadowCastingMode.Off : castShadows;
            for (int b = 0; b < _bucketCount; b++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    _bucketMeshes[b], _bucketSubMeshes[b], _bucketMaterials[b], _worldBounds,
                    state.args, b * ArgsPerDraw * sizeof(uint), buffers.blocks[b],
                    mainShadows, receiveShadows, renderLayer, camera, LightProbeUsage.Off);

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
        }

        // ------------------------------------------------------------------------------------------
        // Construction (toujours hors rendu, sauf la toute première)
        // ------------------------------------------------------------------------------------------

        void Rebuild()
        {
            ReleaseRenderResources();
            _dirty = false;

            MeshSpawnerBehavior behavior = FindBehavior();
            if (behavior == null)
            {
                if (!_warnedNoBehavior)
                    Debug.LogWarning($"{name} : aucun comportement (MeshSpawnerBehavior) sur l'objet, rien n'est dessiné. " +
                                     "Ajouter par exemple Static Instances, Float On Waves ou Bubble Columns.", this);
                _warnedNoBehavior = true;
                DisposePlacements();
                ReleaseInstanceBuffer();
                return;
            }
            _warnedNoBehavior = false;
            _cullingShader = behavior.CullingShader;
            if (_cullingShader == null)
            {
                Debug.LogError($"{name} : le comportement {behavior.GetType().Name} ne fournit pas de compute shader de culling.", this);
                return;
            }
            _kernelCull = _cullingShader.FindKernel("NyxCull");
            _kernelWriteArgs = _cullingShader.FindKernel("NyxWriteArgs");

            int variantCount = variants.Count;
            var lodCounts = new int[variantCount];
            for (int v = 0; v < variantCount; v++)
            {
                lodCounts[v] = LodCount(variants[v]);
                // Avec « Instancing Variants : Strip Unused » (réglage par défaut), un build supprime les variantes
                // instanciées des shaders dont aucun matériau n'a l'instancing coché : rien ne s'afficherait.
                if (lodCounts[v] > 0 && !variants[v].material.enableInstancing)
                    Debug.LogWarning($"{name} : cochez « Enable GPU Instancing » sur le matériau '{variants[v].material.name}', sinon rien ne s'affichera dans les builds.", this);
            }

            // 1. Instances, construites en mémoire native (jobs Burst) et libérées dès l'envoi au GPU
            NativeArray<GpuInstanceData> instances = default;
            var perVariant = new NativeArray<int>(variantCount, Allocator.TempJob);
            var validVariants = new NativeArray<int>(variantCount, Allocator.TempJob);
            try
            {
                if (behavior.UsesPlacement)
                {
                    GeneratePlacements();
                    instances = new NativeArray<GpuInstanceData>(_placements.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    _buildBounds = new Bounds(transform.position, Vector3.one);
                    if (_placements.Length > 0)
                    {
                        behavior.CreateInstances(_placements, instances);
                        _buildBounds = behavior.GetPlacementBounds(PlacementBounds(out float maxScale), (MaxVariantRadius() + boundsPadding) * maxScale);
                    }
                }
                else
                {
                    DisposePlacements();
                    instances = new NativeArray<GpuInstanceData>(Mathf.Max(0, behavior.GetInstanceCount()), Allocator.TempJob);
                    _buildBounds = behavior.BuildInstances(instances);
                }
                UpdateWorldBounds();

                // 2. Instances par variante ; une variante invalide (mesh/matériau manquant) pointe vers une variante
                //    "désactivée" : les index restent stables pour les mises à jour partielles.
                for (int v = 0; v < variantCount; v++)
                    validVariants[v] = lodCounts[v] > 0 ? 1 : 0;
                new MeshSpawnerJobs.CountVariantsJob
                {
                    instances = instances,
                    validVariants = validVariants,
                    perVariant = perVariant,
                    disabledVariant = variantCount
                }.Run();

                if (!BuildBuckets(behavior, lodCounts, perVariant))
                    return;

                // 3. Envoi au GPU (buffer réutilisé si le nombre d'instances n'a pas changé)
                int instanceCount = instances.Length;
                if (_instanceBuffer == null || !_instanceBuffer.IsValid() || _instanceBuffer.count != Mathf.Max(instanceCount, 1))
                {
                    ReleaseInstanceBuffer();
                    _instanceBuffer = new ComputeBuffer(Mathf.Max(instanceCount, 1), GpuInstanceData.Stride, ComputeBufferType.Default);
                }
                if (instanceCount > 0)
                    _instanceBuffer.SetData(instances);
                InstanceCount = instanceCount;
                _built = true;
            }
            finally
            {
                if (instances.IsCreated)
                    instances.Dispose();
                perVariant.Dispose();
                validVariants.Dispose();
            }
        }

        /// <summary>
        /// Un "bucket" = un draw = (variante, LOD). Chaque draw a sa zone d'index (4 octets par instance), dimensionnée pour
        /// le pire cas (toutes les instances de la variante dans ce LOD) ; les données de rendu (64 octets) ne sont écrites
        /// qu'une fois par instance visible, toutes variantes confondues.
        /// </summary>
        bool BuildBuckets(MeshSpawnerBehavior behavior, int[] lodCounts, NativeArray<int> perVariant)
        {
            int variantCount = lodCounts.Length;
            int bucketCount = 0;
            for (int v = 0; v < variantCount; v++)
                bucketCount += lodCounts[v];
            if (bucketCount > MaxBuckets)
            {
                Debug.LogError($"{name} : {bucketCount} draws (variantes × LODs) > {MaxBuckets}. Réduisez les variantes ou les LODs.", this);
                return false;
            }

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
                    _bucketMaterials[bucket] = variant.material;
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
            return true;
        }

        /// <summary>Placements en mémoire native : générés par un job Burst, ou recopiés de la liste sérialisée.</summary>
        void GeneratePlacements()
        {
            DisposePlacements();
            int variantCount = Mathf.Max(variants.Count, 1);
            Vector3 center = transform.position;

            _anchorPositions.Clear();
            foreach (Transform anchor in clusterAnchors)
                _anchorPositions.Add(anchor != null ? anchor.position : Vector3.zero);

            if (source == PlacementSource.List)
            {
                _placements = new NativeArray<MeshPlacement>(placements.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < placements.Count; i++)
                {
                    MeshPlacement placement = placements[i];
                    placement.variant = Mathf.Clamp(placement.variant, 0, variantCount - 1);
                    _placements[i] = placement;
                }
                return;
            }

            int cellsX = 0, cellsZ = 0;
            float stepX = 0f, stepZ = 0f;
            if (densityPer100m2 > 0f && areaSize.x > 0f && areaSize.y > 0f)
            {
                float cell = Mathf.Sqrt(100f / densityPer100m2);
                cellsX = Mathf.Max(1, Mathf.RoundToInt(areaSize.x / cell));
                cellsZ = Mathf.Max(1, Mathf.RoundToInt(areaSize.y / cell));
                if ((long)cellsX * cellsZ > MaxGeneratedInstances)
                {
                    Debug.LogWarning($"{name} : {(long)cellsX * cellsZ} instances demandées, limité à {MaxGeneratedInstances}.", this);
                    float reduction = Mathf.Sqrt((float)MaxGeneratedInstances / ((long)cellsX * cellsZ));
                    cellsX = Mathf.Max(1, (int)(cellsX * reduction));
                    cellsZ = Mathf.Max(1, (int)(cellsZ * reduction));
                }
                stepX = areaSize.x / cellsX;
                stepZ = areaSize.y / cellsZ;
            }

            var anchors = new NativeArray<float3>(clusterAnchors.Count, Allocator.TempJob);
            int anchorCount = 0;
            foreach (Transform anchor in clusterAnchors)
            {
                if (anchor != null)
                    anchors[anchorCount++] = anchor.position;
            }
            int gridCount = cellsX * cellsZ;
            int total = gridCount + anchorCount * clumpsPerCluster;
            _placements = new NativeArray<MeshPlacement>(total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            new MeshSpawnerJobs.GeneratePlacementsJob
            {
                output = _placements,
                anchors = anchors,
                seed = (uint)randomSeed,
                cellsX = Mathf.Max(cellsX, 1),
                gridCount = gridCount,
                origin = new float2(center.x - areaSize.x * 0.5f, center.z - areaSize.y * 0.5f),
                step = new float2(stepX, stepZ),
                height = center.y,
                clumpsPerCluster = Mathf.Max(clumpsPerCluster, 1),
                clusterRadius = clusterRadius,
                scaleRange = new float2(scaleRange.x, scaleRange.y),
                variantCount = variantCount
            }.Schedule(total, MeshSpawnerJobs.BatchSize).Complete();
            anchors.Dispose();
        }

        /// <summary>Boîte des positions au repos des placements (job Burst) et plus grande échelle.</summary>
        Bounds PlacementBounds(out float maxScale)
        {
            var result = new NativeArray<float4>(2, Allocator.TempJob);
            new MeshSpawnerJobs.PlacementBoundsJob { placements = _placements, result = result }.Run();
            var bounds = new Bounds();
            bounds.SetMinMax(result[0].xyz, result[1].xyz);
            maxScale = result[1].w;
            result.Dispose();
            return bounds;
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
        }

        void ReleaseInstanceBuffer()
        {
            _instanceBuffer?.Release();
            _instanceBuffer = null;
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
        }
    }
}
