using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Nyx.GpuInstancing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Outil de développement : où part le temps GPU dans la scène OUVERTE (la démo), vue de sa caméra principale en 1280 x 720
/// (comme la build). Image complète, puis variantes : moins de pixels, ombres coupées, sargasses sans réception d'ombres,
/// culling des sargasses seul (rien dessiné), sans sargasses. Réglages changés par affectation directe puis restaurés : la
/// scène n'est pas marquée modifiée. Ordre aller puis retour (dérive des horloges). Résultat : Logs/NyxGpuInstancingTest/gpu_breakdown.txt.
/// Lancement : invoke.request « DemoGpuBreakdown, Assembly-CSharp-Editor|Run ».
/// </summary>
static class DemoGpuBreakdown
{
    const int Frames = 40;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        var lines = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {SystemInfo.graphicsDeviceName}, 1280 x 720, médiane de {Frames} images, aller puis retour" };
        try
        {
            Measure(lines);
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "gpu_breakdown.txt"), lines);
    }

    static void Measure(List<string> lines)
    {
        Camera camera = Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c.CompareTag("MainCamera"));
        MeshSpawner sargassum = Object.FindObjectsOfType<MeshSpawner>().FirstOrDefault(s => s.GetComponent<FloatOnWaves>() != null);
        if (camera == null || sargassum == null)
        {
            lines.Add($"Caméra principale ou sargasses introuvables dans la scène ouverte ({camera}, {sargassum}).");
            return;
        }

        var full = new RenderTexture(1280, 720, 24) { name = "DemoGpuBreakdown" };
        var small = new RenderTexture(320, 180, 24) { name = "DemoGpuBreakdown petit" };
        RenderTexture previousTarget = camera.targetTexture;
        bool previousEnabled = camera.enabled;
        ShadowQuality previousShadows = QualitySettings.shadows;
        FieldInfo minScreenSize = typeof(MeshSpawner).GetField("minScreenSize", Private);
        FieldInfo receiveShadows = typeof(MeshSpawner).GetField("receiveShadows", Private);
        float previousMinScreenSize = (float)minScreenSize.GetValue(sargassum);
        bool previousReceive = (bool)receiveShadows.GetValue(sargassum);
        camera.targetTexture = full;
        try
        {
            for (int i = 0; i < 5; i++)
                RenderAndWait(camera);
            var counts = new List<int>();
            sargassum.TryReadVisibleCounts(camera, counts);
            lines.Add($"Sargasses : {sargassum.InstanceCount:N0} instances, {counts.Sum():N0} visibles ({string.Join(", ", sargassum.DrawLabels.Zip(counts, (l, c) => $"{l} = {c:N0}"))})");
            long triangles = 0;
            int bucket = 0;
            foreach (InstanceVariant variant in sargassum.Variants)
            {
                for (int lod = 0; lod < variant.lodMeshes.Length && variant.lodMeshes[lod] != null; lod++, bucket++)
                {
                    long perMesh = variant.lodMeshes[lod].GetIndexCount(Mathf.Clamp(variant.subMeshIndex, 0, variant.lodMeshes[lod].subMeshCount - 1)) / 3;
                    lines.Add($"  {variant.lodMeshes[lod].name} : {perMesh} triangles, {variant.lodMeshes[lod].vertexCount} sommets");
                    if (bucket < counts.Count)
                        triangles += perMesh * counts[bucket];
                }
            }
            lines.Add($"  triangles des sargasses dessinés par image (passe principale) : {triangles / 1e6:F1} M ; ombres : {QualitySettings.shadows}, " +
                      $"profondeur de la caméra : {camera.depthTextureMode}");
            SavePng(camera, "gpu_breakdown_vue.png");

            var cases = new (string label, Action apply, Action restore)[]
            {
                ("image complète", () => { }, () => { }),
                ("320 x 180 (part des pixels = écart)", () => camera.targetTexture = small, () => camera.targetTexture = full),
                ("ombres coupées (qualité)", () => QualitySettings.shadows = ShadowQuality.Disable, () => QualitySettings.shadows = previousShadows),
                ("sargasses sans réception d'ombres", () => receiveShadows.SetValue(sargassum, false), () => receiveShadows.SetValue(sargassum, previousReceive)),
                ("sargasses : culling seul, rien dessiné", () => minScreenSize.SetValue(sargassum, 1000f), () => minScreenSize.SetValue(sargassum, previousMinScreenSize)),
                ("sans sargasses", () => sargassum.enabled = false, () => sargassum.enabled = true),
                ("diagnostic : rien au-delà de 60 m", () => SetCullDistance(sargassum, 60f), () => RestoreCullDistance(sargassum)),
                ("diagnostic : rien au-delà de 40 m", () => SetCullDistance(sargassum, 40f), () => RestoreCullDistance(sargassum)),
                ("diagnostic : shader expérimental (graine interpolée)", () => SwapShader(sargassum, "_EXP_SEED_INTERP"), () => RestoreMaterials(sargassum)),
                ("diagnostic : shader expérimental sans normal map", () => SwapShader(sargassum, "_EXP_SEED_INTERP", "_EXP_NO_NORMALMAP"), () => RestoreMaterials(sargassum)),
            };
            var results = new double[cases.Length];
            foreach (int pass in new[] { 0, 1 })
            {
                for (int k = 0; k < cases.Length; k++)
                {
                    int i = pass == 0 ? k : cases.Length - 1 - k;
                    cases[i].apply();
                    try
                    {
                        for (int w = 0; w < 4; w++)
                            RenderAndWait(camera);
                        results[i] += Median(() => RenderAndWait(camera)) / 2.0;
                    }
                    finally
                    {
                        cases[i].restore();
                    }
                }
            }
            lines.Add("");
            for (int i = 0; i < cases.Length; i++)
                lines.Add($"  {cases[i].label,-42} : {results[i],7:F2} ms  (écart avec l'image complète {results[i] - results[0],7:F2} ms)");
        }
        finally
        {
            QualitySettings.shadows = previousShadows;
            minScreenSize.SetValue(sargassum, previousMinScreenSize);
            receiveShadows.SetValue(sargassum, previousReceive);
            sargassum.enabled = true;
            foreach (KeyValuePair<InstanceVariant, (float cull, Material material)> pair in s_saved)
            {
                pair.Key.cullDistance = pair.Value.cull;
                pair.Key.material = pair.Value.material;
            }
            s_saved.Clear();
            foreach (Material copy in s_copies)
                Object.DestroyImmediate(copy);
            s_copies.Clear();
            sargassum.SetDirty();
            camera.targetTexture = previousTarget;
            camera.enabled = previousEnabled;
            Object.DestroyImmediate(full);
            Object.DestroyImmediate(small);
        }
    }

    // Diagnostics : variantes modifiées en place (pas via SerializedObject : la scène n'est pas marquée modifiée), puis restaurées.
    static readonly Dictionary<InstanceVariant, (float cull, Material material)> s_saved = new Dictionary<InstanceVariant, (float, Material)>();
    static readonly List<Material> s_copies = new List<Material>();

    static void Save(MeshSpawner spawner)
    {
        foreach (InstanceVariant variant in spawner.Variants)
        {
            if (!s_saved.ContainsKey(variant))
                s_saved.Add(variant, (variant.cullDistance, variant.material));
        }
    }

    static void SetCullDistance(MeshSpawner spawner, float distance)
    {
        Save(spawner);
        foreach (InstanceVariant variant in spawner.Variants)
            variant.cullDistance = distance;
        spawner.RebuildNow();
    }

    static void RestoreCullDistance(MeshSpawner spawner)
    {
        foreach (InstanceVariant variant in spawner.Variants)
            variant.cullDistance = s_saved[variant].cull;
        spawner.RebuildNow();
    }

    static void SwapShader(MeshSpawner spawner, params string[] keywords)
    {
        Save(spawner);
        var copies = new Dictionary<Material, Material>();
        foreach (InstanceVariant variant in spawner.Variants)
        {
            if (!copies.TryGetValue(variant.material, out Material copy))
            {
                copy = new Material(variant.material) { shader = Shader.Find("Nyx/Experiments/Sargassum"), enableInstancing = true };
                foreach (string keyword in keywords)
                    copy.EnableKeyword(keyword);
                copies.Add(variant.material, copy);
                s_copies.Add(copy);
            }
            variant.material = copy;
        }
        spawner.RebuildNow();
    }

    static void RestoreMaterials(MeshSpawner spawner)
    {
        foreach (InstanceVariant variant in spawner.Variants)
            variant.material = s_saved[variant].material;
        spawner.RebuildNow();
        foreach (Material copy in s_copies)
            Object.DestroyImmediate(copy);
        s_copies.Clear();
    }

    static void SavePng(Camera camera, string file)
    {
        RenderTexture target = camera.targetTexture;
        camera.Render();
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        RenderTexture.active = previous;
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllBytes(Path.Combine(folder, file), texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
    }

    static void RenderAndWait(Camera camera)
    {
        camera.Render();
        AsyncGPUReadback.Request(camera.targetTexture, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
    }

    static double Median(Action action)
    {
        var times = new double[Frames];
        for (int i = 0; i < Frames; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[Frames / 2];
    }
}
