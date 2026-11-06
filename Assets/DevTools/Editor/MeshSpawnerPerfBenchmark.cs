using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Nyx.GpuInstancing;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Banc de performance du MeshSpawner dans une scène temporaire. Les spawners mesurés sont sur une couche dédiée que seule la
/// caméra du banc voit : la scène ouverte n'est ni modifiée ni rendue par le banc. Pour chaque cas :
///  - reconstruction (médiane) ;
///  - image d'édition : reconstruction + premier rendu + attente de la fin du GPU (déplacement d'un spawner dans l'éditeur) ;
///  - rendu en régime établi : CPU seul, puis jusqu'à la fin du GPU ; octets managés alloués par image.
/// Résultat : Logs/NyxGpuInstancingTest/perf.txt. Lancement : invoke.request « MeshSpawnerPerfBenchmark, Assembly-CSharp-Editor|Run »
/// (ou |RunDemoRender, |RunZoneCost pour une seule partie).
/// </summary>
static class MeshSpawnerPerfBenchmark
{
    const int Layer = 31;
    const int RebuildRuns = 5;
    const int Frames = 30;

    static readonly Func<long> AllocatedBytes = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>),
        typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes), false);

    public static void Run() => RunInScene("perf.txt", MeasureAll);

    /// <summary>Seulement le rendu des sargasses de la démo selon les ombres (quelques secondes) : itérations sur les shaders.</summary>
    public static void RunDemoRender() => RunInScene("perf_render.txt", MeasureDemoRenderVariants);

    /// <summary>Seulement le coût de la zone (placement et destruction par le volume).</summary>
    public static void RunZoneCost() => RunInScene("perf_zone.txt", MeasureZoneCost);

    static void RunInScene(string file, Action<List<string>, Camera, Vector3> measure)
    {
        var lines = new List<string>
        {
            $"Unity {Application.unityVersion} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"CPU : {SystemInfo.processorType}, {JobsUtility.JobWorkerCount} workers de jobs ; GPU : {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}) ; image 1280 x 720"
        };
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);   // objets créés dans la scène temporaire : la scène ouverte reste propre
        var target = new RenderTexture(1280, 720, 24) { name = "MeshSpawnerPerfBenchmark" };
        var camera = new GameObject("Caméra du banc").AddComponent<Camera>();
        camera.enabled = false;
        camera.cullingMask = 1 << Layer;
        camera.targetTexture = target;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 500f;
        try
        {
            measure(lines, camera, new Vector3(5000f, 0f, 5000f));   // loin de la scène ouverte
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        finally
        {
            camera.targetTexture = null;
            Object.DestroyImmediate(camera.gameObject);
            Object.DestroyImmediate(target);
            if (previous.IsValid())
                SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, file), lines);
    }

    static void MeasureAll(List<string> lines, Camera camera, Vector3 origin)
    {
        MeasurePlaced(lines, camera, "Static Instances, 1 000 000 rochers, placement procédural",
            typeof(StaticInstances), NyxDefaultAssets.RockVariants(), origin, 1000f, 100f, false);
        MeasurePlaced(lines, camera, "Static Instances, 1 000 000 rochers, placement figé (liste)",
            typeof(StaticInstances), NyxDefaultAssets.RockVariants(), origin, 1000f, 100f, true);
        MeasurePlaced(lines, camera, "Float On Waves, 1 000 000 touffes",
            typeof(FloatOnWaves), NyxDefaultAssets.SargassumVariants(), origin, 1000f, 100f, false);
        MeasurePlaced(lines, camera, "Float On Waves, scène de démo (80 x 80 m à 10 000 / 100 m²)",
            typeof(FloatOnWaves), NyxDefaultAssets.SargassumVariants(), origin, 80f, 10000f, false);
        MeasureBubbles(lines, camera, "Bubble Columns, 1 000 000 bulles (16 colonnes x 62 500 + 1 024)", origin, 16, 62_500, 1024);
        MeasureBubbles(lines, camera, "Bubble Columns, démo (3 colonnes x 600 + 512)", origin, 3, 600, 512);
        MeasureDemoRenderVariants(lines, camera, origin);
        MeasureZoneCost(lines, camera, origin);
        MeasureDecomposition(lines);
    }

    /// <summary>
    /// Coût de la zone sur Static Instances (1 instance par m², carré de 1 000 m) : reconstruction et culling GPU seul
    /// (caméra au centre, distance max 1 m : tout est évalué et testé contre le volume, presque rien n'est dessiné).
    /// Rochers immobiles posés sur la face du haut : aucun n'est détruit, le test du volume est payé pour chaque instance.
    /// </summary>
    static void MeasureZoneCost(List<string> lines, Camera camera, Vector3 center)
    {
        lines.Add("");
        lines.Add("Zone (Static Instances, carré de 1 000 m à 1 / m²) : reconstruction ; culling GPU seul (tout évalué, rien dessiné)");
        var polygon = new List<Vector2>();
        for (int i = 0; i < MeshSpawner.MaxZonePoints; i++)
        {
            float angle = i * Mathf.PI * 2f / MeshSpawner.MaxZonePoints;
            float radius = (i % 2 == 0 ? 500f : 420f);   // étoile : concave, 64 sommets (pire cas)
            polygon.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
        var cases = new (string label, List<Vector2> points, bool clip)[]
        {
            ("rectangle, sans volume", MeshSpawner.RectanglePoints(new Vector2(1000f, 1000f)), false),
            ("rectangle, volume actif", MeshSpawner.RectanglePoints(new Vector2(1000f, 1000f)), true),
            ("étoile de 64 sommets, volume actif", polygon, true),
            ("rectangle, sans volume (remesuré en dernier)", MeshSpawner.RectanglePoints(new Vector2(1000f, 1000f)), false),
        };
        foreach ((string label, List<Vector2> points, bool clip) in cases)
        {
            var go = new GameObject("Coût de la zone");
            go.transform.position = center;
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<StaticInstances>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            Configure(spawner, settings =>
            {
                settings.FindProperty("densityPer100m2").floatValue = 100f;
                settings.FindProperty("renderLayer").intValue = Layer;
                settings.FindProperty("maxDrawDistance").floatValue = 1f;
            });
            spawner.SetZone(Vector3.zero, points, 5f);
            spawner.ClipToVolume = clip;
            try
            {
                spawner.RebuildNow();   // chauffe
                double rebuild = Median(RebuildRuns, () => spawner.RebuildNow());
                camera.transform.SetPositionAndRotation(center + Vector3.up * 0.05f, Quaternion.identity);
                RenderAndWait(camera);
                double withSpawner = Median(Frames, () => RenderAndWait(camera));
                spawner.enabled = false;
                RenderAndWait(camera);
                double without = Median(Frames, () => RenderAndWait(camera));
                spawner.enabled = true;
                spawner.RebuildNow();
                RenderAndWait(camera);
                var data = new GpuInstanceData[spawner.InstanceCount];
                spawner.TryReadInstances(data);
                int destroyed = 0;
                foreach (GpuInstanceData instance in data)
                {
                    if ((int)instance.custom.w == spawner.Variants.Count)
                        destroyed++;
                }
                lines.Add($"  {label,-36} : {spawner.InstanceCount,9:N0} instances ; reconstruction {rebuild,6:F2} ms ; " +
                          $"culling ≈ {Math.Max(0.0, withSpawner - without):F3} ms ; détruites : {destroyed}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }

    static void MeasurePlaced(List<string> lines, Camera camera, string label, Type behaviorType, List<InstanceVariant> variants,
                              Vector3 center, float area, float density, bool frozen)
    {
        var go = new GameObject(label);
        go.transform.position = center;
        var spawner = go.AddComponent<MeshSpawner>();
        go.AddComponent(behaviorType);
        spawner.SetVariants(variants);
        Configure(spawner, settings =>
        {
            settings.FindProperty("densityPer100m2").floatValue = density;
            settings.FindProperty("renderLayer").intValue = Layer;
        });
        RectangleZone(spawner, area);
        try
        {
            if (frozen)
            {
                spawner.RebuildNow();
                spawner.SetPlacements(new List<MeshPlacement>(spawner.Placements));
            }
            Aim(camera, center + new Vector3(0f, 6f, -45f), center + new Vector3(0f, 0f, -5f));
            Measure(lines, label, spawner, camera);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    static void MeasureBubbles(List<string> lines, Camera camera, string label, Vector3 center, int emitterCount, int bubblesPerEmitter, int burstCapacity)
    {
        var go = new GameObject(label);
        Vector3 position = new Vector3(center.x, OceanWaves.WaterLevel - 10f, center.z);
        go.transform.position = position;
        var spawner = go.AddComponent<MeshSpawner>();
        var bubbles = go.AddComponent<BubbleColumns>();
        spawner.SetVariants(NyxDefaultAssets.BubbleVariants());
        Configure(bubbles, settings =>
        {
            SerializedProperty emitters = settings.FindProperty("emitters");
            emitters.arraySize = emitterCount;
            for (int i = 0; i < emitterCount; i++)
            {
                SerializedProperty emitter = emitters.GetArrayElementAtIndex(i);
                emitter.FindPropertyRelative("anchor").objectReferenceValue = null;
                emitter.FindPropertyRelative("localPosition").vector3Value = new Vector3(i * 2f, 0f, 0f);
                emitter.FindPropertyRelative("bubbleCount").intValue = bubblesPerEmitter;
                emitter.FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            }
            settings.FindProperty("burstCapacity").intValue = burstCapacity;
        });
        Configure(spawner, settings =>
        {
            settings.FindProperty("renderLayer").intValue = Layer;
            settings.FindProperty("maxDrawDistance").floatValue = 60f;
        });
        try
        {
            float columnsCenter = emitterCount - 1f;
            Aim(camera, position + new Vector3(columnsCenter, 4f, -12f), position + new Vector3(columnsCenter, 6f, 0f));
            Measure(lines, label, spawner, camera);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    static void Measure(List<string> lines, string label, MeshSpawner spawner, Camera camera)
    {
        // Chauffe : compilation Burst, variantes de shaders, premiers buffers
        spawner.RebuildNow();
        RenderAndWait(camera);
        RenderAndWait(camera);

        double rebuild = Median(RebuildRuns, () => spawner.RebuildNow());
        MeshSpawner.UploadMethod rebuildMethod = spawner.LastUploadMethod;
        double editFrame = Median(RebuildRuns, () =>
        {
            spawner.RebuildNow();
            RenderAndWait(camera);
        });
        MeshSpawner.UploadMethod editMethod = spawner.LastUploadMethod;

        // Même chose avec les instances écrites en C# (placements lus sur GPU, job Burst, envoi), et contenu comparé
        string cpuPath = "sans objet (instances propres au comportement)";
        if (spawner.Behavior != null && spawner.Behavior.UsesPlacement)
        {
            var onGpu = new GpuInstanceData[spawner.InstanceCount];
            spawner.TryReadInstances(onGpu);
            MeshSpawner.CreateInstancesOnGpu = false;
            try
            {
                spawner.RebuildNow();
                double rebuildCpu = Median(RebuildRuns, () => spawner.RebuildNow());
                MeshSpawner.UploadMethod cpuMethod = spawner.LastUploadMethod;
                double editFrameCpu = Median(RebuildRuns, () =>
                {
                    spawner.RebuildNow();
                    RenderAndWait(camera);
                });
                var onCpu = new GpuInstanceData[spawner.InstanceCount];
                spawner.TryReadInstances(onCpu);
                cpuPath = $"reconstruction {rebuildCpu:F2} ms ({cpuMethod}) ; image d'édition {editFrameCpu:F2} ms ; " +
                          $"écart max avec le GPU : {(onCpu.Length == onGpu.Length ? MaxDifference(onGpu, onCpu).ToString("G3") : $"{onCpu.Length} instances au lieu de {onGpu.Length}")}";
            }
            finally
            {
                MeshSpawner.CreateInstancesOnGpu = true;
            }
        }
        spawner.RebuildNow();
        double renderCpu = Median(Frames, () => camera.Render());
        RenderAndWait(camera);   // vide la file du GPU avant la mesure suivante
        double renderGpu = Median(Frames, () => RenderAndWait(camera));

        long before = AllocatedBytes?.Invoke() ?? 0;
        for (int i = 0; i < Frames; i++)
            camera.Render();
        long allocatedPerFrame = ((AllocatedBytes?.Invoke() ?? 0) - before) / Frames;

        RenderAndWait(camera);
        var counts = new List<int>();
        spawner.TryReadVisibleCounts(camera, counts);
        int visible = 0;
        foreach (int count in counts)
            visible += count;
        string summary = $"  instances : {spawner.InstanceCount:N0}, visibles : {visible:N0}, draws : {spawner.DrawCallsPerCamera}, " +
                         $"GPU : {spawner.GpuMemoryBytes / (1024.0 * 1024.0):F1} Mo";

        // Même vue avec 16 fois moins de pixels : ce qui ne baisse pas est le coût des sommets (et du culling)
        RenderTexture fullTarget = camera.targetTexture;
        RenderTexture smallTarget = RenderTexture.GetTemporary(320, 180, 24);
        camera.targetTexture = smallTarget;
        RenderAndWait(camera);
        double renderGpuSmall = Median(Frames, () => RenderAndWait(camera));
        camera.targetTexture = fullTarget;
        RenderTexture.ReleaseTemporary(smallTarget);

        // Image sans le spawner (même vue) : ce qui reste est le coût GPU du spawner (culling + draws)
        spawner.enabled = false;
        RenderAndWait(camera);
        double emptyGpu = Median(Frames, () => RenderAndWait(camera));
        spawner.enabled = true;
        spawner.RebuildNow();

        // Culling seul : caméra dans les bounds du spawner, distance max 1 m (toutes les instances évaluées, presque aucune dessinée)
        Vector3 pose = camera.transform.position;
        Quaternion rotation = camera.transform.rotation;
        float maxDistance = 0f;
        Configure(spawner, settings =>
        {
            maxDistance = settings.FindProperty("maxDrawDistance").floatValue;
            settings.FindProperty("maxDrawDistance").floatValue = 1f;
        });
        camera.transform.SetPositionAndRotation(spawner.transform.position + Vector3.up * 0.05f, Quaternion.identity);
        RenderAndWait(camera);
        double cullOnlyGpu = Median(Frames, () => RenderAndWait(camera));
        spawner.enabled = false;
        RenderAndWait(camera);
        double cullEmptyGpu = Median(Frames, () => RenderAndWait(camera));
        spawner.enabled = true;
        Configure(spawner, settings => settings.FindProperty("maxDrawDistance").floatValue = maxDistance);
        camera.transform.SetPositionAndRotation(pose, rotation);

        lines.Add("");
        lines.Add(label);
        lines.Add(summary);
        lines.Add($"  reconstruction : {rebuild:F2} ms ({rebuildMethod}) ; image d'édition (reconstruction + premier rendu + fin du GPU) : {editFrame:F2} ms ({editMethod})");
        lines.Add($"    instances écrites en C# : {cpuPath}");
        lines.Add($"  rendu : {renderCpu:F3} ms CPU ; {renderGpu:F3} ms jusqu'à la fin du GPU (sans le spawner : {emptyGpu:F3} ms) ; {allocatedPerFrame} o managés alloués par image");
        lines.Add($"  même vue en 320 x 180 : {renderGpuSmall:F3} ms jusqu'à la fin du GPU (part des pixels ≈ {Math.Max(0.0, renderGpu - renderGpuSmall):F3} ms)");
        lines.Add($"  culling GPU seul (tout évalué, rien dessiné) : ≈ {Math.Max(0.0, cullOnlyGpu - cullEmptyGpu):F3} ms");
    }

    /// <summary>
    /// Rendu des sargasses de la démo (640 000 touffes, ~540 000 visibles) selon les ombres : réglage par défaut (ombres
    /// reçues, soleil à ombres douces : passe de profondeur pour les ombres en espace écran), ombres coupées dans la qualité,
    /// ombres non reçues, ombres projetées. Réglages restaurés après chaque mesure.
    /// </summary>
    static void MeasureDemoRenderVariants(List<string> lines, Camera camera, Vector3 center)
    {
        var go = new GameObject("Variantes de rendu de la démo");
        go.transform.position = center;
        var spawner = go.AddComponent<MeshSpawner>();
        go.AddComponent<FloatOnWaves>();
        spawner.SetVariants(NyxDefaultAssets.SargassumVariants());
        Configure(spawner, settings =>
        {
            settings.FindProperty("densityPer100m2").floatValue = 10000f;
            settings.FindProperty("renderLayer").intValue = Layer;
        });
        RectangleZone(spawner, 80f);
        ShadowQuality quality = QualitySettings.shadows;
        try
        {
            Aim(camera, center + new Vector3(0f, 6f, -45f), center + new Vector3(0f, 0f, -5f));
            spawner.RebuildNow();
            RenderAndWait(camera);
            double standard = Median(Frames, () => RenderAndWait(camera));

            QualitySettings.shadows = ShadowQuality.Disable;
            RenderAndWait(camera);
            double noShadows = Median(Frames, () => RenderAndWait(camera));
            QualitySettings.shadows = quality;

            Configure(spawner, settings => settings.FindProperty("receiveShadows").boolValue = false);
            RenderAndWait(camera);
            double noReceive = Median(Frames, () => RenderAndWait(camera));
            Configure(spawner, settings => settings.FindProperty("receiveShadows").boolValue = true);

            spawner.ExcludeFromDepthTexture = true;
            spawner.RebuildNow();
            RenderAndWait(camera);
            double depthless = Median(Frames, () => RenderAndWait(camera));
            spawner.ExcludeFromDepthTexture = false;
            spawner.RebuildNow();

            Configure(spawner, settings => settings.FindProperty("castShadows").enumValueIndex = (int)ShadowCastingMode.On);
            RenderAndWait(camera);
            double cast = Median(Frames, () => RenderAndWait(camera));

            lines.Add("");
            lines.Add($"Sargasses de la démo, rendu selon les ombres (soleil : {(RenderSettings.sun != null ? RenderSettings.sun.shadows.ToString() : "aucun")})");
            lines.Add($"  par défaut (ombres reçues) : {standard:F3} ms ; ombres coupées (qualité) : {noShadows:F3} ms ; " +
                      $"ombres non reçues : {noReceive:F3} ms ; hors de la passe de profondeur : {depthless:F3} ms ; ombres projetées : {cast:F3} ms");
        }
        finally
        {
            QualitySettings.shadows = quality;
            Object.DestroyImmediate(go);
        }
    }

    /// <summary>Décomposition d'une reconstruction de 1 000 000 d'instances avec les briques publiques du package.</summary>
    static void MeasureDecomposition(List<string> lines)
    {
        const int count = 1_000_000;
        var placements = new NativeArray<MeshPlacement>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var list = new List<MeshPlacement>(count);
        for (int i = 0; i < count; i++)
        {
            var placement = new MeshPlacement { position = new Vector3(i % 1000, 0f, i / 1000), yaw = i * 0.37f, scale = 1f, seed = (i % 997) / 997f };
            placements[i] = placement;
            list.Add(placement);
        }
        var buffer = new ComputeBuffer(count, GpuInstanceData.Stride, ComputeBufferType.Default);
        try
        {
            double allocUninit = Median(RebuildRuns, () =>
                new NativeArray<GpuInstanceData>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory).Dispose());
            double allocClear = Median(RebuildRuns, () => new NativeArray<GpuInstanceData>(count, Allocator.TempJob).Dispose());
            double fill = Median(RebuildRuns, () =>
            {
                var instances = new NativeArray<GpuInstanceData>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                new MeshSpawnerJobs.TrsInstancesJob { placements = placements, output = instances }
                    .Schedule(count, MeshSpawnerJobs.BatchSize).Complete();
                instances.Dispose();
            });
            var data = new NativeArray<GpuInstanceData>(count, Allocator.TempJob);
            double upload, uploadFresh, staged;
            try
            {
                upload = Median(RebuildRuns, () => buffer.SetData(data));
                // Même envoi dans un buffer neuf à chaque fois (aucune attente possible sur un buffer encore lu par le GPU)
                uploadFresh = Median(RebuildRuns, () =>
                {
                    var fresh = new ComputeBuffer(count, GpuInstanceData.Stride, ComputeBufferType.Default);
                    fresh.SetData(data);
                    fresh.Release();
                });
                // Écriture directe par un job parallèle dans un buffer SubUpdates (mémoire visible du CPU), sans SetData
                staged = Median(RebuildRuns, () =>
                {
                    var staging = new ComputeBuffer(count, GpuInstanceData.Stride, ComputeBufferType.Default, ComputeBufferMode.SubUpdates);
                    NativeArray<GpuInstanceData> mapped = staging.BeginWrite<GpuInstanceData>(0, count);
                    new CopyInstancesJob { source = data, destination = mapped }.Schedule(count, MeshSpawnerJobs.BatchSize).Complete();
                    staging.EndWrite<GpuInstanceData>(count);
                    staging.Release();
                });
            }
            finally
            {
                data.Dispose();
            }
            double listCopy = Median(RebuildRuns, () =>
            {
                var native = new NativeArray<MeshPlacement>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < count; i++)
                    native[i] = list[i];
                native.Dispose();
            });
            MeshPlacement[] array = list.ToArray();
            double arrayCopy = Median(RebuildRuns, () =>
            {
                var native = new NativeArray<MeshPlacement>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeArray<MeshPlacement>.Copy(array, native);
                native.Dispose();
            });

            lines.Add("");
            lines.Add("Décomposition, 1 000 000 d'instances (briques publiques du package)");
            lines.Add($"  tableau natif de 64 Mo : allocation + libération {allocUninit:F2} ms ; avec mise à zéro {allocClear:F2} ms");
            lines.Add($"  allocation + remplissage parallèle (TrsInstancesJob) + libération : {fill:F2} ms");
            lines.Add($"  envoi au GPU (ComputeBuffer.SetData, 64 Mo) : {upload:F2} ms ; dans un buffer neuf : {uploadFresh:F2} ms ; " +
                      $"écriture parallèle dans un buffer SubUpdates (BeginWrite/EndWrite) : {staged:F2} ms");
            lines.Add($"  copie d'une List<MeshPlacement> élément par élément : {listCopy:F2} ms ; d'un tableau (NativeArray.Copy) : {arrayCopy:F2} ms");
        }
        finally
        {
            placements.Dispose();
            buffer.Release();
        }
    }

    /// <summary>Empreinte du contenu (un seul tableau de relecture pour les deux méthodes d'envoi).</summary>
    /// <summary>Plus grand écart entre deux jeux d'instances de même taille (toutes composantes), +infini si une variante diffère.</summary>
    static float MaxDifference(GpuInstanceData[] a, GpuInstanceData[] b)
    {
        float worst = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].custom.w != b[i].custom.w)
                return float.PositiveInfinity;
            for (int k = 0; k < 4; k++)
            {
                worst = Mathf.Max(worst, Mathf.Abs(a[i].row0[k] - b[i].row0[k]));
                worst = Mathf.Max(worst, Mathf.Abs(a[i].row1[k] - b[i].row1[k]));
                worst = Mathf.Max(worst, Mathf.Abs(a[i].row2[k] - b[i].row2[k]));
                worst = Mathf.Max(worst, Mathf.Abs(a[i].custom[k] - b[i].custom[k]));
            }
        }
        return worst;
    }

    /// <summary>Rendu puis attente de la fin du GPU (relecture d'un pixel de la cible).</summary>
    static void RenderAndWait(Camera camera)
    {
        camera.Render();
        AsyncGPUReadback.Request(camera.targetTexture, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
    }

    /// <summary>Zone carrée centrée sur l'objet, sans destruction par le volume : mesures comparables à celles d'avant la zone.</summary>
    static void RectangleZone(MeshSpawner spawner, float side)
    {
        spawner.SetZone(Vector3.zero, MeshSpawner.RectanglePoints(new Vector2(side, side)), 10f);
        spawner.ClipToVolume = false;
    }

    static void Configure(Object target, Action<SerializedObject> edit)
    {
        var settings = new SerializedObject(target);
        edit(settings);
        settings.ApplyModifiedPropertiesWithoutUndo();
    }

    static void Aim(Camera camera, Vector3 position, Vector3 lookAt)
    {
        camera.transform.position = position;
        camera.transform.LookAt(lookAt);
    }

    [Unity.Burst.BurstCompile(CompileSynchronously = true)]
    struct CopyInstancesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<GpuInstanceData> source;
        [WriteOnly] public NativeArray<GpuInstanceData> destination;

        public void Execute(int index) => destination[index] = source[index];
    }

    static double Median(int runs, Action action)
    {
        var times = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[runs / 2];
    }
}
