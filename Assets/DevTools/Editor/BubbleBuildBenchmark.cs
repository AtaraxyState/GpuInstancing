using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Nyx.GpuInstancing;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Temps de construction d'un Mesh Spawner de bulles (Bubble Columns) à plusieurs échelles, dans une scène temporaire
/// (la scène ouverte n'est pas touchée) : reconstruction complète (colonnes construites sur GPU),
/// coût d'un Emit(). Médiane de plusieurs passes, après une passe de chauffe.
/// Résultat : Logs/NyxGpuInstancingTest/bubbles.txt. Lancement : invoke.request « BubbleBuildBenchmark, Assembly-CSharp-Editor|Run ».
/// </summary>
static class BubbleBuildBenchmark
{
    const int Runs = 7;
    const int Emits = 1000;

    public static void Run()
    {
        var lines = new List<string> { $"Unity {Application.unityVersion} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}", Describe() };
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);   // objets créés dans la scène temporaire : la scène ouverte reste propre
        try
        {
            Measure(lines, "Démo (3 colonnes x 600 + 512 ponctuelles)", 3, 600, 512);
            Measure(lines, "100 000 bulles (16 colonnes x 6 250 + 1 024)", 16, 6_250, 1024);
            Measure(lines, "1 000 000 bulles (16 colonnes x 62 500 + 1 024)", 16, 62_500, 1024);
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        finally
        {
            if (previous.IsValid())
                SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "bubbles.txt"), lines);
    }

    static void Measure(List<string> lines, string label, int emitterCount, int bubblesPerEmitter, int burstCapacity)
    {
        var go = new GameObject("Bubble benchmark");
        go.transform.position = new Vector3(5000f, OceanWaves.WaterLevel - 10f, 5000f);   // hors de vue de la scène ouverte
        var spawner = go.AddComponent<MeshSpawner>();
        var bubbles = go.AddComponent<BubbleColumns>();
        spawner.SetVariants(NyxDefaultAssets.BubbleVariants());
        var settings = new SerializedObject(bubbles);
        SerializedProperty emitters = settings.FindProperty("emitters");
        emitters.arraySize = emitterCount;
        for (int i = 0; i < emitterCount; i++)
        {
            SerializedProperty emitter = emitters.GetArrayElementAtIndex(i);
            emitter.FindPropertyRelative("anchor").objectReferenceValue = null;
            emitter.FindPropertyRelative("localPosition").vector3Value = new Vector3(i * 2f, 0f, 0f);
            emitter.FindPropertyRelative("bubbleCount").intValue = bubblesPerEmitter;
        }
        settings.FindProperty("burstCapacity").intValue = burstCapacity;
        settings.ApplyModifiedPropertiesWithoutUndo();

        try
        {
            spawner.RebuildNow();   // chauffe : compilation des shaders
            double rebuild = Median(() => spawner.RebuildNow());

            Vector3 diver = go.transform.position + Vector3.up * 2f;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < Emits; i++)
                bubbles.Emit(diver, 14, 0.03f);
            watch.Stop();
            // Même chose sans envoi GPU (spawner à reconstruire : UploadInstances ne fait rien) : part de l'envoi dans Emit
            spawner.SetDirty();
            var cpuOnly = Stopwatch.StartNew();
            for (int i = 0; i < Emits; i++)
                bubbles.Emit(diver, 14, 0.03f);
            cpuOnly.Stop();

            lines.Add("");
            lines.Add(label);
            lines.Add($"  instances : {spawner.InstanceCount:N0}, draws : {spawner.DrawCallsPerCamera}, origine : {spawner.LastUploadMethod}");
            lines.Add($"  reconstruction complète (CPU, colonnes construites sur GPU) : {rebuild:F2} ms");
            lines.Add($"  Emit(14 bulles) : {watch.Elapsed.TotalMilliseconds * 1000.0 / Emits:F1} µs par appel, " +
                      $"dont {(watch.Elapsed.TotalMilliseconds - cpuOnly.Elapsed.TotalMilliseconds) * 1000.0 / Emits:F1} µs d'envoi GPU");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    static double Median(Action action)
    {
        var times = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[Runs / 2];
    }

    /// <summary>Burst lu par réflexion : ce script ne dépend pas de l'assembly Unity.Burst.</summary>
    static string Describe()
    {
        Type burst = Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst");
        object enabled = burst?.GetProperty("IsEnabled")?.GetValue(null);
        object options = burst?.GetProperty("Options")?.GetValue(null);
        object safety = options?.GetType().GetProperty("EnableBurstSafetyChecks")?.GetValue(options);
        return $"CPU : {SystemInfo.processorType}, {SystemInfo.processorCount} threads logiques, {JobsUtility.JobWorkerCount} workers de jobs ; " +
               $"Burst : {enabled ?? "?"}, vérifications de sécurité : {safety ?? "?"} (éditeur)";
    }
}
