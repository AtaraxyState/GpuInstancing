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
    /// Assets par défaut (éditeur uniquement) : meshes et matériaux de chaque comportement fourni (touffes de sargasses,
    /// bulles, rochers), eau et fond marin de la démo. Livrés dans Defaults/ du package ; recréés s'ils manquent (dans
    /// Assets/NyxGpuInstancing/Defaults si le package est en lecture seule). Un comportement ajouté dans l'éditeur donne
    /// les siens au MeshSpawner, qui s'affiche sans aucun réglage.
    /// </summary>
    public static class NyxDefaultAssets
    {
        public const string Prefix = "NyxGpu_";
        public const string SargassumShaderName = "Nyx/GPU Instancing/Sargassum";
        public const string BubbleShaderName = "Nyx/GPU Instancing/Bubble";
        public const string WaterShaderName = "Nyx/GPU Instancing/Demo Water";
        public const string LitShaderName = "Nyx/GPU Instancing/Lit";

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

        public static Material RockMaterial => GetOrCreate(Prefix + "Rock", "mat", () =>
        {
            var material = new Material(Shader.Find(LitShaderName)) { enableInstancing = true };
            material.SetColor("_Color", new Color(0.42f, 0.40f, 0.36f));
            return material;
        });

        /// <summary>Fond marin de la démo (shader Standard, pas instancié).</summary>
        public static Material SeabedMaterial => GetOrCreate(Prefix + "DemoSeabed", "mat", () =>
        {
            var material = new Material(Shader.Find("Standard"));
            material.SetColor("_Color", new Color(0.55f, 0.50f, 0.38f));
            material.SetFloat("_Glossiness", 0.05f);
            return material;
        });

        /// <summary>Deux touffes (8 et 6 frondes), 2 LODs chacune, LOD1 au-delà de 20 m. Comportement : FloatOnWaves.</summary>
        public static List<InstanceVariant> SargassumVariants()
        {
            Material material = SargassumMaterial;
            return new List<InstanceVariant>
            {
                Variant(material, new[] { 20f }, Clump("ClumpA_LOD0", 8, 3, 0.9f, 0.18f), Clump("ClumpA_LOD1", 4, 1, 0.9f, 0.24f)),
                Variant(material, new[] { 20f }, Clump("ClumpB_LOD0", 6, 3, 0.7f, 0.2f), Clump("ClumpB_LOD1", 3, 1, 0.7f, 0.26f))
            };
        }

        /// <summary>Icosphères 162 / 42 / 12 sommets, LOD suivant à 3 et 10 m. Comportement : BubbleColumns.</summary>
        public static List<InstanceVariant> BubbleVariants() => new List<InstanceVariant>
        {
            Variant(BubbleMaterial, new[] { 3f, 10f },
                GetOrCreate(Prefix + "Bubble_LOD0", "asset", () => IcosphereBuilder.Build(2)),
                GetOrCreate(Prefix + "Bubble_LOD1", "asset", () => IcosphereBuilder.Build(1)),
                GetOrCreate(Prefix + "Bubble_LOD2", "asset", () => IcosphereBuilder.Build(0)))
        };

        /// <summary>Rocher (icosphère bosselée et aplatie), 2 LODs, LOD1 au-delà de 25 m. Comportement : StaticInstances.</summary>
        public static List<InstanceVariant> RockVariants() => new List<InstanceVariant>
        {
            Variant(RockMaterial, new[] { 25f },
                GetOrCreate(Prefix + "Rock_LOD0", "asset", () => BuildRock(2)),
                GetOrCreate(Prefix + "Rock_LOD1", "asset", () => BuildRock(1)))
        };

        /// <summary>Crée tous les assets manquants (au premier import du package, ou après une suppression).</summary>
        public static void EnsureAll()
        {
            SargassumVariants();
            BubbleVariants();
            RockVariants();
            _ = WaterMaterial;
            _ = SeabedMaterial;
        }

        static InstanceVariant Variant(Material material, float[] lodDistances, params Mesh[] lods) => new InstanceVariant
        {
            lodMeshes = lods,
            lodDistances = lodDistances,
            material = material
        };

        /// <summary>Icosphère déformée par un bruit fonction de la direction (pas de fissure aux sommets partagés), aplatie, posée sur y = 0.</summary>
        static Mesh BuildRock(int subdivisions)
        {
            Mesh mesh = IcosphereBuilder.Build(subdivisions);
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 d = vertices[i].normalized;
                float bumps = 1f + 0.22f * Mathf.Sin(d.x * 5.1f + 1.3f) * Mathf.Sin(d.y * 4.3f + 0.7f) * Mathf.Sin(d.z * 6.7f + 2.1f)
                                 + 0.08f * Mathf.Sin(d.x * 13f + d.z * 11f);
                Vector3 p = d * (0.5f * bumps);
                vertices[i] = new Vector3(p.x * 1.15f, p.y * 0.6f + 0.2f, p.z);
            }
            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

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
            asset.hideFlags = HideFlags.None;   // un mesh généré pour l'exécution (IcosphereBuilder) est marqué DontSave
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
