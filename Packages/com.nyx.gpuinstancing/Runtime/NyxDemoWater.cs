using UnityEngine;
using UnityEngine.Rendering;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Surface d'eau de démonstration : grille générée au chargement (aucun asset, rien de sérialisé), dessinée pour chaque
    /// caméra et déformée dans le shader « Nyx/GPU Instancing/Demo Water » par la même houle que FloatOnWaves.
    /// Objet posé au niveau de l'eau.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Nyx/GPU Instancing/Demo Water")]
    public sealed class NyxDemoWater : MonoBehaviour
    {
        [Tooltip("Matériau utilisant le shader Nyx/GPU Instancing/Demo Water.")]
        [SerializeField] Material material;
        [Tooltip("Côté de la surface (m).")]
        [SerializeField, Min(1f)] float size = 160f;
        [Tooltip("Cases par côté : plus fin = vagues plus lisses, plus de sommets.")]
        [SerializeField, Range(8, 250)] int resolution = 160;

        Mesh _mesh;

        public Material Material
        {
            get => material;
            set => material = value;
        }

        void OnEnable()
        {
            Build();
            Camera.onPreCull += Draw;
            RenderPipelineManager.beginCameraRendering += DrawSrp;
        }

        void OnDisable()
        {
            Camera.onPreCull -= Draw;
            RenderPipelineManager.beginCameraRendering -= DrawSrp;
        }

        void OnValidate()
        {
            if (isActiveAndEnabled)
                Build();
        }

        void OnDestroy()
        {
            if (_mesh == null)
                return;
            if (Application.isPlaying)
                Destroy(_mesh);
            else
                DestroyImmediate(_mesh);
        }

        void DrawSrp(ScriptableRenderContext context, Camera camera) => Draw(camera);

        void Draw(Camera camera)
        {
            if (material == null || _mesh == null || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return;
            Graphics.DrawMesh(_mesh, transform.localToWorldMatrix, material, gameObject.layer, camera, 0, null, ShadowCastingMode.Off, false);
        }

        void Build()
        {
            if (_mesh == null)
                _mesh = new Mesh { name = "NyxDemoWater", hideFlags = HideFlags.DontSave };
            int side = resolution + 1;
            var vertices = new Vector3[side * side];
            var normals = new Vector3[side * side];
            var uvs = new Vector2[side * side];
            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    float u = (float)x / resolution, v = (float)z / resolution;
                    int i = z * side + x;
                    vertices[i] = new Vector3((u - 0.5f) * size, 0f, (v - 0.5f) * size);
                    normals[i] = Vector3.up;
                    uvs[i] = new Vector2(u, v);
                }
            }
            var triangles = new int[resolution * resolution * 6];
            int t = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int a = z * side + x;   // sens horaire vu du dessus
                    triangles[t++] = a;
                    triangles[t++] = a + side;
                    triangles[t++] = a + 1;
                    triangles[t++] = a + 1;
                    triangles[t++] = a + side;
                    triangles[t++] = a + side + 1;
                }
            }
            _mesh.Clear();
            _mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.vertices = vertices;
            _mesh.normals = normals;
            _mesh.uv = uvs;
            _mesh.triangles = triangles;
            // Bounds agrandies : les vagues déplacent les sommets dans le shader
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(size + 8f, 8f, size + 8f));
        }
    }
}
