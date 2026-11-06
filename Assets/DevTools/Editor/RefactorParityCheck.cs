using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nyx.GpuInstancing;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Outil de développement : vérifie qu'un refactor ne change pas ce que produit le MeshSpawner. Six configurations (chemins
/// GPU et C#, zone naturelle, liste, bulles, placement figé) : instances écrites sur GPU, placements, draws, mémoire, requêtes
/// de zone, visibles par draw, image de rochers.
///  1. Avant le refactor : SaveReference (Logs/NyxGpuInstancingTest/parity_reference/).
///  2. Après : Compare (parity/compare.txt). Tout doit être identique, sauf les instances, comparées à quelques ulp près :
///     le pilote peut calculer le sinus / cosinus du GPU un peu différemment d'une exécution à l'autre (~1e-6, mesuré sur
///     GTX 1060), placements identiques.
/// Scène additive temporaire, refermée sans enregistrer : les scènes ouvertes ne sont pas touchées (modifications non
/// enregistrées comprises). Leurs lumières éclairent aussi l'image des rochers : garder les mêmes scènes ouvertes entre
/// SaveReference et Compare (la 2e ligne du résumé les liste).
/// invoke.request « RefactorParityCheck, Assembly-CSharp-Editor|SaveReference » (ou Compare).
/// </summary>
static class RefactorParityCheck
{
    const string ReferenceFolder = "parity_reference";
    const string CurrentFolder = "parity";
    const string SummaryFile = "summary.txt";
    static readonly Vector3 Origin = new Vector3(10000f, 0f, 10000f);

