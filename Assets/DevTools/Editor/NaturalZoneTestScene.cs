using System.Collections.Generic;
using System.IO;
using Nyx.GpuInstancing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Outil de développement : scène de test d'une zone naturelle précise (Assets/DevTools/Scenes/ZoneNaturelleTest.unity).
///  - « Zone détaillée (2048 sommets) » : sargasses (Float On Waves) dans une côte décrite par 2 048 sommets, visibles et
///    modifiables dans l'inspecteur (Zone Points), volume actif.
///  - « Zone naturelle (16 points + bord naturel) », désactivée, au même endroit : la même forme générale décrite par
///    16 points, avec contour courbe, bord irrégulier et lisière. Activer l'une ou l'autre pour comparer l'aspect et le coût.
/// Menu Tools > Nyx > GPU Instancing > Dev, ou invoke.request « NaturalZoneTestScene, Assembly-CSharp-Editor|Create ».
/// Ne fait rien si une scène ouverte a des modifications non enregistrées.
/// </summary>
static class NaturalZoneTestScene
{
    const string ScenePath = "Assets/DevTools/Scenes/ZoneNaturelleTest.unity";
    const int DetailedPoints = 2048;
    const int ControlPoints = 16;
    const float Radius = 55f;
    const int Seed = 7;
    const float Density = 2500f;   // touffes pour 100 m² : environ 240 000 dans la zone

    [MenuItem("Tools/Nyx/GPU Instancing/Dev/Scène de test : zone naturelle détaillée")]
    public static void Create()
    {
        var log = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene open = SceneManager.GetSceneAt(i);
            if (open.isDirty)
            {
                log.Add($"ARRÊT : la scène {open.path} a des modifications non enregistrées.");
                Write(log);
                return;
            }
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Camera camera = Camera.main;
        camera.transform.position = new Vector3(0f, 38f, -95f);
        camera.transform.LookAt(new Vector3(0f, 0f, -5f));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.04f, 0.22f, 0.32f);
        camera.farClipPlane = 400f;
        camera.gameObject.AddComponent<NyxFlyCamera>();
        new GameObject("Ocean Waves").AddComponent<OceanWaves>();

        MeshSpawner detailed = CreateSargassum($"Zone détaillée ({DetailedPoints} sommets)", Coastline(DetailedPoints, int.MaxValue));
        detailed.RebuildNow();
        log.Add($"{detailed.name} : {detailed.ZonePoints.Count} sommets, {detailed.InstanceCount} touffes");

