using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Rendu d'un spawner, caméra par caméra : paramètres de la caméra (<see cref="CameraCulling"/>), rejet CPU du spawner
    /// entier, culling GPU (noyaux NyxCull et NyxWriteArgs du compute shader du comportement, InstancingCulling.hlsl), puis un
    /// DrawMeshInstancedIndirect par draw (<see cref="DrawBuckets"/>). Buffers : <see cref="CullingBufferPool"/> ; destruction
    /// des instances qui sortent du volume : <see cref="ZoneGpu"/>.
    /// </summary>
    internal sealed class InstanceRenderer
    {
        readonly CameraCulling _camera = new CameraCulling();
        readonly CullingBufferPool _buffers = new CullingBufferPool();
        ComputeShader _shader;
        int _kernelCull = -1;
        int _kernelWriteArgs = -1;
        ZoneGpu _zone;                     // volume figé à la reconstruction ; désactivé sans destruction par le volume
        float _nextLogTime;

        /// <summary>Draws du spawner, refaits à chaque reconstruction.</summary>
        public DrawBuckets Draws { get; } = new DrawBuckets();

        /// <summary>Jeux de buffers de culling alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int BufferSets => _buffers.SetCount;

        /// <summary>Mémoire GPU : table des variantes et zones d'index, jeux de culling, arguments de chaque caméra (octets).</summary>
        public long GpuBytes => Draws.GpuBytes + _buffers.GpuBytes;

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

        /// <summary>Volume pour la destruction sur GPU, jusqu'à la prochaine reconstruction (ZoneGpu.Disabled : aucune).</summary>
        public void SetZone(ZoneGpu zone)
        {
            _zone?.Release();   // détruit par Unity après les dispatchs déjà soumis qui le lisent
            _zone = zone;
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
            DrawBuckets draws = Draws;
            _camera.Setup(camera, settings);
            CullingBufferPool.CameraState state = _buffers.StateFor(camera, draws.ArgsTemplate);

            // Spawner entièrement hors du frustum ou trop loin : ni dispatch, ni draw.
            if (!_camera.Sees(worldBounds, camera.transform.position, settings.maxDrawDistance))
            {
                state.skippedLastRender = true;
                return;
            }

            CullingBufferPool.BufferSet buffers = _buffers.Take(state, draws);
            uint multiplier = _camera.Multiplier;
            state.lastMultiplier = multiplier;
            state.skippedLastRender = false;

            ComputeShader cs = _shader;
            _camera.Apply(cs);
            cs.SetFloat(ShaderIds.Time, MeshSpawner.CurrentTime);
            cs.SetInt(ShaderIds.InstanceCount, instanceCount);
            cs.SetInt(ShaderIds.BucketCount, draws.Count);
            cs.SetInt(ShaderIds.DeadVariant, draws.DeadVariant);
            _zone.Bind(cs, _kernelCull);
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

            Draw(camera, settings, draws, state, buffers, worldBounds, multiplier);

            if (settings.logVisibleCounts)
                LogVisibleCounts(owner, camera, state, multiplier, instanceCount);
        }

        /// <summary>Fin du rendu d'une caméra : son jeu de buffers redevient libre.</summary>
        public void EndCamera(Camera camera) => _buffers.EndCamera(camera);

        /// <summary>Hors rendu (LateUpdate) : ressources des caméras détruites, jeux de buffers restés pris.</summary>
        public void EndFrame() => _buffers.EndFrame();

        /// <summary>DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera' (lecture GPU bloquante).</summary>
        public bool TryReadVisibleCounts(Camera camera, List<int> countsPerDraw) => _buffers.TryReadVisibleCounts(camera, Draws.Count, countsPerDraw);

        /// <summary>Draws, jeux de buffers et caméras (à chaque reconstruction). Le volume de la zone est gardé.</summary>
        public void ReleaseBuffers()
        {
            Draws.Release();
            _buffers.Release();
        }

        /// <summary>Toutes les ressources GPU, volume de la zone compris (spawner désactivé).</summary>
        public void Release()
        {
            ReleaseBuffers();
            _zone?.Release();
            _zone = null;
        }

        /// <summary>
        /// Un DrawMeshInstancedIndirect par draw. En Single Pass Instanced, le nombre d'instances est doublé pour les passes
        /// caméra, mais la carte d'ombre n'est pas stéréo : elle utilise son propre bloc d'arguments (non doublé) via un draw
        /// ShadowsOnly. Hors de la texture de profondeur, les instances ne reçoivent pas d'ombres.
        /// </summary>
        static void Draw(Camera camera, in CullingSettings settings, DrawBuckets draws, CullingBufferPool.CameraState state,
                         CullingBufferPool.BufferSet buffers, Bounds worldBounds, uint multiplier)
        {
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
        }

        /// <summary>DIAGNOSTIC : 1 fois par seconde, visibles par draw (lecture GPU asynchrone, sans blocage).</summary>
        void LogVisibleCounts(MeshSpawner owner, Camera camera, CullingBufferPool.CameraState state, uint multiplier, int instanceCount)
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
