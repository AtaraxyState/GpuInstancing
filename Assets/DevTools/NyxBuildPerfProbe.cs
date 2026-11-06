using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Nyx.GpuInstancing;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Outil de développement (hors package) : mesure d'une build de développement, active seulement si le joueur est lancé avec
/// « -nyxPerfLog &lt;dossier&gt; ». VSync coupée. 4 s de chauffe, puis 20 s de temps par image sans Profiler.
/// Avec « -nyxCompareDepth » : bascule ensuite Exclude From Depth Texture sur les sargasses (Float On Waves), 3 s de chauffe,
/// 20 s de plus. Puis 5 s de capture du Profiler dans &lt;dossier&gt;/build_capture.raw. Résumé dans build_perf.txt, temps de
/// chaque image dans build_frames.csv (phase;image;ms;thread principal ms), et le joueur se ferme. Sans l'argument : rien.
/// </summary>
public sealed class NyxBuildPerfProbe : MonoBehaviour
{
    const float WarmUpSeconds = 4f;
    const float MeasureSeconds = 20f;
    const float CaptureSeconds = 5f;

    string _folder;
    bool _compareDepth;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-nyxPerfLog");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var go = new GameObject("NyxBuildPerfProbe");
        DontDestroyOnLoad(go);
        var probe = go.AddComponent<NyxBuildPerfProbe>();
        probe._folder = args[index + 1];
        probe._compareDepth = Array.IndexOf(args, "-nyxCompareDepth") >= 0;
    }

    IEnumerator Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Directory.CreateDirectory(_folder);
        var lines = new List<string>
        {
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - build de développement {Application.unityVersion}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), " +
            $"{Screen.width} x {Screen.height}, qualité {QualitySettings.names[QualitySettings.GetQualityLevel()]}, VSync coupée"
        };
        var csv = new List<string> { "phase;image;ms;thread principal ms" };
        MeshSpawner sargassum = FindObjectsOfType<MeshSpawner>().FirstOrDefault(s => s.GetComponent<FloatOnWaves>() != null);

        yield return new WaitForSecondsRealtime(WarmUpSeconds);

        using (ProfilerRecorder mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1))
        {
            var frames = new List<float>();
            var main = new List<float>();
            string phase = Phase(sargassum);
            yield return Record(MeasureSeconds, frames, main, mainThread);
            Summarize(lines, $"{MeasureSeconds:F0} s sans Profiler, {phase}", frames, main);
            Append(csv, phase, frames, main);

            if (_compareDepth && sargassum != null)
            {
                sargassum.ExcludeFromDepthTexture = !sargassum.ExcludeFromDepthTexture;
                yield return new WaitForSecondsRealtime(3f);
                frames.Clear();
                main.Clear();
                phase = Phase(sargassum);
                yield return Record(MeasureSeconds, frames, main, mainThread);
                Summarize(lines, $"{MeasureSeconds:F0} s sans Profiler, {phase}", frames, main);
                Append(csv, phase, frames, main);
                sargassum.ExcludeFromDepthTexture = !sargassum.ExcludeFromDepthTexture;
                yield return new WaitForSecondsRealtime(2f);
            }

            string raw = Path.Combine(_folder, "build_capture");
            Profiler.logFile = raw;   // .raw ajouté par Unity
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            frames.Clear();
            main.Clear();
            yield return Record(CaptureSeconds, frames, main, mainThread);
            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            Profiler.logFile = "";
            Summarize(lines, $"{CaptureSeconds:F0} s avec le Profiler (build_capture.raw)", frames, main);
        }

        File.WriteAllLines(Path.Combine(_folder, "build_perf.txt"), lines);
        File.WriteAllLines(Path.Combine(_folder, "build_frames.csv"), csv);
        Application.Quit();
    }

    static string Phase(MeshSpawner sargassum) =>
        sargassum == null ? "scène" : sargassum.ExcludeFromDepthTexture ? "sargasses hors de la passe de profondeur" : "sargasses dans la passe de profondeur";

    static void Append(List<string> csv, string phase, List<float> frames, List<float> main)
    {
        for (int i = 0; i < frames.Count; i++)
            csv.Add($"{phase};{i};{frames[i].ToString("F3", CultureInfo.InvariantCulture)};{main[i].ToString("F3", CultureInfo.InvariantCulture)}");
    }

    static IEnumerator Record(float seconds, List<float> frames, List<float> main, ProfilerRecorder mainThread)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end)
        {
            yield return null;
            frames.Add(Time.unscaledDeltaTime * 1000f);
            main.Add(mainThread.Valid ? mainThread.LastValue * 1e-6f : 0f);
        }
    }

    static void Summarize(List<string> lines, string label, List<float> frames, List<float> main)
    {
        if (frames.Count == 0)
            return;
        float[] sorted = frames.OrderBy(x => x).ToArray();
        float median = sorted[sorted.Length / 2];
        float threshold = Mathf.Max(2f * median, 16.7f);
        lines.Add("");
        lines.Add($"{label} : {frames.Count} frames ({frames.Count / frames.Sum() * 1000f:F0} images/s)");
        lines.Add($"  temps par frame : médiane {median:F2} ms, 95 % {Percentile(sorted, 0.95f):F2}, 99 % {Percentile(sorted, 0.99f):F2}, " +
                  $"max {sorted[sorted.Length - 1]:F2} ms");
        float[] mainSorted = main.OrderBy(x => x).ToArray();
        lines.Add($"  thread principal (marqueur Main Thread) : médiane {mainSorted[mainSorted.Length / 2]:F2} ms, " +
                  $"99 % {Percentile(mainSorted, 0.99f):F2}, max {mainSorted[mainSorted.Length - 1]:F2} ms");
        var spikes = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i] > threshold)
                spikes.Add($"#{i} {frames[i]:F1} ms (thread principal {main[i]:F1})");
        }
        lines.Add($"  pics (> {threshold:F1} ms) : {spikes.Count}" + (spikes.Count > 0 ? " : " + string.Join(", ", spikes.Take(40)) : ""));
    }

    static float Percentile(float[] sorted, float p) => sorted[Mathf.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}