        MeshSpawner natural = CreateSargassum($"Zone naturelle ({ControlPoints} points + bord naturel)", Coastline(ControlPoints, 3));
        natural.ZoneSmooth = true;
        natural.ZoneEdgeNoise = 4f;
        natural.ZoneEdgeNoiseScale = 14f;
        natural.ZoneEdgeFeather = 4f;
        natural.RebuildNow();
        log.Add($"{natural.name} : {natural.ZonePoints.Count} points, {natural.InstanceCount} touffes (désactivée)");
        natural.gameObject.SetActive(false);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        Selection.activeGameObject = detailed.gameObject;
        bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
        log.Add($"scène {ScenePath} enregistrée : {saved}");
        Write(log);
    }

    /// <summary>Sargasses comme « Add Component » : MeshSpawner, touffes par défaut, volume posé sur l'eau ; puis la zone.</summary>
    static MeshSpawner CreateSargassum(string name, List<Vector2> points)
    {
        var go = new GameObject(name);
        ObjectFactory.AddComponent<FloatOnWaves>(go);
        var spawner = go.GetComponent<MeshSpawner>();
        spawner.SetZone(spawner.ZoneCenterOffset, points, spawner.ZoneDepth);
        spawner.ClipToVolume = true;
        var settings = new SerializedObject(spawner);
        settings.FindProperty("densityPer100m2").floatValue = Density;
        settings.FindProperty("maxDrawDistance").floatValue = 250f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        return spawner;
    }

    /// <summary>
    /// Côte autour du centre : rayon modulé par des harmoniques d'amplitude décroissante (~1 / k), phases tirées une fois
    /// pour toutes (même graine) : les mêmes grands lobes quel que soit le nombre de sommets, et de plus en plus de détails
    /// avec maxHarmonic. Toujours un polygone simple (rayon > 0 dans chaque direction).
    /// </summary>
    static List<Vector2> Coastline(int count, int maxHarmonic)
    {
        const int harmonics = 512;
        var random = new System.Random(Seed);
        var amplitude = new float[harmonics + 1];
        var phase = new float[harmonics + 1];
        for (int k = 2; k <= harmonics; k++)
        {
            amplitude[k] = (k <= 3 ? 0.16f : 0.22f) * Mathf.Pow(k, -1.1f);
            phase[k] = (float)(random.NextDouble() * Mathf.PI * 2.0);
        }
        int used = Mathf.Min(maxHarmonic, Mathf.Min(harmonics, count / 4));
        var points = new List<Vector2>(count);
        for (int i = 0; i < count; i++)
        {
            float t = i * Mathf.PI * 2f / count;
            float s = 0f;
            for (int k = 2; k <= used; k++)
                s += amplitude[k] * Mathf.Sin(k * t + phase[k]);
            float r = Radius * Mathf.Max(0.35f, 1f + s);
            points.Add(new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r);
        }
        return points;
    }

    /// <summary>Diagnostic : dernière opération d'annulation et état des scènes ouvertes (zone_test_scene.txt).</summary>
    public static void Diagnose()
    {
        var log = new List<string> { $"dernière opération : « {Undo.GetCurrentGroupName()} » (groupe {Undo.GetCurrentGroup()})" };
        for (int i = 0; i < SceneManager.sceneCount; i++)
            log.Add($"{SceneManager.GetSceneAt(i).path} : modifiée {SceneManager.GetSceneAt(i).isDirty}");
        Write(log);
    }

    /// <summary>
    /// Compare les deux zones de la scène de test ouverte, vue de la caméra principale en 1280 x 720 : reconstruction et
    /// temps GPU par image, avec et sans Clip To Volume (le test de la zone à chaque image). Tours aller-retour. État restauré.
    /// </summary>
    public static void Compare()
    {
        var log = new List<string> { $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} - {SystemInfo.graphicsDeviceName}, 1280 x 720, médiane de 60 images, aller-retour" };
        var spawners = new List<MeshSpawner>();
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            var spawner = root.GetComponent<MeshSpawner>();
            if (spawner != null)
                spawners.Add(spawner);
        }
        Camera camera = Camera.main;
        if (SceneManager.GetActiveScene().path != ScenePath || spawners.Count != 2 || camera == null)
        {
            log.Add("ARRÊT : ouvrir la scène de test (2 Mesh Spawners, une caméra principale).");
            Write(log);
            return;
        }
        var active = spawners.ConvertAll(s => s.gameObject.activeSelf);
        var clip = spawners.ConvertAll(s => s.ClipToVolume);
        var target = new RenderTexture(1280, 720, 24);
        camera.targetTexture = target;
        var rebuild = new double[2, 2];
        var frame = new double[2, 2];
        var count = new int[2];
        try
        {
            foreach (int pass in new[] { 0, 1 })
            {
                for (int k = 0; k < 4; k++)
                {
                    int c = pass == 0 ? k : 3 - k;
                    int s = c / 2;
                    bool withClip = c % 2 == 0;
                    for (int i = 0; i < 2; i++)
                        spawners[i].gameObject.SetActive(i == s);
                    spawners[s].ClipToVolume = withClip;
                    spawners[s].RebuildNow();
                    var times = new double[7];
                    for (int r = 0; r < times.Length; r++)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        spawners[s].RebuildNow();
                        RenderAndWait(camera);
                        times[r] = watch.Elapsed.TotalMilliseconds;
                    }
                    System.Array.Sort(times);
                    rebuild[s, withClip ? 0 : 1] += times[3] / 2.0;
                    for (int w = 0; w < 5; w++)
                        RenderAndWait(camera);
                    var frames = new double[60];
                    for (int f = 0; f < frames.Length; f++)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        RenderAndWait(camera);
                        frames[f] = watch.Elapsed.TotalMilliseconds;
                    }
                    System.Array.Sort(frames);
                    frame[s, withClip ? 0 : 1] += frames[30] / 2.0;
                    count[s] = spawners[s].InstanceCount;
                }
            }
            for (int s = 0; s < 2; s++)
            {
                log.Add($"{spawners[s].name} : {spawners[s].ZonePoints.Count} sommets, {count[s]:N0} touffes");
                log.Add($"  image avec Clip To Volume {frame[s, 0]:F2} ms, sans {frame[s, 1]:F2} ms (test de la zone à chaque image ≈ {frame[s, 0] - frame[s, 1]:F2} ms)");
                log.Add($"  reconstruction + premier rendu : avec Clip To Volume {rebuild[s, 0]:F2} ms, sans {rebuild[s, 1]:F2} ms");
            }
        }
        finally
        {
            for (int i = 0; i < 2; i++)
            {
                spawners[i].ClipToVolume = clip[i];
                spawners[i].gameObject.SetActive(active[i]);
            }
            camera.targetTexture = null;
            Object.DestroyImmediate(target);
        }
        Write(log);
    }

    static void RenderAndWait(Camera camera)
    {
        camera.Render();
        UnityEngine.Rendering.AsyncGPUReadback.Request(camera.targetTexture, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
    }

    /// <summary>Ouvre la scène de test, zone détaillée sélectionnée (si aucune scène ouverte n'a de modification non enregistrée).</summary>
    public static void Open()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i).isDirty)
            {
                Write(new List<string> { $"ARRÊT : la scène {SceneManager.GetSceneAt(i).path} a des modifications non enregistrées." });
                return;
            }
        }
        EditorSceneManager.OpenScene(ScenePath);
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name.StartsWith("Zone détaillée"))
                Selection.activeGameObject = root;
        }
        Write(new List<string> { $"ouverte : {SceneManager.GetActiveScene().path}" });
    }

    /// <summary>Enregistre la scène de test si elle est ouverte.</summary>
    public static void Save()
    {
        Scene scene = SceneManager.GetActiveScene();
        var log = new List<string>();
        if (scene.path == ScenePath)
            log.Add($"enregistrée : {EditorSceneManager.SaveScene(scene)}");
        Write(log);
    }

    static void Write(List<string> log)
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "zone_test_scene.txt"), log);
    }
}
