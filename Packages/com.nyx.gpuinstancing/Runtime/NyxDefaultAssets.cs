#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Assets par défaut (éditeur uniquement) : touffes de sargasses, texture de fronde, matériaux, eau de démo.
    /// Livrés dans Defaults/ du package ; recréés s'ils manquent (dans Assets/NyxGpuInstancing/Defaults si le package
    /// est en lecture seule). Un champ ajouté dans l'éditeur les reçoit et s'affiche sans aucun réglage.
    /// </summary>
    public static class NyxDefaultAssets
    {
        public const string Prefix = "NyxGpu_";
        public const string SargassumShaderName = "Nyx/GPU Instancing/Sargassum";
        public const string BubbleShaderName = "Nyx/GPU Instancing/Bubble";
        public const string WaterShaderName = "Nyx/GPU Instancing/Demo Water";

        /// <summary>Dossier des assets par défaut : Defaults/ du package s'il est modifiable, sinon dans Assets.</summary>
        public static string Folder
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NyxDefaultAssets).Assembly);
                if (package != null && (package.source == UnityEditor.PackageManager.PackageSource.Embedded ||
                                        package.source == UnityEditor.PackageManager.PackageSource.Local))
                    return package.assetPath + "/Defaults";
                return "Assets/NyxGpuInstancing/Defaults";
            }
        }

        public static Texture2D FrondTexture => GetOrCreate(Prefix + "FrondTexture", "asset", BuildFrondTexture);

        public static Material SargassumMaterial => GetOrCreate(Prefix + "Sargassum", "mat", () =>
        {
            Texture2D texture = FrondTexture;
            var material = new Material(Shader.Find(SargassumShaderName)) { enableInstancing = true };
            material.SetTexture("_MainTex", texture);
            return material;
        });

        public static Material BubbleMaterial => GetOrCreate(Prefix + "Bubble", "mat",
            () => new Material(Shader.Find(BubbleShaderName)) { enableInstancing = true });

        public static Material WaterMaterial => GetOrCreate(Prefix + "DemoWater", "mat",
            () => new Material(Shader.Find(WaterShaderName)));

        /// <summary>Deux touffes (8 et 6 frondes), 2 LODs chacune, LOD1 au-delà de 20 m.</summary>
        public static List<InstanceVariant> SargassumVariants()
        {
            Material material = SargassumMaterial;
            return new List<InstanceVariant>
            {
                Variant(material, Clump("ClumpA_LOD0", 8, 3, 0.9f, 0.18f), Clump("ClumpA_LOD1", 4, 1, 0.9f, 0.24f)),
                Variant(material, Clump("ClumpB_LOD0", 6, 3, 0.7f, 0.2f), Clump("ClumpB_LOD1", 3, 1, 0.7f, 0.26f))
            };
        }

        /// <summary>Crée tous les assets manquants (au premier import du package, ou après une suppression).</summary>
        public static void EnsureAll()
        {
            SargassumVariants();
            _ = BubbleMaterial;
            _ = WaterMaterial;
        }

        static InstanceVariant Variant(Material material, Mesh lod0, Mesh lod1) => new InstanceVariant
        {
            lodMeshes = new[] { lod0, lod1 },
            lodDistances = new[] { 20f },
            material = material
        };

        static Mesh Clump(string name, int fronds, int segments, float length, float width) =>
            GetOrCreate(Prefix + name, "asset", () => BuildClump(fronds, segments, length, width));

        static T GetOrCreate<T>(string name, string extension, Func<T> create) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets($"{name} t:{typeof(T).Name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name)
                {
                    var existing = AssetDatabase.LoadAssetAtPath<T>(path);
                    if (existing != null)
                        return existing;
                }
            }
            T asset = create();
            asset.name = name;
            string folder = Folder;
            EnsureFolder(folder);
            AssetDatabase.CreateAsset(asset, $"{folder}/{name}.{extension}");
            return asset;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Touffe : 'fronds' lanières qui rayonnent depuis le centre, légèrement relevées.</summary>
        static Mesh BuildClump(int fronds, int segments, float length, float width)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (int f = 0; f < fronds; f++)
            {
                float angle = f * Mathf.PI * 2f / fronds;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var side = new Vector3(-direction.z, 0f, direction.x);
                int start = vertices.Count;
                for (int s = 0; s <= segments; s++)
                {
                    float t = (float)s / segments;
                    Vector3 center = direction * (t * length) + Vector3.up * (0.08f * Mathf.Sin(t * Mathf.PI));
                    vertices.Add(center - side * (width * 0.5f));
                    vertices.Add(center + side * (width * 0.5f));
                    normals.Add(Vector3.up);
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2(0f, t));
                    uvs.Add(new Vector2(1f, t));
                }
                for (int s = 0; s < segments; s++)
                {
                    int a = start + s * 2;   // sens horaire vu du dessus : face avant du côté des normales (+Y)
                    triangles.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
                }
            }
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Texture de fronde : brun doré, bords découpés en alpha (alpha-test du shader).</summary>
        static Texture2D BuildFrondTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    float edge = 0.5f - Mathf.Abs(u - 0.5f);
                    float lobes = 0.35f + 0.15f * Mathf.Sin(v * 40f);
                    byte alpha = (byte)(edge * 2f > 1f - lobes ? 255 : 0);
                    byte shade = (byte)(200 + 40 * Mathf.Sin(v * 25f + u * 6f));
                    pixels[y * size + x] = new Color32(shade, (byte)(shade * 0.75f), (byte)(shade * 0.3f), alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }
    }
}
#endif