    static string LogFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));

    public static void SaveReference() => Capture(ReferenceFolder);

    public static void Compare()
    {
        if (!Capture(CurrentFolder))
            return;
        string reference = Path.Combine(LogFolder, ReferenceFolder);
        string current = Path.Combine(LogFolder, CurrentFolder);
        var report = new List<string>();
        if (!File.Exists(Path.Combine(reference, SummaryFile)))
        {
            report.Add("ARRÊT : pas de référence (lancer SaveReference avant le refactor).");
            File.WriteAllLines(Path.Combine(current, "compare.txt"), report);
            return;
        }

        string[] before = File.ReadAllLines(Path.Combine(reference, SummaryFile));
        string[] after = File.ReadAllLines(Path.Combine(current, SummaryFile));
        int textDifferences = 0;
        for (int i = 0; i < Math.Max(before.Length, after.Length); i++)
        {
            string a = i < before.Length ? before[i] : "(absente)";
            string b = i < after.Length ? after[i] : "(absente)";
            if (a != b)
            {
                textDifferences++;
                report.Add($"DIFFÉRENT ligne {i + 1} :\n  avant : {a}\n  après : {b}");
            }
        }
        int instanceDifferences = 0;
        foreach (string file in Directory.GetFiles(reference, "*.bin").OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string other = Path.Combine(current, name + ".bin");
            string verdict = File.Exists(other) ? CompareInstances(File.ReadAllBytes(file), File.ReadAllBytes(other), out bool same) : "DIFFÉRENT : absent";
            if (!File.Exists(other) || verdict.StartsWith("DIFFÉRENT"))
                instanceDifferences++;
            report.Add($"{name} instances : {verdict}");
        }
        report.Insert(0, textDifferences == 0 && instanceDifferences == 0
            ? "IDENTIQUE à la référence"
            : $"DIFFÉRENCES : {textDifferences} ligne(s), {instanceDifferences} jeu(x) d'instances");
        File.WriteAllLines(Path.Combine(current, "compare.txt"), report);
    }

    /// <summary>
    /// Instances (flottants) égales à quelques ulp près : écart ≤ max(2e-6, 4 ulp). Au-delà, ou tailles différentes : DIFFÉRENT.
    /// </summary>
    static string CompareInstances(byte[] a, byte[] b, out bool bitExact)
    {
        bitExact = a.SequenceEqual(b);
        if (bitExact)
            return $"identiques au bit près ({a.Length / GpuInstanceData.Stride})";
        if (a.Length != b.Length)
            return $"DIFFÉRENT : {a.Length / GpuInstanceData.Stride} puis {b.Length / GpuInstanceData.Stride} instances";
        int floatsPerInstance = GpuInstanceData.Stride / 4;
        int changed = 0, beyond = 0, firstBeyond = -1;
        double maxDifference = 0;
        for (int offset = 0; offset < a.Length; offset += 4)
        {
            float x = BitConverter.ToSingle(a, offset);
            float y = BitConverter.ToSingle(b, offset);
            if (BitConverter.ToInt32(a, offset) == BitConverter.ToInt32(b, offset))
                continue;
            changed++;
            double difference = Math.Abs((double)x - y);
            maxDifference = Math.Max(maxDifference, difference);
            if (difference > Math.Max(2e-6, 4.0 * Ulp(Math.Max(Math.Abs(x), Math.Abs(y)))))
            {
                beyond++;
                if (firstBeyond < 0)
                    firstBeyond = offset / 4;
            }
        }
        return beyond == 0
            ? $"identiques à quelques ulp près ({changed} valeur(s) sur {a.Length / 4}, écart max {maxDifference:E2})"
            : $"DIFFÉRENT : {beyond} valeur(s) hors tolérance, écart max {maxDifference:E2}, première : instance {firstBeyond / floatsPerInstance}, composante {firstBeyond % floatsPerInstance}";
    }

    static double Ulp(float value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) + 1) - (double)value;

    /// <summary>Capture dans Logs/NyxGpuInstancingTest/{folder} : summary.txt et une copie brute des instances par spawner.</summary>
    static bool Capture(string folder)
    {
        string output = Path.Combine(LogFolder, folder);
        Directory.CreateDirectory(output);
        foreach (string old in Directory.GetFiles(output, "*.bin"))
            File.Delete(old);
        var open = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
            open.Add(SceneManager.GetSceneAt(i).path);
        var lines = new List<string> { $"{SystemInfo.graphicsDeviceName}", $"scènes ouvertes : {string.Join(", ", open)}" };
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        bool onGpu = MeshSpawner.CreateInstancesOnGpu;
        bool asyncCompilation = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            Camera camera = MakeCamera(Origin + new Vector3(0f, 25f, -60f), Origin);

            // A : rochers, rectangle procédural + ancres, sans volume
            MeshSpawner a = SpawnerA();
            a.RebuildNow();
            Describe(lines, output, a, camera, visible: true);

            // B : rochers, étoile de 16 sommets, volume, instances écrites en C#
            MeshSpawner.CreateInstancesOnGpu = false;
            MeshSpawner b = Rocks("B", Origin + new Vector3(0f, 0f, 30f));
            b.SetZone(new Vector3(0f, 0.5f, 0f), Star(16, 25f, 12f), 4f);
            b.ClipToVolume = true;
            b.RebuildNow();
            Describe(lines, output, b, camera, visible: true);
            MeshSpawner.CreateInstancesOnGpu = onGpu;

            lines.Add($"image A+B : {ImageHash(camera):X16}");

            // C : sargasses, zone naturelle, volume, hors texture de profondeur
            var cObject = new GameObject("C");
            cObject.transform.position = Origin + new Vector3(500f, 0f, 0f);
            cObject.AddComponent<FloatOnWaves>();
            MeshSpawner c = cObject.GetComponent<MeshSpawner>();
            c.SetVariants(NyxDefaultAssets.SargassumVariants());
            c.SetZone(Vector3.zero, Star(9, 30f, 18f), 6f);
            c.ZoneSmooth = true;
            c.ZoneEdgeNoise = 3f;
            c.ZoneEdgeNoiseScale = 12f;
            c.ZoneEdgeFeather = 5f;
            c.ClipToVolume = true;
            c.ExcludeFromDepthTexture = true;
            c.RebuildNow();
            Describe(lines, output, c, null, visible: false);
            ZoneQueries(lines, c);

            // D : rochers, liste explicite, volume
            MeshSpawner d = Rocks("D", Origin + new Vector3(-500f, 0f, 0f));
            d.SetZone(Vector3.zero, Star(5, 20f, 10f), 3f);
            d.ClipToVolume = true;
            var placements = new List<MeshPlacement>();
            var random = new System.Random(3);
            for (int i = 0; i < 900; i++)
            {
                placements.Add(new MeshPlacement
                {
                    position = d.transform.position + new Vector3((float)random.NextDouble() * 50f - 25f, (float)random.NextDouble() * 4f - 2f, (float)random.NextDouble() * 50f - 25f),
                    yaw = (float)random.NextDouble() * 360f,
                    scale = 0.5f + (float)random.NextDouble(),
                    variant = random.Next(0, 4),
                    seed = (float)random.NextDouble()
                });
            }
            d.SetPlacements(placements);
            d.RebuildNow();
            Describe(lines, output, d, null, visible: false);

            // E : bulles (instances propres au comportement, construites sur GPU)
            var eObject = new GameObject("E");
            eObject.transform.position = Origin + new Vector3(0f, -10f, 500f);
            eObject.AddComponent<BubbleColumns>();
            MeshSpawner e = eObject.GetComponent<MeshSpawner>();
            e.SetVariants(NyxDefaultAssets.BubbleVariants());
            e.RebuildNow();
            Describe(lines, output, e, null, visible: false);

            // F : placement de C figé dans la liste, puis reconstruit
            c.FreezePlacement();
            c.RebuildNow();
            c.name = "F";
            lines.Add($"F figé : {c.Placements.Count} placements, {PlacementHash(c.Placements):X16}");
            Dump(output, c);
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        finally
        {
            MeshSpawner.CreateInstancesOnGpu = onGpu;
            ShaderUtil.allowAsyncCompilation = asyncCompilation;
            if (previous.IsValid())
                SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
        }
        File.WriteAllLines(Path.Combine(output, SummaryFile), lines);
        return true;
    }

    /// <summary>A : rochers, rectangle procédural de 60 x 40 m + 2 ancres d'amas, sans volume.</summary>
    static MeshSpawner SpawnerA()
    {
        MeshSpawner a = Rocks("A", Origin);
        a.SetZone(Vector3.zero, MeshSpawner.RectanglePoints(new Vector2(60f, 40f)), 5f);
        a.ClipToVolume = false;
        var anchors = new SerializedObject(a);
        SerializedProperty list = anchors.FindProperty("clusterAnchors");
        list.arraySize = 2;
        list.GetArrayElementAtIndex(0).objectReferenceValue = Anchor(Origin + new Vector3(-10f, 0f, 5f));
        list.GetArrayElementAtIndex(1).objectReferenceValue = Anchor(Origin + new Vector3(12f, 0f, -8f));
        anchors.ApplyModifiedPropertiesWithoutUndo();
        return a;
    }

    static MeshSpawner Rocks(string name, Vector3 position)
    {
        var go = new GameObject(name);
        go.transform.position = position;
        var spawner = go.AddComponent<MeshSpawner>();
        go.AddComponent<StaticInstances>();
        spawner.SetVariants(NyxDefaultAssets.RockVariants());
        var settings = new SerializedObject(spawner);
        settings.FindProperty("densityPer100m2").floatValue = 40f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        return spawner;
    }

    static Transform Anchor(Vector3 position)
    {
        var anchor = new GameObject("ancre").transform;
        anchor.position = position;
        return anchor;
    }

    static List<Vector2> Star(int tips, float outer, float inner)
    {
        var points = new List<Vector2>();
        for (int i = 0; i < tips * 2; i++)
        {
            float angle = i * Mathf.PI / tips;
            float radius = i % 2 == 0 ? outer : inner;
            points.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
        return points;
    }

    static void Describe(List<string> lines, string output, MeshSpawner spawner, Camera camera, bool visible)
    {
        string prefix = spawner.name;
        lines.Add($"{prefix} : {spawner.InstanceCount} instances, envoi {spawner.LastUploadMethod}, {spawner.DrawCallsPerCamera} draws " +
                  $"[{string.Join(" ", spawner.DrawLabels)}], construit {spawner.IsBuilt}, mouvement hors volume {spawner.MotionExceedsVolumeHeight}");
        Dump(output, spawner);
        lines.Add($"{prefix} mémoire : GPU {spawner.GpuMemoryBytes} o, CPU {spawner.CpuMemoryBytes} o, jeux de culling {spawner.CullingBufferSets}");
        IReadOnlyList<MeshPlacement> placements = spawner.Placements;
        lines.Add($"{prefix} placements : {placements.Count}, {PlacementHash(placements):X16}, CPU après lecture {spawner.CpuMemoryBytes} o");
        if (visible && camera != null)
        {
            Render(camera);
            var counts = new List<int>();
            bool read = spawner.TryReadVisibleCounts(camera, counts);
            lines.Add($"{prefix} visibles : {read} [{string.Join(" ", counts)}], GPU {spawner.GpuMemoryBytes} o, jeux {spawner.CullingBufferSets}");
        }
    }

    static void ZoneQueries(List<string> lines, MeshSpawner spawner)
    {
        List<Vector2> outline = spawner.WorldOutline();
        ulong hash = Fnv();
        int inside = 0;
        Vector3 center = spawner.ZoneCenter;
        for (int x = -25; x <= 25; x++)
        {
            for (int z = -25; z <= 25; z++)
            {
                var p = new Vector3(center.x + x * 1.7f, center.y - (x + z) % 7, center.z + z * 1.7f);
                bool contains = spawner.VolumeContains(p);
                inside += contains ? 1 : 0;
                hash = Fnv(hash, contains ? 1 : 0);
                hash = Fnv(hash, BitConverter.SingleToInt32Bits(spawner.ZoneEdgeAt(new Vector2(p.x, p.z), outline, true)));
                hash = Fnv(hash, BitConverter.SingleToInt32Bits(spawner.ZoneEdgeAt(new Vector2(p.x, p.z), outline, false)));
            }
        }
        foreach (Vector2 point in outline)
            hash = Fnv(Fnv(hash, BitConverter.SingleToInt32Bits(point.x)), BitConverter.SingleToInt32Bits(point.y));
        lines.Add($"{spawner.name} zone : contour {outline.Count} sommets (local {spawner.ZoneOutline.Count}), {inside} points dedans, {hash:X16}");
    }

    /// <summary>Copie brute des instances du spawner ({nom}.bin), comparée à quelques ulp près par Compare.</summary>
    static void Dump(string output, MeshSpawner spawner)
    {
        var instances = new GpuInstanceData[spawner.InstanceCount];
        spawner.TryReadInstances(instances);
        using var native = new NativeArray<GpuInstanceData>(instances, Allocator.Temp);
        File.WriteAllBytes(Path.Combine(output, spawner.name + ".bin"), native.Reinterpret<byte>(GpuInstanceData.Stride).ToArray());
    }

    static ulong PlacementHash(IReadOnlyList<MeshPlacement> placements)
    {
        ulong hash = Fnv();
        foreach (MeshPlacement p in placements)
        {
            foreach (float value in new[] { p.position.x, p.position.y, p.position.z, p.yaw, p.scale, p.seed })
                hash = Fnv(hash, BitConverter.SingleToInt32Bits(value));
            hash = Fnv(hash, p.variant);
        }
        return hash;
    }

    static ulong Fnv() => 14695981039346656037UL;

    static ulong Fnv(ulong hash, int value)
    {
        for (int i = 0; i < 4; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    static ulong Fnv(ulong hash, byte value) => (hash ^ value) * 1099511628211UL;

    static Camera MakeCamera(Vector3 position, Vector3 target)
    {
        var camera = new GameObject("caméra").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.farClipPlane = 300f;
        camera.transform.position = position;
        camera.transform.LookAt(target);
        camera.enabled = false;
        return camera;
    }

    static void Render(Camera camera)
    {
        RenderTexture target = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = target;
        camera.Render();
        camera.targetTexture = null;
        RenderTexture.ReleaseTemporary(target);
    }

    static ulong ImageHash(Camera camera)
    {
        RenderTexture target = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        var image = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        image.Apply();
        RenderTexture.active = previous;
        camera.targetTexture = null;
        RenderTexture.ReleaseTemporary(target);
        ulong hash = Fnv();
        foreach (byte value in image.GetRawTextureData())
            hash = Fnv(hash, value);
        Object.DestroyImmediate(image);
        return hash;
    }
}
