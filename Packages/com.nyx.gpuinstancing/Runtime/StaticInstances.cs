using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Comportement : instances immobiles (rochers, épaves, végétation fixe), posées par le placement du MeshSpawner.
    /// C'est le modèle le plus simple à copier pour écrire un comportement : il ne fait que nommer son compute shader
    /// (Shaders/Resources/NyxGpuInstancing/StaticInstances.compute), qui recopie la matrice de chaque instance.
    /// Matériau conseillé : Nyx/GPU Instancing/Lit.
    /// </summary>
    [AddComponentMenu("Nyx/GPU Instancing/Behaviors/Static Instances")]
    public class StaticInstances : MeshSpawnerBehavior
    {
        protected override string CullingShaderResource => "NyxGpuInstancing/StaticInstances";

#if UNITY_EDITOR
        public override List<InstanceVariant> CreateDefaultVariants() => NyxDefaultAssets.RockVariants();
#endif
    }
}
