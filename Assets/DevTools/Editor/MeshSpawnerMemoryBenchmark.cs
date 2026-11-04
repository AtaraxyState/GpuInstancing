using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Nyx.GpuInstancing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Mesure mémoire du MeshSpawner à grande échelle, dans une scène temporaire : mémoire managée retenue et allouée pendant
/// la reconstruction, mémoire native, temps de reconstruction, mémoire GPU après le rendu de 2 caméras (vue Game + vue Scene).
/// Résultat : Logs/NyxGpuInstancingTest/memory.txt. Lancement : invoke.request « MeshSpawnerMemoryBenchmark, Assembly-CSharp-Editor|Run ».
/// </summary>
static class MeshSpawnerMemoryBenchmark
{
    static readonly Func<long> AllocatedBytes = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>),
        typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes), false);

    public static void Run()
    {
        var lines = new List<string> { $"Unity {Application.unityVersion} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            Measure(lines, scene, "Static Instances, 1 000 000 rochers (1 variante x 2 LODs)", typeof(StaticInstances), NyxDefaultAssets.RockVariants());
            Measure(lines, scene, "Float On Waves, 1 000 000 touffes (2 variantes x 2 LODs)", typeof(FloatOnWaves), NyxDefaultAssets.SargassumVariants());
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "memory.txt"), lines);
    }

    static void Measure(List<string> lines, Scene scene, string label, Type behaviorType, List<InstanceVariant> variants)
    {
        var go = new GameObject(label);
        SceneManager.MoveGameObjectToScene(go, scene);
        var spawner = go.AddComponent<MeshSpawner>();
        go.AddComponent(behaviorType);
        spawner.SetVariants(variants);
        var settings = new SerializedObject(spawner);
        settings.FindProperty("areaSize").vector2Value = new Vector2(1000f, 1000f);
        settings.FindProperty("densityPer100m2").floatValue = 100f;
        settings.FindProperty("maxDrawDistance").floatValue = 2000f;
        settings.ApplyModifiedPropertiesWithoutUndo();

        GC.Collect();
        long managedBefore = GC.GetTotalMemory(true);
        long nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
        long allocatedBefore = AllocatedBytes?.Invoke() ?? 0;
        var watch = Stopwatch.StartNew();
        spawner.RebuildNow();
        watch.Stop();
        long allocated = (AllocatedBytes?.Invoke() ?? 0) - allocatedBefore;
        long managedRetained = GC.GetTotalMemory(true) - managedBefore;
        long nativeRetained = Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;

        // Deux caméras, comme la vue Game et la vue Scene de l'éditeur
        var cameras = new List<Camera>();
        for (int i = 0; i < 2; i++)
        {
            var camera = new GameObject("Camera " + i).AddComponent<Camera>();
            SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
            camera.transform.position = new Vector3(0f, 60f, -520f);
            camera.transform.LookAt(Vector3.zero);
            camera.farClipPlane = 2000f;
            camera.enabled = false;
            RenderTexture target = RenderTexture.GetTemporary(256, 256, 24);
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(target);
            cameras.Add(camera);
        }
        var counts = new List<int>();
        spawner.TryReadVisibleCounts(cameras[0], counts);
        int visible = 0;
        foreach (int count in counts)
            visible += count;

        lines.Add("");
        lines.Add(label);
        lines.Add($"  instances : {spawner.InstanceCount:N0}, visibles caméra 1 : {visible:N0}, draws : {spawner.DrawCallsPerCamera}");
        lines.Add($"  reconstruction : {watch.Elapsed.TotalMilliseconds:F0} ms");
        lines.Add($"  managé alloué pendant la reconstruction : {Mb(allocated)} ; managé retenu après : {Mb(managedRetained)}");
        lines.Add($"  natif retenu après la reconstruction : {Mb(nativeRetained)} ; tas Mono réservé : {Mb(Profiler.GetMonoHeapSizeLong())}");
        lines.Add($"  GPU (buffers du spawner, 2 caméras) : {Mb(spawner.GpuMemoryBytes)}");
        AddOptional(lines, spawner, "CpuMemoryBytes", value => $"  CPU retenu par le spawner (placements, natif) : {Mb((long)value)}");
        AddOptional(lines, spawner, "CullingBufferSets", value => $"  jeux de buffers de culling : {value}");

        foreach (Camera camera in cameras)
            Object.DestroyImmediate(camera.gameObject);
        Object.DestroyImmediate(go);
        GC.Collect();
    }

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} Mo";

    /// <summary>Propriété lue par réflexion : le banc compile aussi avec une version du package qui ne l'a pas.</summary>
    static void AddOptional(List<string> lines, MeshSpawner spawner, string property, Func<object, string> format)
    {
        PropertyInfo info = typeof(MeshSpawner).GetProperty(property);
        if (info != null)
            lines.Add(format(info.GetValue(spawner)));
    }
}
