using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Nyx.GpuInstancing
{
    /// <summary>Réglages du rendu d'un spawner (section Rendu, Culling GPU, Caméras / VR du MeshSpawner), lus à chaque caméra.</summary>
    internal struct CullingSettings
    {
        public int renderLayer;
        public ShadowCastingMode castShadows;
        public bool receiveShadows;
        public bool excludeFromDepthTexture;
        public float maxDrawDistance;
        public float lodBias;
        public float minScreenSize;
        public float shadowCullingExtrusion;
        public Light shadowLight;
        public StereoInstancingMode stereoInstancing;
        public bool renderInSceneView;
        public bool logVisibleCounts;
    }

    /// <summary>
    /// Rendu d'un spawner, caméra par caméra : culling GPU (frustum des deux yeux, distance, LOD, taille à l'écran,
    /// destruction hors du volume de la zone) puis un DrawMeshInstancedIndirect par draw (<see cref="DrawBuckets"/>).
    ///
    /// Mémoire GPU : un jeu de buffers de culling partagé par les caméras rendues l'une après l'autre (visibles × 64 o + index
    /// par draw × 4 o), et les arguments indirects de chaque caméra. Une caméra rendue PENDANT le rendu d'une autre
    /// (réflexion d'eau) prend un second jeu : les draws de la première gardent leurs données.
    /// </summary>
    internal sealed class InstanceRenderer
    {
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

            public long Bytes => SpawnerGpu.Size(visible) + SpawnerGpu.Size(visibleIndex) + SpawnerGpu.Size(counters);

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

        readonly Dictionary<Camera, CameraState> _cameras = new Dictionary<Camera, CameraState>();
        readonly List<CullingBuffers> _allBuffers = new List<CullingBuffers>();
        readonly Stack<CullingBuffers> _freeBuffers = new Stack<CullingBuffers>();
        readonly List<Camera> _deadCameras = new List<Camera>();
        readonly Vector4[] _planes = new Vector4[12];
        ComputeShader _shader;
        int _kernelCull = -1;
        int _kernelWriteArgs = -1;
        float _nextLogTime;

        // Volume de la zone figé à la reconstruction, pour la destruction sur GPU (posé à chaque dispatch de culling)
        ComputeBuffer _zoneVertexBuffer;   // sommets monde (x, z) ; 1 élément vide sans destruction par le volume
        int _zonePointCount;               // 0 = pas de destruction par le volume
        Vector4 _zoneBounds;               // min x, min z, max x, max z
        Vector4 _zoneHeight;               // bas, haut (monde, tolérance comprise)
        Vector4 _zoneNatural;              // bord naturel : amplitude, 1 / taille, (lisière : 0 pour la destruction)
        Vector4 _zoneOrigin;               // centre (x, z) et graine du bruit

        /// <summary>Draws du spawner, refaits à chaque reconstruction.</summary>
        public DrawBuckets Draws { get; } = new DrawBuckets();

        /// <summary>Jeux de buffers de culling alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int BufferSets => _allBuffers.Count;

        /// <summary>Mémoire GPU : table des variantes et zones d'index, jeux de culling, arguments de chaque caméra (octets).</summary>
        public long GpuBytes
        {
            get
            {
                long bytes = Draws.GpuBytes;
                foreach (CullingBuffers buffers in _allBuffers)
                    bytes += buffers.Bytes;
                foreach (CameraState state in _cameras.Values)
                    bytes += SpawnerGpu.Size(state.args);
                return bytes;
            }
        }

        /// <summary>Compute shader de culling du comportement (noyaux NyxCull et NyxWriteArgs). false s'il manque.</summary>
        public bool UseShader(ComputeShader shader)
        {
            _shader = shader;
            if (shader == null)
                return false;
            _kernelCull = shader.FindKernel("NyxCull");
            _kernelWriteArgs = shader.FindKernel("NyxWriteArgs");
            return true;
        }

        /// <summary>Volume pour la destruction sur GPU, figé jusqu'à la prochaine reconstruction : rien si 'clip' est faux.</summary>
        public void SetZone(in ZoneVolume zone, bool clip)
        {
            _zonePointCount = 0;
            _zoneVertexBuffer?.Release();   // détruit par Unity après les dispatchs déjà soumis qui le lisent
            _zoneVertexBuffer = null;
            if (!clip)
            {
                _zoneVertexBuffer = new ComputeBuffer(1, 2 * sizeof(float));   // le noyau de culling le déclare : toujours lié
                return;
            }
            NativeArray<float2> polygon = zone.WorldPolygon(Allocator.Temp, out float4 b);
            _zoneVertexBuffer = new ComputeBuffer(polygon.Length, 2 * sizeof(float));
            _zoneVertexBuffer.SetData(polygon);
            _zonePointCount = polygon.Length;
            polygon.Dispose();
            float noise = zone.EdgeNoise;
            _zoneBounds = new Vector4(b.x - noise, b.y - noise, b.z + noise, b.w + noise);
            _zoneNatural = zone.NaturalParameters(false);   // destruction : bord seulement, sans lisière
            _zoneOrigin = zone.NoiseOrigin;
            _zoneHeight = zone.HeightRange;
        }

        /// <summary>Caméras concernées : celles qui voient la couche ; vue Scene selon le réglage ; ni aperçus ni sondes.</summary>
        public static bool ShouldRender(Camera camera, in CullingSettings settings)
        {
            if ((camera.cullingMask & (1 << settings.renderLayer)) == 0)
                return false;

            switch (camera.cameraType)
            {
                case CameraType.Game:
                case CameraType.VR:
                    return true;            // inclut les caméras de réflexion d'eau (rendues comme caméras Game)
                case CameraType.SceneView:
                    return settings.renderInSceneView;
                default:
                    return false;           // aperçus, sondes de réflexion
            }
        }

        /// <summary>
        /// Culling puis draws de 'instanceCount' instances pour 'camera'. Rien si les bounds du spawner sont hors du frustum
        /// ou trop loin. 'owner' : contexte des logs de diagnostic.
        /// </summary>
        public void Render(MeshSpawner owner, Camera camera, in CullingSettings settings, MeshSpawnerBehavior behavior,
                           ComputeBuffer instances, int instanceCount, Bounds worldBounds)
        {
            ComputeShader cs = _shader;
            uint multiplier = InstanceMultiplier(camera, settings.stereoInstancing);
            bool stereo = SetCameraParameters(cs, camera, settings, multiplier, out Vector3 shadowSweep);
            CameraState state = GetCameraState(camera);

            // Spawner entièrement hors du frustum ou trop loin : ni dispatch, ni draw.
            Bounds cullBounds = worldBounds;
            cullBounds.center += shadowSweep;
            cullBounds.extents += Abs(shadowSweep);
            if (!BoundsVisible(cullBounds, stereo, camera.transform.position, settings.maxDrawDistance))
            {
                state.skippedLastRender = true;
                return;
            }

            CullingBuffers buffers = state.inUse ??= TakeBuffers();
            state.lastMultiplier = multiplier;
            state.skippedLastRender = false;

            DrawBuckets draws = Draws;
            cs.SetFloat(ShaderIds.Time, MeshSpawner.CurrentTime);
            cs.SetInt(ShaderIds.InstanceCount, instanceCount);
            cs.SetInt(ShaderIds.BucketCount, draws.Count);
            cs.SetInt(ShaderIds.DeadVariant, draws.DeadVariant);
            cs.SetInt(ShaderIds.ZonePointCount, _zonePointCount);
            cs.SetBuffer(_kernelCull, ShaderIds.ZoneVertices, _zoneVertexBuffer);
            if (_zonePointCount > 0)
            {
                cs.SetVector(ShaderIds.ZoneBounds, _zoneBounds);
                cs.SetVector(ShaderIds.ZoneHeight, _zoneHeight);
                cs.SetVector(ShaderIds.ZoneNatural, _zoneNatural);
                cs.SetVector(ShaderIds.ZoneOrigin, _zoneOrigin);
            }
            behavior.SetShaderParameters(cs, camera);

            cs.SetBuffer(_kernelCull, ShaderIds.Instances, instances);
            cs.SetBuffer(_kernelCull, ShaderIds.Variants, draws.VariantBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.BucketOffsets, draws.OffsetBuffer);
            cs.SetBuffer(_kernelCull, ShaderIds.Visible, buffers.visible);
            cs.SetBuffer(_kernelCull, ShaderIds.VisibleIndex, buffers.visibleIndex);
            cs.SetBuffer(_kernelCull, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Counters, buffers.counters);
            cs.SetBuffer(_kernelWriteArgs, ShaderIds.Args, state.args);

            SpawnerGpu.DispatchInstances(cs, _kernelCull, instanceCount);
            cs.Dispatch(_kernelWriteArgs, 1, 1, 1);   // draws.Count <= 64 : un seul groupe

            // En Single Pass Instanced, le nombre d'instances est doublé pour les passes caméra, mais la carte
            // d'ombre n'est pas stéréo : elle utilise son propre bloc d'arguments (non doublé) via un draw ShadowsOnly.
            bool shadows = settings.castShadows != ShadowCastingMode.Off;
            bool splitShadows = shadows && multiplier > 1;
            ShadowCastingMode mainShadows = splitShadows ? ShadowCastingMode.Off : settings.castShadows;
            bool receive = settings.receiveShadows && !settings.excludeFromDepthTexture;
            draws.SyncMaterials();
            for (int b = 0; b < draws.Count; b++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    draws.Meshes[b], draws.SubMeshes[b], draws.Materials[b], worldBounds,
                    state.args, b * DrawBuckets.ArgsPerDraw * sizeof(uint), buffers.blocks[b],
                    mainShadows, receive, settings.renderLayer, camera, LightProbeUsage.Off);

                if (splitShadows)
                {
                    Graphics.DrawMeshInstancedIndirect(
                        draws.Meshes[b], draws.SubMeshes[b], draws.Materials[b], worldBounds,
                        state.args, (draws.Count + b) * DrawBuckets.ArgsPerDraw * sizeof(uint), buffers.blocks[b],
                        ShadowCastingMode.ShadowsOnly, false, settings.renderLayer, camera, LightProbeUsage.Off);
                }
            }

            if (settings.logVisibleCounts)
                LogVisibleCounts(owner, camera, state, multiplier, instanceCount);
        }

        /// <summary>Fin du rendu d'une caméra : son jeu de buffers redevient libre.</summary>
        public void EndCamera(Camera camera)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                ReturnBuffers(state);
        }

        /// <summary>Hors rendu (LateUpdate) : ressources des caméras détruites, jeux de buffers restés pris.</summary>
        public void EndFrame()
        {
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

        /// <summary>
        /// DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera'. Lecture GPU bloquante.
        /// false si cette caméra n'a encore jamais dessiné ce spawner.
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
            for (int b = 0; b < Draws.Count; b++)
                countsPerDraw.Add((int)(args[b * DrawBuckets.ArgsPerDraw + 1] / multiplier));
            return true;
        }

        /// <summary>Draws, jeux de buffers et caméras (à chaque reconstruction). Le volume de la zone est gardé.</summary>
        public void ReleaseBuffers()
        {
            Draws.Release();
            foreach (CullingBuffers buffers in _allBuffers)
                buffers.Release();
            _allBuffers.Clear();
            _freeBuffers.Clear();
            foreach (CameraState state in _cameras.Values)
                state.args?.Release();
            _cameras.Clear();
        }

        /// <summary>Toutes les ressources GPU, volume de la zone compris (spawner désactivé).</summary>
        public void Release()
        {
            ReleaseBuffers();
            _zoneVertexBuffer?.Release();
            _zoneVertexBuffer = null;
        }

        CameraState GetCameraState(Camera camera)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                return state;
            uint[] template = Draws.ArgsTemplate;
            state = new CameraState { args = new ComputeBuffer(template.Length, sizeof(uint), ComputeBufferType.IndirectArguments) };
            state.args.SetData(template);
            _cameras.Add(camera, state);
            return state;
        }

        /// <summary>Jeu de buffers libre, ou un nouveau si toutes les caméras en cours de rendu en tiennent un.</summary>
        CullingBuffers TakeBuffers()
        {
            if (_freeBuffers.Count > 0)
                return _freeBuffers.Pop();

            // Création seulement (jamais de libération ici : on peut être au milieu du rendu d'une autre caméra)
            DrawBuckets draws = Draws;
            var buffers = new CullingBuffers
            {
                visible = new ComputeBuffer(Mathf.Max(draws.VisibleCapacity, 1), GpuInstanceData.Stride, ComputeBufferType.Default),
                visibleIndex = new ComputeBuffer(Mathf.Max(draws.IndexCapacity, 1), sizeof(uint), ComputeBufferType.Default),
                counters = new ComputeBuffer(draws.Count + 1, sizeof(uint), ComputeBufferType.Default),
                blocks = new MaterialPropertyBlock[draws.Count]
            };
            buffers.counters.SetData(new uint[draws.Count + 1]);
            for (int b = 0; b < draws.Count; b++)
            {
                var block = new MaterialPropertyBlock();
                block.SetBuffer(ShaderIds.RenderData, buffers.visible);
                block.SetBuffer(ShaderIds.RenderIndex, buffers.visibleIndex);
                block.SetFloat(ShaderIds.BucketOffset, draws.Offsets[b]);
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
        bool SetCameraParameters(ComputeShader cs, Camera camera, in CullingSettings settings, uint multiplier, out Vector3 shadowSweep)
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

            float maxDistance = Mathf.Min(settings.maxDrawDistance, camera.farClipPlane);
            float sizeRatio = camera.orthographic ? 0f : settings.minScreenSize / Mathf.Max(projectionScale, 1e-4f);

            shadowSweep = Vector3.zero;
            Light light = settings.shadowLight != null ? settings.shadowLight : RenderSettings.sun;
            if (settings.castShadows != ShadowCastingMode.Off && settings.shadowCullingExtrusion > 0f && light != null)
                shadowSweep = light.transform.forward * (settings.shadowCullingExtrusion * 0.5f);

            Vector3 cameraPosition = camera.transform.position;
            cs.SetVectorArray(ShaderIds.FrustumPlanes, _planes);
            cs.SetInt(ShaderIds.PlaneSetCount, stereo ? 2 : 1);
            cs.SetVector(ShaderIds.CameraPosition, new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, maxDistance * maxDistance));
            cs.SetVector(ShaderIds.CullParams, new Vector4(1f / (settings.lodBias * settings.lodBias), sizeRatio * sizeRatio, 0f, 0f));
            cs.SetVector(ShaderIds.ShadowSweep, shadowSweep);
            cs.SetInt(ShaderIds.InstanceMultiplier, (int)multiplier);
            return stereo;
        }

        /// <summary>
        /// En Single Pass Instanced, chaque instance est dessinée 2 fois (une par œil) : le nombre d'instances
        /// des arguments indirects doit être doublé à la main (SetInstanceMultiplier ne s'applique pas aux draws indirects).
        /// En multiview (Android : PICO, Quest), l'œil vient de gl_ViewID : pas de doublement.
        /// </summary>
        static uint InstanceMultiplier(Camera camera, StereoInstancingMode mode)
        {
            if (!camera.stereoEnabled)
                return 1;

            switch (mode)
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

        bool BoundsVisible(Bounds bounds, bool stereo, Vector3 cameraPosition, float maxDrawDistance)
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

        void LogVisibleCounts(MeshSpawner owner, Camera camera, CameraState state, uint multiplier, int instanceCount)
        {
            if (!SystemInfo.supportsAsyncGPUReadback || camera.cameraType == CameraType.SceneView)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextLogTime)
                return;
            _nextLogTime = now + 1f;

            int bucketCount = Draws.Count;
            string[] labels = Draws.Labels;
            long memory = owner.GpuMemoryBytes;
            string cameraName = camera.name;

            AsyncGPUReadback.Request(state.args, request =>
            {
                if (request.hasError || owner == null)
                    return;
                var data = request.GetData<uint>();
                var text = new StringBuilder(160);
                text.Append(owner.name).Append(" [").Append(cameraName).Append("] : ").Append(instanceCount).Append(" instances, ")
                    .Append(bucketCount).Append(" draw(s), ").Append(memory / 1024).Append(" Ko GPU | visibles :");
                uint total = 0;
                for (int b = 0; b < bucketCount && b * DrawBuckets.ArgsPerDraw + 1 < data.Length; b++)
                {
                    uint visible = data[b * DrawBuckets.ArgsPerDraw + 1] / multiplier;
                    total += visible;
                    text.Append(' ').Append(labels[b]).Append('=').Append(visible);
                }
                text.Append(" | total ").Append(total);
                Debug.Log(text.ToString(), owner);
            });
        }
    }
}
