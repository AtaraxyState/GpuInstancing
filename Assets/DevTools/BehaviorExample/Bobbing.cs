using Nyx.GpuInstancing;
using UnityEngine;

/// <summary>Exemple du README du package (« Écrire un comportement ») : instances qui montent et descendent.</summary>
public sealed class Bobbing : MeshSpawnerBehavior
{
    [SerializeField] float amplitude = 0.3f;
    [SerializeField] float speed = 1f;

    protected override string CullingShaderResource => "Bobbing";   // chemin dans Resources, sans extension

    public override void SetShaderParameters(ComputeShader shader, Camera camera) =>
        shader.SetVector("_Bobbing", new Vector4(amplitude, speed, 0f, 0f));

    // Le culling du spawner entier doit couvrir le mouvement
    public override Bounds GetPlacementBounds(Bounds positions, float meshRadius)
    {
        positions.Expand(2f * (meshRadius + amplitude));
        return positions;
    }
}
