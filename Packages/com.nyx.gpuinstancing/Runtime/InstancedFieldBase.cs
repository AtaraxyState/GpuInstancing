using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Cœur de l'instanciateur GPU (Unity 2021.3, pipeline Built-in).
    /// Par caméra et par frame : 1 dispatch de culling (frustum des 2 yeux, distance, petits objets, LOD)
    /// + 1 dispatch d'arguments, puis 1 Graphics.DrawMeshInstancedIndirect par (variante × LOD).
    /// Le nombre de draw calls ne dépend PAS du nombre d'instances.
    /// </summary>
    [ExecuteAlways]
    public abstract class InstancedFieldBase : MonoBehaviour
    {
        public const int MaxLods = 4;
        public const int MaxBuckets = 64;           // = NYX_MAX_BUCKETS (InstancingCulling.hlsl)
        const int ThreadGroupSize = 64;             // = NYX_THREADS
        const int MaxGroupsPerDimension = 65535;
        const int ArgsPerDraw = 5;

        [Header("Rendu")]
        [Tooltip("Vide = chargé automatiquement depuis un dossier Resources.")]
        [SerializeField] protected ComputeShader cullingShader;
        [SerializeField] protected int renderLayer;
        [SerializeField] protected ShadowCastingMode castShadows = ShadowCastingMode.Off;
        [SerializeField] protected bool receiveShadows = true;

        [Header("Culling GPU")]
        [Tooltip("Distance max d'affichage (m). Sous l'eau, inutile de dépasser la visibilité du brouillard.")]
        [SerializeField, Min(1f)] protected float maxDrawDistance = 150f;
        [Tooltip("> 1 : les LODs détaillés restent visibles plus loin. < 1 : bascule plus tôt.")]
        [SerializeField, Min(0.01f)] protected float lodBias = 1f;
        [Tooltip("Rayon projeté minimal, en fraction de la demi-hauteur d'écran. 0,001 ≈ 1 px pour 2000 px de haut.")]
        [SerializeField, Range(0f, 0.02f)] protected float minScreenSize = 0.001f;
        [Tooltip("Marge ajoutée aux bounds (espace local du mesh) pour couvrir la déformation du vertex shader.")]
        [SerializeField, Min(0f)] protected float boundsPadding = 0.1f;
        [Tooltip("Avec ombres : extrusion (m) des bounds le long de la lumière, pour garder les instances hors champ dont l'ombre est visible.")]
        [SerializeField, Min(0f)] protected float shadowCullingExtrusion;
        [Tooltip("Lumière des ombres. Vide = soleil de la scène (Lighting > Sun Source).")]
        [SerializeField] protected Light shadowLight;

        [Header("Caméras / VR")]
        [SerializeField] protected StereoInstancingMode stereoInstancing = StereoInstancingMode.Auto;
        [SerializeField] protected bool renderInSceneView = true;
        [Tooltip("Log 1x/s du nombre d'instances visibles par draw (lecture GPU asynchrone, sans blocage).")]
        [SerializeField] protected bool logVisibleCounts;

        /// <summary>
        /// Buffers propres à une caméra. Indispensable : une caméra peut en rendre une autre PENDANT son propre
        /// rendu (réflexion d'eau via Camera.Render) ; avec des buffers partagés, la seconde écraserait les
        /// résultats de culling dont les draws de la première ont encore besoin.
        /// </summary>
        sealed class CameraResources
        {
            public ComputeBuffer visible;     // instances visibles compactées, une zone par draw
            public ComputeBuffer counters;    // compteurs par draw (remis à zéro par NyxWriteArgs)
            public ComputeBuffer args;        // arguments indirects : bloc caméra puis bloc ombre
            public MaterialPropertyBlock[] blocks;
            public uint lastMultiplier = 1;   // diagnostic : multiplicateur VR du dernier rendu
            public bool skippedLastRender;    // diagnostic : champ rejeté en entier côté CPU au dernier rendu

            public long Bytes => Size(visible) + Size(counters) + Size(args);

            public void Release()
            {
                visible?.Release();
                counters?.Release();
                args?.Release();
                visible = counters = args = null;
            }
        }

        // --- Données communes à toutes les caméras (reconstruites hors rendu) ---
        ComputeBuffer _instanceBuffer;
        ComputeBuffer _variantBuffer;
        ComputeBuffer _bucketOffsetBuffer;
        Mesh[] _bucketMeshes = Array.Empty<Mesh>();
        Material[] _bucketMaterials = Array.Empty<Material>();
        int[] _bucketSubMeshes = Array.Empty<int>();
        string[] _bucketLabels = Array.Empty<string>();
        uint[] _bucketOffsets = Array.Empty<uint>();
        uint[] _argsTemplate = Array.Empty<uint>();
        int _visibleSlots;
        int _bucketCount;
        int _kernelCull = -1;
        int _kernelWriteArgs = -1;
        bool _built;
        bool _dirty = true;
        bool _warnedUnsupported;
        float _nextLogTime;
        Bounds _buildBounds;
        Bounds? _dynamicBounds;
        Bounds _worldBounds;
        readonly Dictionary<Camera, CameraResources> _cameraResources = new Dictionary<Camera, CameraResources>();
        readonly List<Camera> _deadCameras = new List<Camera>();
        readonly Vector4[] _planes = new Vector4[12];
        readonly List<GpuInstanceData> _buildList = new List<GpuInstanceData>();

        /// <summary>Copie CPU des données d'instance, dans l'ordre exact du buffer GPU (mises à jour partielles).</summary>
        protected GpuInstanceData[] instances = Array.Empty<GpuInstanceData>();

        public int InstanceCount { get; private set; }

        /// <summary>Draw calls par caméra pour la passe principale (hors cascades d'ombre). Indépendant du nombre d'instances.</summary>
        public int DrawCallsPerCamera => _bucketCount;

        /// <summary>Mémoire GPU occupée par ce champ, toutes caméras comprises (octets).</summary>
        public long GpuMemoryBytes
        {
            get
            {
                long bytes = Size(_instanceBuffer) + Size(_variantBuffer) + Size(_bucketOffsetBuffer);
                foreach (CameraResources resources in _cameraResources.Values)
                    bytes += resources.Bytes;
                return bytes;
            }
        }

        /// <summary>
        /// Temps commun au culling GPU, aux vagues et aux émissions (s). Time.time ne repart pas de zéro
        /// au chargement d'une scène : les bulles en vol d'un champ conservé restent cohérentes.
        /// </summary>
        public static float CurrentTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        /// <summary>Buffer d'instances à jour : les mises à jour partielles (UploadInstances) sont possibles.</summary>
        protected bool IsBuilt => _built && !_dirty;

        // ------------------------------------------------------------------------------------------
        // À fournir par chaque système
        // ------------------------------------------------------------------------------------------

        /// <summary>Nom du compute shader de culling dans un dossier Resources.</summary>
        protected abstract string DefaultCullingShaderName { get; }

        /// <summary>Variantes (mesh + LODs + matériau). L'index dans cette liste = custom.w des instances.</summary>
        protected abstract IReadOnlyList<InstanceVariant> GetVariants();

        /// <summary>
        /// Remplit la liste des instances et renvoie des bounds monde englobant TOUTES les positions possibles
        /// (animation comprise). Appelé à chaque reconstruction, jamais pendant un rendu.
        /// </summary>
        protected abstract Bounds BuildInstances(List<GpuInstanceData> output);

        /// <summary>Paramètres propres au système (vagues, forme...), posés avant chaque dispatch.</summary>
        protected virtual void SetSystemParameters(ComputeShader shader, Camera camera) { }

        /// <summary>Marge (espace local) ajoutée aux bounds d'une variante pour couvrir la déformation du vertex shader.</summary>
        protected virtual float GetBoundsPadding(InstanceVariant variant, Bounds localBounds) => boundsPadding;

        // ------------------------------------------------------------------------------------------
        // API
        // ------------------------------------------------------------------------------------------

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

        /// <summary>Envoie au GPU une plage de 'instances' modifiée (count × 64 octets). Sans effet si pas à jour.</summary>
        protected void UploadInstances(int start, int count)
        {
            if (!IsBuilt || count <= 0 || start < 0 || start + count > InstanceCount)
                return;
            _instanceBuffer.SetData(instances, start, start, count);
        }

        /// <summary>Reconstruction immédiate (tests, création par code). Jamais pendant un rendu : préférer SetDirty().</summary>
        public void RebuildNow() => Rebuild();

        /// <summary>Libellé de chaque draw (« v0/LOD1 »...), dans l'ordre de TryReadVisibleCounts.</summary>
        public IReadOnlyList<string> DrawLabels => _bucketLabels;

        /// <summary>
        /// DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera'.
        /// Lecture GPU bloquante (attend la fin du GPU) : à ne jamais appeler en jeu.
        /// Renvoie false si cette caméra n'a encore jamais dessiné ce champ.
        /// </summary>
        public bool TryReadVisibleCounts(Camera camera, List<int> countsPerDraw)
        {
            countsPerDraw.Clear();
            if (camera == null || !_cameraResources.TryGetValue(camera, out CameraResources resources) || resources.args == null)
                return false;

            var args = new uint[resources.args.count];
            if (!resources.skippedLastRender)
                resources.args.GetData(args);
            uint multiplier = resources.lastMultiplier == 0 ? 1u : resources.lastMultiplier;
            for (int b = 0; b < _bucketCount; b++)
                countsPerDraw.Add((int)(args[b * ArgsPerDraw + 1] / multiplier));
            return true;
        }

        /// <summary>Bounds supplémentaires qui évoluent sans reconstruction (ex. émissions ponctuelles en vol). null = aucune.</summary>
        protected void SetDynamicBounds(Bounds? bounds)
        {
            _dynamicBounds = bounds;
            UpdateWorldBounds();
        }

        // ------------------------------------------------------------------------------------------
        // Cycle de vie
        // ------------------------------------------------------------------------------------------

        protected virtual void Reset() => cullingShader = Resources.Load<ComputeShader>(DefaultCullingShaderName);

        protected virtual void OnEnable()
        {
            SetDirty();
            Camera.onPreCull += OnCameraPreCull;                                    // Built-in
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;   // SRP (les shaders fournis restent Built-in)
            OceanWaves.Changed += SetDirty;
        }

        protected virtual void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            OceanWaves.Changed -= SetDirty;
            ReleaseAll();
        }

        protected virtual void OnValidate() => SetDirty();

        /// <summary>Reconstruction différée + libération des ressources des caméras détruites. Toujours hors rendu.</summary>
        protected virtual void LateUpdate()
        {
            if (_dirty)
                Rebuild();

            foreach (KeyValuePair<Camera, CameraResources> pair in _cameraResources)
            {
                if (pair.Key == null)
                    _deadCameras.Add(pair.Key);
            }
            foreach (Camera camera in _deadCameras)
            {
                _cameraResources[camera].Release();
                _cameraResources.Remove(camera);
            }
            _deadCameras.Clear();
        }

        void OnCameraPreCull(Camera camera) => RenderForCamera(camera);

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera) => RenderForCamera(camera);

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

            // Première construction seulement : aucun draw de ce champ n'est encore en vol.
            // Les reconstructions suivantes attendent LateUpdate (on dessine avec les données courantes d'ici là).
            // Après un échec (_dirty retombé à false), on ne réessaie qu'au prochain SetDirty : pas d'erreur à chaque frame.
            if (!_built && _dirty)
                Rebuild();
            if (!_built || InstanceCount == 0 || _bucketCount == 0)
                return;

            ComputeShader cs = cullingShader;
            uint multiplier = GetInstanceMultiplier(camera);
            bool stereo = SetCameraParameters(cs, camera, multiplier, out Vector3 shadowSweep);

            // Champ entièrement hors du frustum ou trop loin : ni dispatch, ni draw.
            Bounds cullBounds = _worldBounds;
            cullBounds.center += shadowSweep;
            cullBounds.extents += Abs(shadowSweep);
            if (!BoundsVisible(cullBounds, stereo, camera.transform.position))
            {
                if (_cameraResources.TryGetValue(camera, out CameraResources skipped))
                    skipped.skippedLastRender = true;
                return;
            }

            CameraResources resources = GetCameraResources(camera);
            resources.lastMultiplier = multiplier;
            resources.skippedLastRender = false;

            cs.SetFloat(ShaderIds.Time, CurrentTime);
            cs.SetInt(ShaderIds.InstanceCount, InstanceCount);
            cs.SetInt(ShaderIds.BucketCount, _bucketCount);
            SetSystemParameters(cs, camera);

            cs.SetBuffer(_kernelCull, ShaderIds.Instances, _instanceBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Variants, _variantBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.BucketOffsets, _bucketOffsetBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Visible, resources.visible);
            cs.SetBuffer(_kernelCull, ShaderIds.Counters, resources.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Counters, resources.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Args, resources.args);

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
                    resources.args, b * ArgsPerDraw * sizeof(uint), resources.blocks[b],
                    mainShadows, receiveShadows, renderLayer, camera, LightProbeUsage.Off);

                if (splitShadows)
                {
                    Graphics.DrawMeshInstancedIndirect(
                        _bucketMeshes[b], _bucketSubMeshes[b], _bucketMaterials[b], _worldBounds,
                        resources.args, (_bucketCount + b) * ArgsPerDraw * sizeof(uint), resources.blocks[b],
                        ShadowCastingMode.ShadowsOnly, false, renderLayer, camera, LightProbeUsage.Off);
                }
            }

            if (logVisibleCounts)
                LogVisibleCounts(camera, resources, multiplier);
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

        CameraResources GetCameraResources(Camera camera)
        {
            if (_cameraResources.TryGetValue(camera, out CameraResources resources))
                return resources;

            // Création seulement (jamais de libération ici : on peut être au milieu du rendu d'une autre caméra)
            resources = new CameraResources
            {
                visible = new ComputeBuffer(Mathf.Max(_visibleSlots, 1), GpuInstanceData.Stride, ComputeBufferType.Default),
                counters = new ComputeBuffer(Mathf.Max(_bucketCount, 1), sizeof(uint), ComputeBufferType.Default),
                args = new ComputeBuffer(_argsTemplate.Length, sizeof(uint), ComputeBufferType.IndirectArguments),
                blocks = new MaterialPropertyBlock[_bucketCount]
            };
            resources.counters.SetData(new uint[Mathf.Max(_bucketCount, 1)]);
            resources.args.SetData(_argsTemplate);
            for (int b = 0; b < _bucketCount; b++)
            {
                var block = new MaterialPropertyBlock();
                block.SetBuffer(ShaderIds.RenderData, resources.visible);
                block.SetFloat(ShaderIds.BucketOffset, _bucketOffsets[b]);
                resources.blocks[b] = block;
            }
            _cameraResources.Add(camera, resources);
            return resources;
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
            _dirty = false;
            ReleaseAll();
            _dirty = false;

            if (cullingShader == null)
                cullingShader = Resources.Load<ComputeShader>(DefaultCullingShaderName);
            if (cullingShader == null)
            {
                Debug.LogError($"{name} : compute shader '{DefaultCullingShaderName}' introuvable (Resources).", this);
                return;
            }
            _kernelCull = cullingShader.FindKernel("NyxCull");
            _kernelWriteArgs = cullingShader.FindKernel("NyxWriteArgs");

            IReadOnlyList<InstanceVariant> variants = GetVariants() ?? Array.Empty<InstanceVariant>();
            int variantCount = variants.Count;

            _buildList.Clear();
            _buildBounds = BuildInstances(_buildList);
            UpdateWorldBounds();

            // 1. Instances par variante. Une variante invalide (mesh/matériau manquant) pointe vers une
            //    variante "désactivée" : les index restent stables pour les mises à jour partielles.
            var lodCounts = new int[variantCount];
            for (int v = 0; v < variantCount; v++)
            {
                lodCounts[v] = LodCount(variants[v]);
                // Avec « Instancing Variants : Strip Unused » (réglage par défaut), un build supprime les variantes
                // instanciées des shaders dont aucun matériau n'a l'instancing coché : rien ne s'afficherait.
                if (lodCounts[v] > 0 && !variants[v].material.enableInstancing)
                    Debug.LogWarning($"{name} : cochez « Enable GPU Instancing » sur le matériau '{variants[v].material.name}', sinon rien ne s'affichera dans les builds.", this);
            }

            var perVariant = new int[variantCount];
            for (int i = 0; i < _buildList.Count; i++)
            {
                GpuInstanceData data = _buildList[i];
                int v = data.Variant;
                if (v >= 0 && v < variantCount && lodCounts[v] > 0)
                {
                    perVariant[v]++;
                }
                else
                {
                    data.Variant = variantCount;   // index de la variante désactivée
                    _buildList[i] = data;
                }
            }

            int bucketCount = 0;
            for (int v = 0; v < variantCount; v++)
                bucketCount += lodCounts[v];
            if (bucketCount > MaxBuckets)
            {
                Debug.LogError($"{name} : {bucketCount} draws (variantes × LODs) > {MaxBuckets}. Réduisez les variantes ou les LODs.", this);
                return;
            }

            // 2. Un "bucket" = un draw = (variante, LOD). Chaque bucket a sa zone dans le buffer des visibles,
            //    dimensionnée pour le pire cas (toutes les instances de la variante dans ce LOD).
            _bucketMeshes = new Mesh[bucketCount];
            _bucketMaterials = new Material[bucketCount];
            _bucketSubMeshes = new int[bucketCount];
            _bucketLabels = new string[bucketCount];
            _bucketOffsets = new uint[Mathf.Max(bucketCount, 1)];
            _argsTemplate = new uint[Mathf.Max(bucketCount, 1) * ArgsPerDraw * 2];   // bloc caméra + bloc ombre
            var variantInfos = new GpuVariantInfo[variantCount + 1];
            int bucket = 0;
            uint visibleSlots = 0;

            for (int v = 0; v < variantCount; v++)
            {
                if (lodCounts[v] == 0)
                {
                    variantInfos[v] = GpuVariantInfo.Disabled;
                    continue;
                }

                InstanceVariant variant = variants[v];
                int lods = lodCounts[v];
                Bounds local = LocalBounds(variant, lods);
                float padding = GetBoundsPadding(variant, local);
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
                    _bucketOffsets[bucket] = visibleSlots;
                    visibleSlots += (uint)perVariant[v];

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

            // 3. Buffers communs. Les buffers par caméra sont créés au premier rendu de chaque caméra.
            int instanceCount = _buildList.Count;
            instances = _buildList.ToArray();
            _instanceBuffer = new ComputeBuffer(Mathf.Max(instanceCount, 1), GpuInstanceData.Stride, ComputeBufferType.Default);
            _variantBuffer = new ComputeBuffer(variantInfos.Length, GpuVariantInfo.Stride, ComputeBufferType.Default);
            _bucketOffsetBuffer = new ComputeBuffer(_bucketOffsets.Length, sizeof(uint), ComputeBufferType.Default);
            if (instanceCount > 0)
                _instanceBuffer.SetData(instances, 0, 0, instanceCount);
            _variantBuffer.SetData(variantInfos);
            _bucketOffsetBuffer.SetData(_bucketOffsets);

            _visibleSlots = (int)visibleSlots;
            _bucketCount = bucketCount;
            InstanceCount = instanceCount;
            _built = true;
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

        void ReleaseAll()
        {
            _instanceBuffer?.Release();
            _variantBuffer?.Release();
            _bucketOffsetBuffer?.Release();
            _instanceBuffer = _variantBuffer = _bucketOffsetBuffer = null;
            foreach (CameraResources resources in _cameraResources.Values)
                resources.Release();
            _cameraResources.Clear();
            _bucketCount = 0;
            InstanceCount = 0;
            _built = false;
            _dirty = true;
        }

        // ------------------------------------------------------------------------------------------
        // Diagnostic
        // ------------------------------------------------------------------------------------------

        void LogVisibleCounts(Camera camera, CameraResources resources, uint multiplier)
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

            AsyncGPUReadback.Request(resources.args, request =>
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
            public static readonly int BucketOffset = Shader.PropertyToID("_NyxBucketOffset");
        }
    }
}
