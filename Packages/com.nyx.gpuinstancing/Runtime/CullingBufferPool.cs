using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Buffers du culling d'un spawner. Un jeu (données de rendu des visibles compactées, index par draw, compteurs) est
    /// partagé par les caméras rendues l'une après l'autre (vue Game puis vue Scene...) : la caméra suivante ne lance son
    /// culling qu'après les draws de la précédente. Une caméra rendue PENDANT le rendu d'une autre (réflexion d'eau via
    /// Camera.Render) prend un autre jeu : les draws de la première gardent leurs données. Chaque caméra a ses arguments
    /// indirects (bloc caméra puis bloc ombre).
    /// Mémoire par jeu : visibles × 64 o + pire cas par draw × 4 o.
    /// </summary>
    internal sealed class CullingBufferPool
    {
        /// <summary>Jeu de buffers de culling et les blocs de propriétés des draws qui le lisent.</summary>
        public sealed class BufferSet
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

        /// <summary>État propre à une caméra : ses arguments indirects (lus aussi par TryReadVisibleCounts) et le jeu qu'elle tient.</summary>
        public sealed class CameraState
        {
            public ComputeBuffer args;           // bloc caméra puis bloc ombre
            public BufferSet inUse;              // pris entre le début et la fin du rendu de la caméra
            public uint lastMultiplier = 1;      // diagnostic : multiplicateur VR du dernier rendu
            public bool skippedLastRender;       // diagnostic : spawner rejeté en entier côté CPU au dernier rendu
        }

        readonly Dictionary<Camera, CameraState> _cameras = new Dictionary<Camera, CameraState>();
        readonly List<BufferSet> _allSets = new List<BufferSet>();
        readonly Stack<BufferSet> _freeSets = new Stack<BufferSet>();
        readonly List<Camera> _deadCameras = new List<Camera>();

        /// <summary>Jeux alloués (1 tant qu'aucune caméra n'est rendue pendant le rendu d'une autre).</summary>
        public int SetCount => _allSets.Count;

        /// <summary>Jeux de buffers et arguments de chaque caméra (octets).</summary>
        public long GpuBytes
        {
            get
            {
                long bytes = 0;
                foreach (BufferSet set in _allSets)
                    bytes += set.Bytes;
                foreach (CameraState state in _cameras.Values)
                    bytes += SpawnerGpu.Size(state.args);
                return bytes;
            }
        }

        /// <summary>État de 'camera', créé au premier rendu avec les arguments initiaux des draws.</summary>
        public CameraState StateFor(Camera camera, uint[] argsTemplate)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                return state;
            state = new CameraState { args = new ComputeBuffer(argsTemplate.Length, sizeof(uint), ComputeBufferType.IndirectArguments) };
            state.args.SetData(argsTemplate);
            _cameras.Add(camera, state);
            return state;
        }

        /// <summary>Jeu tenu par la caméra pendant son rendu : un jeu libre, ou un nouveau si tous sont tenus.</summary>
        public BufferSet Take(CameraState state, DrawBuckets draws) => state.inUse ??= TakeFree(draws);

        /// <summary>Fin du rendu d'une caméra : son jeu de buffers redevient libre.</summary>
        public void EndCamera(Camera camera)
        {
            if (_cameras.TryGetValue(camera, out CameraState state))
                Return(state);
        }

        /// <summary>Hors rendu (LateUpdate) : ressources des caméras détruites, jeux de buffers restés pris.</summary>
        public void EndFrame()
        {
            foreach (KeyValuePair<Camera, CameraState> pair in _cameras)
            {
                if (pair.Key == null)
                    _deadCameras.Add(pair.Key);
                else
                    Return(pair.Value);   // aucun rendu en cours ici : un jeu resté pris (fin de rendu manquée) est libre
            }
            foreach (Camera camera in _deadCameras)
            {
                CameraState state = _cameras[camera];
                Return(state);
                state.args?.Release();
                _cameras.Remove(camera);
            }
            _deadCameras.Clear();
        }

        /// <summary>
        /// DIAGNOSTIC / TESTS : instances visibles par draw au dernier rendu de 'camera'. Lecture GPU bloquante.
        /// false si cette caméra n'a encore jamais dessiné ce spawner.
        /// </summary>
        public bool TryReadVisibleCounts(Camera camera, int drawCount, List<int> countsPerDraw)
        {
            countsPerDraw.Clear();
            if (camera == null || !_cameras.TryGetValue(camera, out CameraState state) || state.args == null)
                return false;

            var args = new uint[state.args.count];
            if (!state.skippedLastRender)
                state.args.GetData(args);
            uint multiplier = state.lastMultiplier == 0 ? 1u : state.lastMultiplier;
            for (int b = 0; b < drawCount; b++)
                countsPerDraw.Add((int)(args[b * DrawBuckets.ArgsPerDraw + 1] / multiplier));
            return true;
        }

        /// <summary>Tous les jeux et les arguments des caméras (reconstruction, spawner désactivé).</summary>
        public void Release()
        {
            foreach (BufferSet set in _allSets)
                set.Release();
            _allSets.Clear();
            _freeSets.Clear();
            foreach (CameraState state in _cameras.Values)
                state.args?.Release();
            _cameras.Clear();
        }

        BufferSet TakeFree(DrawBuckets draws)
        {
            if (_freeSets.Count > 0)
                return _freeSets.Pop();

            // Création seulement (jamais de libération ici : on peut être au milieu du rendu d'une autre caméra)
            var set = new BufferSet
            {
                visible = new ComputeBuffer(Mathf.Max(draws.VisibleCapacity, 1), GpuInstanceData.Stride, ComputeBufferType.Default),
                visibleIndex = new ComputeBuffer(Mathf.Max(draws.IndexCapacity, 1), sizeof(uint), ComputeBufferType.Default),
                counters = new ComputeBuffer(draws.Count + 1, sizeof(uint), ComputeBufferType.Default),
                blocks = new MaterialPropertyBlock[draws.Count]
            };
            set.counters.SetData(new uint[draws.Count + 1]);
            for (int b = 0; b < draws.Count; b++)
            {
                var block = new MaterialPropertyBlock();
                block.SetBuffer(ShaderIds.RenderData, set.visible);
                block.SetBuffer(ShaderIds.RenderIndex, set.visibleIndex);
                block.SetFloat(ShaderIds.BucketOffset, draws.Offsets[b]);
                set.blocks[b] = block;
            }
            _allSets.Add(set);
            return set;
        }

        void Return(CameraState state)
        {
            if (state.inUse == null)
                return;
            _freeSets.Push(state.inUse);
            state.inUse = null;
        }
    }
}
