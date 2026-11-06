using UnityEngine;
using UnityEngine.Rendering;

namespace Nyx.GpuInstancing
{
    /// <summary>Réglages du rendu d'un spawner (sections Rendu, Culling GPU, Caméras / VR du MeshSpawner), lus à chaque caméra.</summary>
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
}
