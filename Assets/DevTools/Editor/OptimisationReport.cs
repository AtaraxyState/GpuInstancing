using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Nyx.GpuInstancing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Outil de développement : mesures, images et graphiques (SVG) du document Packages/com.nyx.gpuinstancing/Documentation~/Optimisations.md.
///  - MeasureEditor : scène de démo OUVERTE, vue de sa caméra principale en 1280 x 720 : temps GPU image par image avant /
///    après les optimisations des sargasses (Logs/NyxGpuInstancingTest/doc/editor_series.csv) et images de la vue ;
///    vues de dessus des réglages de la zone naturelle. Réglages restaurés : la scène n'est pas marquée modifiée.
///  - Charts : graphiques SVG à partir des mesures (journaux du dossier Logs/NyxGpuInstancingTest et CSV).
/// invoke.request « OptimisationReport, Assembly-CSharp-Editor|MeasureEditor » puis « |Charts ».
/// </summary>
static class OptimisationReport
{
    const int SeriesFrames = 300;

    static string Logs => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
    static string DataFolder => Directory.CreateDirectory(Path.Combine(Logs, "doc")).FullName;
    static string Images => Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages",
        "com.nyx.gpuinstancing", "Documentation~", "Images"))).FullName;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------------------------------
    // Mesures dans l'éditeur
    // ------------------------------------------------------------------------------------------

    public static void MeasureEditor()
    {
        var log = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        try
        {
            MeasureDemoSeries(log);
            ZoneImages(log);
        }
        catch (Exception exception)
        {
            log.Add("EXCEPTION " + exception);
        }
        File.WriteAllLines(Path.Combine(DataFolder, "measure_editor.txt"), log);
    }

    static void MeasureDemoSeries(List<string> log)
    {
        Camera camera = Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c.CompareTag("MainCamera"));
        MeshSpawner sargassum = Object.FindObjectsOfType<MeshSpawner>().FirstOrDefault(s => s.GetComponent<FloatOnWaves>() != null);
        if (camera == null || sargassum == null)
        {
            log.Add("Caméra principale ou sargasses introuvables dans la scène ouverte.");
            return;
        }
        var target = new RenderTexture(1280, 720, 24);
        RenderTexture previousTarget = camera.targetTexture;
        bool previousDepthless = sargassum.ExcludeFromDepthTexture;
        var savedMaterials = sargassum.Variants.Select(v => v.material).ToList();
        var copies = new List<Material>();
        camera.targetTexture = target;
        var csv = new List<string> { "etat;image;ms" };
        try
        {
            var states = new (string label, bool perPixelSeed, bool depthless, string image)[]
            {
                ("avant : graine lue à chaque pixel, sargasses dans la passe de profondeur", true, false, "demo_avant.png"),
                ("graine interpolée", false, false, null),
                ("graine interpolée + hors de la passe de profondeur", false, true, "demo_apres.png"),
            };
            // Blocs de 50 images alternés entre les états (6 tours) : la dérive des horloges touche les trois de la même façon
            const int block = 50;
            var times = states.Select(_ => new List<double>()).ToArray();
            var perPixel = savedMaterials.Select(m => new Material(m) { shader = Shader.Find("Nyx/Experiments/Sargassum"), enableInstancing = true }).ToList();
            copies.AddRange(perPixel);
            for (int round = 0; round < SeriesFrames / block; round++)
            {
                for (int s = 0; s < states.Length; s++)
                {
                    for (int v = 0; v < sargassum.Variants.Count; v++)
                        sargassum.Variants[v].material = states[s].perPixelSeed ? perPixel[v] : savedMaterials[v];
                    sargassum.ExcludeFromDepthTexture = states[s].depthless;
                    sargassum.RebuildNow();
                    for (int w = 0; w < 5; w++)
                        RenderAndWait(camera);
                    for (int i = 0; i < block; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        RenderAndWait(camera);
                        times[s].Add(watch.Elapsed.TotalMilliseconds);
                    }
                    if (round == 0 && states[s].image != null)
                        SavePng(target, Path.Combine(Images, states[s].image));
                }
            }
            for (int s = 0; s < states.Length; s++)
            {
                for (int i = 0; i < times[s].Count; i++)
                    csv.Add($"{states[s].label};{i};{times[s][i].ToString("F3", Invariant)}");
                double[] sorted = times[s].OrderBy(x => x).ToArray();
                log.Add($"{states[s].label} : médiane {sorted[sorted.Length / 2]:F2} ms, 95 % {sorted[(int)(sorted.Length * 0.95)]:F2} ms");
            }
        }
        finally
        {
            for (int v = 0; v < sargassum.Variants.Count; v++)
                sargassum.Variants[v].material = savedMaterials[v];
            sargassum.ExcludeFromDepthTexture = previousDepthless;
            sargassum.SetDirty();
            foreach (Material copy in copies)
                Object.DestroyImmediate(copy);
            camera.targetTexture = previousTarget;
            Object.DestroyImmediate(target);
        }
        File.WriteAllLines(Path.Combine(DataFolder, "editor_series.csv"), csv);
    }

    /// <summary>Vues de dessus d'une même zone : polygone, contour courbe, bord irrégulier, lisière. Points de contrôle en rouge.</summary>
    static void ZoneImages(List<string> log)
    {
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        var target = new RenderTexture(900, 900, 24);
        var camera = new GameObject("Caméra de dessus").AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.orthographicSize = 40f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.05f, 0.20f, 0.28f);
        camera.targetTexture = target;
        camera.cullingMask = 1 << 31;
        var center = new Vector3(7000f, 0f, 7000f);
        camera.transform.SetPositionAndRotation(center + Vector3.up * 100f, Quaternion.Euler(90f, 0f, 0f));
        camera.nearClipPlane = 1f;
        camera.farClipPlane = 300f;
        var light = new GameObject("Soleil").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
        var go = new GameObject("Zone naturelle");
        go.transform.position = center;
        var spawner = go.AddComponent<MeshSpawner>();
        go.AddComponent<StaticInstances>();
        spawner.SetVariants(NyxDefaultAssets.SargassumVariants());
        var settings = new SerializedObject(spawner);
        settings.FindProperty("densityPer100m2").floatValue = 300f;
        settings.FindProperty("renderLayer").intValue = 31;
        settings.FindProperty("scaleRange").vector2Value = new Vector2(1.2f, 2f);
        settings.ApplyModifiedPropertiesWithoutUndo();
        Vector2[] control =
        {
            new Vector2(-30f, -18f), new Vector2(-6f, -30f), new Vector2(22f, -24f), new Vector2(32f, -2f),
            new Vector2(18f, 12f), new Vector2(26f, 30f), new Vector2(-4f, 26f), new Vector2(-28f, 14f)
        };
        spawner.SetZone(Vector3.zero, control, 2f);
        spawner.ClipToVolume = false;
        var lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
        try
        {
            var cases = new (string file, bool smooth, float noise, float feather)[]
            {
                ("zone_1_polygone.png", false, 0f, 0f),
                ("zone_2_courbe.png", true, 0f, 0f),
                ("zone_3_bord_irregulier.png", true, 3f, 0f),
                ("zone_4_lisiere.png", true, 3f, 5f),
            };
            foreach (var c in cases)
            {
                spawner.ZoneSmooth = c.smooth;
                spawner.ZoneEdgeNoise = c.noise;
                spawner.ZoneEdgeNoiseScale = 12f;
                spawner.ZoneEdgeFeather = c.feather;
                spawner.RebuildNow();
                camera.Render();
                camera.Render();
                DrawControlPolygon(camera, target, lineMaterial, center, control);
                SavePng(target, Path.Combine(Images, c.file));
                log.Add($"{c.file} : {spawner.InstanceCount} instances");
            }
        }
        finally
        {
            Object.DestroyImmediate(lineMaterial);
            camera.targetTexture = null;
            Object.DestroyImmediate(target);
            if (previous.IsValid())
                SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    static void DrawControlPolygon(Camera camera, RenderTexture target, Material material, Vector3 center, Vector2[] control)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        GL.PushMatrix();
        GL.LoadOrtho();
        material.SetPass(0);
        GL.Begin(GL.LINES);
        GL.Color(new Color(1f, 0.25f, 0.2f));
        for (int i = 0; i < control.Length; i++)
        {
            Vector3 a = camera.WorldToViewportPoint(center + new Vector3(control[i].x, 1f, control[i].y));
            Vector3 b = camera.WorldToViewportPoint(center + new Vector3(control[(i + 1) % control.Length].x, 1f, control[(i + 1) % control.Length].y));
            GL.Vertex3(a.x, a.y, 0f);
            GL.Vertex3(b.x, b.y, 0f);
        }
        GL.End();
        GL.PopMatrix();
        RenderTexture.active = previous;
    }

    /// <summary>Seulement les vues de dessus de la zone naturelle.</summary>
    public static void ZoneImagesOnly()
    {
        var log = new List<string>();
        try
        {
            ZoneImages(log);
        }
        catch (Exception exception)
        {
            log.Add("EXCEPTION " + exception);
        }
        File.WriteAllLines(Path.Combine(DataFolder, "zone_images.txt"), log);
    }

    static void RenderAndWait(Camera camera)
    {
        camera.Render();
        AsyncGPUReadback.Request(camera.targetTexture, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
    }

    static void SavePng(RenderTexture target, string path)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        RenderTexture.active = previous;
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
    }

    // ------------------------------------------------------------------------------------------
    // Graphiques
    // ------------------------------------------------------------------------------------------

    const string Before = "#9E9E9E";
    const string After = "#1E88E5";
    const string Middle = "#FB8C00";

    public static void Charts()
    {
        var log = new List<string>();
        try
        {
            // 1. Construction des instances sur GPU (perf_cpu_build.txt du 2026-11-05 11:38 / perf_gpu_build_staged.txt 17:30)
            string[] cases = { "1 000 000 rochers", "1 000 000 touffes", "640 000 touffes (démo)", "1 000 000 bulles", "liste figée de 1 000 000" };
            Write("rebuild_thread_principal.svg", Bars("Reconstruction : temps du thread principal (ms)", "ms", cases, new[]
            {
                ("Jobs Burst + envoi", Before, new[] { 44.76f, 41.55f, 29.48f, 69.65f, 307.59f }),
                ("Construction sur GPU", After, new[] { 1.27f, 1.77f, 1.01f, 1.10f, 22.31f }),
            }));
            Write("rebuild_image_edition.svg", Bars("Image d'édition : reconstruction + premier rendu + fin du GPU (ms)", "ms", cases, new[]
            {
                ("Jobs Burst + envoi", Before, new[] { 61.76f, 68.52f, 58.61f, 86.48f, 264.69f }),
                ("Construction sur GPU", After, new[] { 6.99f, 6.25f, 26.75f, 8.50f, 28.32f }),
            }));

            // 2. Envoi des instances écrites en C# (readback.txt, staged_copy.txt du 2026-11-05)
            Write("envoi_csharp.svg", Bars("Instances écrites en C#, 1 048 576 rochers (ms)", "ms",
                new[] { "reconstruction complète", "copie de 64 Mo jusqu'à la fin du GPU" }, new[]
                {
                    ("Buffer intermédiaire (SubUpdates)", Before, new[] { 146.04f, 77.9f }),
                    ("SetData", After, new[] { 85.97f, 49.7f }),
                }));

            // 3. Pics de l'éditeur (capture Profiler en Play mode) et build de développement
            var editorFrames = ReadColumn(Path.Combine(DataFolder, "profiler_spikes_frames.csv"), 1);
            var buildFrames = ReadSeries(Path.Combine(Logs, "build", "build_frames.csv"));
            if (editorFrames.Count > 0 && buildFrames.Count > 0)
            {
                Write("pics_editeur_build.svg", Lines("Temps par image de la démo (ms) : Play mode dans l'éditeur, puis build de développement", "ms",
                    new[] { ("Éditeur (Play mode), Profiler ouvert", Middle, editorFrames.Take(editorFrames.Count - 1).ToArray()), ("Build de développement", After, buildFrames.First().Value.Take(300).ToArray()) },
                    60f, 16.7f));
            }

            // 4. GPU des sargasses image par image (editor_series.csv)
            Dictionary<string, List<float>> series = ReadSeries(Path.Combine(DataFolder, "editor_series.csv"));
            if (series.Count == 3)
            {
                string[] names = series.Keys.ToArray();
                Write("gpu_sargasses_series.svg", Lines("Démo, vue de la caméra principale : temps GPU par image (ms, éditeur)", "ms", new[]
                {
                    ("Avant", Before, series[names[0]].ToArray()),
                    ("Graine interpolée", Middle, series[names[1]].ToArray()),
                    ("+ hors de la passe de profondeur", After, series[names[2]].ToArray()),
                }, 16f, 0f));
            }

            // 5. Répartition du temps GPU (gpu_breakdown_avant.txt 2026-11-06 10:32 / gpu_breakdown.txt 10:56)
            Write("gpu_repartition.svg", Stacked("Démo : répartition du temps GPU d'une image (ms, éditeur)",
                new[] { "Avant", "Après" }, new[]
                {
                    ("reste de la scène", "#BDBDBD", new[] { 1.06f, 1.11f }),
                    ("culling (930 000 touffes)", "#8E24AA", new[] { 0.56f, 0.62f }),
                    ("sommets", "#FB8C00", new[] { 4.11f, 2.43f }),
                    ("pixels", "#1E88E5", new[] { 6.32f, 4.51f }),
                }));

            // 6. Build de développement : passe de profondeur (build_frames.csv) et résumé avant tout (build_perf_avant.txt)
            if (buildFrames.Count >= 2)
            {
                string[] phases = buildFrames.Keys.ToArray();
                Write("build_profondeur.svg", Lines("Build de développement, 1280 x 720 : temps par image (ms)", "ms", new[]
                {
                    (phases[1], Before, buildFrames[phases[1]].Take(600).ToArray()),
                    (phases[0], After, buildFrames[phases[0]].Take(600).ToArray()),
                }, 10f, 0f));
            }

            // 7. Coût du test de zone (zone_cost.csv, 2026-11-06 11:31)
            var cost = File.ReadAllLines(Path.Combine(Logs, "zone_cost.csv")).Skip(1).Select(l => l.Split(';'))
                .Where(c => c[3] == "1000000").ToList();
            Write("zone_cout_test.svg", Bars("Test « dans la zone » pour 1 000 000 de points (ms GPU, à chaque image avec Clip To Volume)", "ms",
                cost.Select(c => c[0] == "liste" ? $"liste de {c[1]} sommets" : $"64 sommets + bord naturel{(c[2] != "0" ? $" ({c[2]} octaves)" : " (sans bruit)")}").ToArray(),
                new[] { ("Coût", After, cost.Select(c => float.Parse(c[4], Invariant)).ToArray()) }));
            log.Add("ok");
        }
        catch (Exception exception)
        {
            log.Add("EXCEPTION " + exception);
        }
        File.WriteAllLines(Path.Combine(DataFolder, "charts.txt"), log);
    }

    static void Write(string file, string svg) => File.WriteAllText(Path.Combine(Images, file), svg, new UTF8Encoding(false));

    static List<float> ReadColumn(string path, int column)
    {
        if (!File.Exists(path))
            return new List<float>();
        return File.ReadAllLines(path).Skip(1).Select(l => float.Parse(l.Split(';')[column], Invariant)).ToList();
    }

    /// <summary>CSV « série;index;ms » : une liste de valeurs par série, dans l'ordre du fichier.</summary>
    static Dictionary<string, List<float>> ReadSeries(string path)
    {
        var result = new Dictionary<string, List<float>>();
        if (!File.Exists(path))
            return result;
        foreach (string line in File.ReadAllLines(path).Skip(1))
        {
            string[] cells = line.Split(';');
            if (!result.TryGetValue(cells[0], out List<float> values))
                result.Add(cells[0], values = new List<float>());
            values.Add(float.Parse(cells[2], Invariant));
        }
        return result;
    }

    static string F(float value) => value.ToString(value < 10f ? "0.##" : "0.#", Invariant);

    static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Barres horizontales groupées, valeur écrite au bout de chaque barre.</summary>
    static string Bars(string title, string unit, string[] groups, (string name, string color, float[] values)[] series)
    {
        const int width = 820, left = 230, right = 70, bar = 18, gap = 4, groupGap = 16, top = 64;
        float max = series.Max(s => s.values.Max());
        int height = top + groups.Length * (series.Length * (bar + gap) + groupGap) + 20;
        var svg = Header(width, height, title);
        Legend(svg, series.Select(s => (s.name, s.color)).ToArray(), 40);
        float scale = (width - left - right) / max;
        int y = top;
        for (int g = 0; g < groups.Length; g++)
        {
            svg.Append($"<text x='{left - 10}' y='{y + series.Length * (bar + gap) / 2 + 4}' text-anchor='end'>{Escape(groups[g])}</text>");
            foreach (var s in series)
            {
                float w = Mathf.Max(1f, s.values[g] * scale);
                svg.Append($"<rect x='{left}' y='{y}' width='{w.ToString("F1", Invariant)}' height='{bar}' fill='{s.color}'/>");
                svg.Append($"<text x='{(left + w + 6).ToString("F1", Invariant)}' y='{y + bar - 5}'>{F(s.values[g])} {unit}</text>");
                y += bar + gap;
            }
            y += groupGap;
        }
        return svg.Append("</svg>").ToString();
    }

    /// <summary>Barres empilées verticales (une par état), total écrit au-dessus.</summary>
    static string Stacked(string title, string[] bars, (string name, string color, float[] values)[] parts)
    {
        const int width = 620, height = 420, bottom = 370, top = 80, barWidth = 120;
        float max = Enumerable.Range(0, bars.Length).Max(b => parts.Sum(p => p.values[b])) * 1.1f;
        float scale = (bottom - top) / max;
        var svg = Header(width, height, title);
        Legend(svg, parts.Select(p => (p.name, p.color)).ToArray(), 40);
        for (int b = 0; b < bars.Length; b++)
        {
            int x = 140 + b * 220;
            float y = bottom;
            foreach (var p in parts)
            {
                float h = p.values[b] * scale;
                y -= h;
                svg.Append($"<rect x='{x}' y='{y.ToString("F1", Invariant)}' width='{barWidth}' height='{h.ToString("F1", Invariant)}' fill='{p.color}'/>");
                if (h > 14)
                    svg.Append($"<text x='{x + barWidth / 2}' y='{(y + h / 2 + 4).ToString("F1", Invariant)}' text-anchor='middle' fill='white'>{F(p.values[b])}</text>");
            }
            svg.Append($"<text x='{x + barWidth / 2}' y='{(y - 8).ToString("F1", Invariant)}' text-anchor='middle' font-weight='bold'>{F(parts.Sum(p => p.values[b]))} ms</text>");
            svg.Append($"<text x='{x + barWidth / 2}' y='{bottom + 20}' text-anchor='middle'>{Escape(bars[b])}</text>");
        }
        return svg.Append("</svg>").ToString();
    }

    /// <summary>Courbes (une valeur par image), axe des ordonnées de 0 à yMax, ligne de référence facultative (16,7 ms).</summary>
    static string Lines(string title, string unit, (string name, string color, float[] values)[] series, float yMax, float reference)
    {
        const int width = 820, height = 360, left = 60, right = 20, top = 70, bottom = 320;
        float xMax = series.Max(s => s.values.Length) - 1;
        var svg = Header(width, height, title);
        Legend(svg, series.Select(s => (s.name, s.color)).ToArray(), 40);
        for (int i = 0; i <= 4; i++)
        {
            float value = yMax * i / 4f;
            float y = bottom - (bottom - top) * i / 4f;
            svg.Append($"<line x1='{left}' x2='{width - right}' y1='{y.ToString("F1", Invariant)}' y2='{y.ToString("F1", Invariant)}' stroke='#E0E0E0'/>");
            svg.Append($"<text x='{left - 6}' y='{(y + 4).ToString("F1", Invariant)}' text-anchor='end'>{F(value)}</text>");
        }
        if (reference > 0f && reference < yMax)
        {
            float y = bottom - (bottom - top) * reference / yMax;
            svg.Append($"<line x1='{left}' x2='{width - right}' y1='{y.ToString("F1", Invariant)}' y2='{y.ToString("F1", Invariant)}' stroke='#E53935' stroke-dasharray='6 4'/>");
            svg.Append($"<text x='{width - right}' y='{(y - 4).ToString("F1", Invariant)}' text-anchor='end' fill='#E53935'>{F(reference)} {unit} (60 images/s)</text>");
        }
        foreach (var s in series)
        {
            var points = new StringBuilder();
            for (int i = 0; i < s.values.Length; i++)
            {
                float x = left + (width - left - right) * i / Mathf.Max(1f, xMax);
                float y = bottom - (bottom - top) * Mathf.Min(s.values[i], yMax) / yMax;
                points.Append(x.ToString("F1", Invariant)).Append(',').Append(y.ToString("F1", Invariant)).Append(' ');
            }
            svg.Append($"<polyline fill='none' stroke='{s.color}' stroke-width='1.5' points='{points}'/>");
        }
        svg.Append($"<text x='{(left + width - right) / 2}' y='{bottom + 30}' text-anchor='middle'>images</text>");
        return svg.Append("</svg>").ToString();
    }

    static StringBuilder Header(int width, int height, string title)
    {
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' viewBox='0 0 {width} {height}' " +
                   "font-family='Segoe UI, Helvetica, Arial, sans-serif' font-size='13' fill='#212121'>");
        svg.Append($"<rect width='{width}' height='{height}' fill='white'/>");
        svg.Append($"<text x='20' y='26' font-size='15' font-weight='bold'>{Escape(title)}</text>");
        return svg;
    }

    static void Legend(StringBuilder svg, (string name, string color)[] items, int y)
    {
        int x = 20;
        foreach (var item in items)
        {
            svg.Append($"<rect x='{x}' y='{y}' width='12' height='12' fill='{item.color}'/>");
            svg.Append($"<text x='{x + 18}' y='{y + 11}'>{Escape(item.name)}</text>");
            x += 30 + item.name.Length * 7;
        }
    }
}
