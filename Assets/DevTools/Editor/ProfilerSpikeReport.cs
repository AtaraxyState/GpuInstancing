using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Outil de développement : analyse des pics dans les frames que le Profiler a en mémoire (celles de la fenêtre Profiler).
///  - Run              : frames actuelles. Temps par frame (thread principal), pics (> 2 × médiane et > 16,7 ms), arbre des
///                       marqueurs de chaque pic, marqueurs qui coûtent plus dans les pics qu'en temps normal (tous threads).
///  - CapturePlayMode  : vide le Profiler, enregistre ~12 s de Play mode, sort du Play mode puis lance Run.
/// Résultat : Logs/NyxGpuInstancingTest/profiler_spikes.txt. Lancement : invoke.request « ProfilerSpikeReport, Assembly-CSharp-Editor|Run ».
/// </summary>
[InitializeOnLoad]
static class ProfilerSpikeReport
{
    const string CaptureKey = "Nyx.ProfilerSpikeReport.Capture";        // 1 : Play demandé, 2 : en cours
    const string CaptureStartKey = "Nyx.ProfilerSpikeReport.CaptureStart";
    const double CaptureSeconds = 12.0;
    const int MaxSpikesDumped = 6;
    const float TreeThresholdMs = 0.25f;

    static ProfilerSpikeReport() => EditorApplication.update += CaptureTick;

    public static void CapturePlayMode()
    {
        ProfilerDriver.ClearAllFrames();
        ProfilerDriver.profileEditor = false;
        ProfilerDriver.enabled = true;
        SessionState.SetInt(CaptureKey, 1);
        EditorApplication.isPlaying = true;
    }

    /// <summary>Comme CapturePlayMode, mais en profilant l'éditeur entier : détaille ce que contient EditorLoop.</summary>
    public static void CaptureEditorPlayMode()
    {
        CapturePlayMode();
        ProfilerDriver.profileEditor = true;
    }

    static void CaptureTick()
    {
        int state = SessionState.GetInt(CaptureKey, 0);
        if (state == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            if (state == 1)
            {
                ProfilerDriver.enabled = true;
                SessionState.SetInt(CaptureKey, 2);
                SessionState.SetFloat(CaptureStartKey, (float)EditorApplication.timeSinceStartup);
            }
            else if (EditorApplication.timeSinceStartup - SessionState.GetFloat(CaptureStartKey, 0f) > CaptureSeconds)
            {
                ProfilerDriver.enabled = false;
                EditorApplication.isPlaying = false;
            }
            return;
        }
        if (state == 2 && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            SessionState.SetInt(CaptureKey, 0);
            ProfilerDriver.profileEditor = false;
            Run();
        }
    }

    public static void Run() => Run("profiler_spikes.txt", "");

    /// <summary>Charge la capture de la build de développement (NyxBuildPerfProbe : build/build_capture.raw) dans le Profiler, puis l'analyse.</summary>
    public static void RunBuildCapture()
    {
        string raw = Path.Combine(Folder, "build", "build_capture.raw");
        bool loaded = File.Exists(raw) && ProfilerDriver.LoadProfile(raw, false);
        Run("profiler_spikes_build.txt", $"capture {raw} : {(loaded ? "chargée" : "INTROUVABLE OU ILLISIBLE")} ; ");
    }

    static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));

    static string s_framesCsv = "profiler_spikes_frames.csv";   // temps par image (doc/), pour les graphiques

    static void Run(string file, string prefix)
    {
        s_framesCsv = Path.GetFileNameWithoutExtension(file) + "_frames.csv";
        var lines = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {prefix}frames du Profiler {ProfilerDriver.firstFrameIndex} à {ProfilerDriver.lastFrameIndex}" };
        try
        {
            Analyze(lines);
        }
        catch (Exception exception)
        {
            lines.Add("EXCEPTION " + exception);
        }
        File.WriteAllLines(Path.Combine(Folder, file), lines);
    }

    static void Analyze(List<string> lines)
    {
        int first = ProfilerDriver.firstFrameIndex;
        int last = ProfilerDriver.lastFrameIndex;
        if (first < 0 || last < first)
        {
            lines.Add("Aucune frame dans le Profiler.");
            return;
        }

        var frames = new List<(int index, float ms, float gpuMs, float gcKb)>();
        for (int f = first; f <= last; f++)
        {
            using HierarchyFrameDataView view = MainView(f);
            if (view == null || !view.valid)
                continue;
            int root = view.GetRootItemID();
            frames.Add((f, view.frameTimeMs, view.frameGpuTimeMs, view.GetItemColumnDataAsFloat(root, HierarchyFrameDataView.columnGcMemory) / 1024f));
        }
        Directory.CreateDirectory(Path.Combine(Folder, "doc"));
        File.WriteAllLines(Path.Combine(Folder, "doc", s_framesCsv), new[] { "image;ms" }.Concat(frames.Select(x => $"{x.index};{x.ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}")));
        if (frames.Count == 0)
        {
            lines.Add("Aucune frame lisible.");
            return;
        }

        float[] sorted = frames.Select(x => x.ms).OrderBy(x => x).ToArray();
        float median = sorted[sorted.Length / 2];
        float p95 = sorted[(int)(sorted.Length * 0.95f)];
        float threshold = Mathf.Max(2f * median, 16.7f);
        var spikes = frames.Where(x => x.ms > threshold).ToList();
        var normal = frames.Where(x => x.ms <= median * 1.2f).ToList();
        lines.Add($"{frames.Count} frames : médiane {median:F2} ms, 95 % {p95:F2} ms, max {sorted[sorted.Length - 1]:F2} ms ; " +
                  $"pics (> {threshold:F1} ms) : {spikes.Count}");
        lines.Add("GC alloué par frame (thread principal) : médiane " +
                  $"{frames.Select(x => x.gcKb).OrderBy(x => x).ElementAt(frames.Count / 2):F1} Ko, max {frames.Max(x => x.gcKb):F1} Ko");
        if (spikes.Count == 0)
        {
            var typical = frames.OrderBy(x => x.ms).ElementAt(frames.Count / 2);
            lines.Add("");
            lines.Add($"Frame médiane #{typical.index} : {typical.ms:F2} ms (thread principal)");
            using HierarchyFrameDataView typicalView = MainView(typical.index);
            DumpTree(typicalView, typicalView.GetRootItemID(), 1, lines);
            return;
        }
        lines.Add("Pics : " + string.Join(", ", spikes.Select(x => $"#{x.index} {x.ms:F1} ms (GPU {x.gpuMs:F1}, GC {x.gcKb:F0} Ko)")));
        lines.Add("Écart entre pics (frames) : " + string.Join(", ", spikes.Zip(spikes.Skip(1), (a, b) => b.index - a.index)));

        // Marqueurs qui coûtent plus dans les pics qu'en temps normal, par thread (temps propre)
        lines.Add("");
        lines.Add("Temps propre moyen en plus dans un pic (par thread et marqueur, > 0,5 ms) :");
        var normalSample = normal.Count > 30 ? normal.Where((x, i) => i % (normal.Count / 30) == 0).ToList() : normal;
        var excess = new List<(string thread, string marker, float spike, float baseline)>();
        foreach ((string threadName, int threadIndex) in Threads(spikes[0].index))
        {
            Dictionary<string, float> spikeSelf = AverageSelf(spikes.Select(x => x.index), threadIndex);
            Dictionary<string, float> normalSelf = AverageSelf(normalSample.Select(x => x.index), threadIndex);
            foreach (KeyValuePair<string, float> pair in spikeSelf)
            {
                normalSelf.TryGetValue(pair.Key, out float baseline);
                if (pair.Value - baseline > 0.5f && pair.Key.Length > 0 && !pair.Key.Contains("Idle") && !pair.Key.Contains("WaitForSignal"))
                    excess.Add((threadName, pair.Key, pair.Value, baseline));
            }
        }
        foreach (var item in excess.OrderByDescending(x => x.spike - x.baseline).Take(30))
            lines.Add($"  [{item.thread}] {item.marker} : {item.spike:F2} ms dans un pic, {item.baseline:F2} ms en temps normal");

        // Arbre du thread principal pour quelques pics, et une frame normale pour comparer
        foreach (var spike in spikes.OrderByDescending(x => x.ms).Take(MaxSpikesDumped))
        {
            lines.Add("");
            lines.Add($"Pic #{spike.index} : {spike.ms:F2} ms (thread principal)");
            using HierarchyFrameDataView view = MainView(spike.index);
            DumpTree(view, view.GetRootItemID(), 1, lines);
        }
        if (normal.Count > 0)
        {
            var reference = normal[normal.Count / 2];
            lines.Add("");
            lines.Add($"Frame normale #{reference.index} : {reference.ms:F2} ms (thread principal)");
            using HierarchyFrameDataView view = MainView(reference.index);
            DumpTree(view, view.GetRootItemID(), 1, lines);
        }
    }

    static HierarchyFrameDataView MainView(int frame) => View(frame, 0);

    static HierarchyFrameDataView View(int frame, int thread) =>
        ProfilerDriver.GetHierarchyFrameDataView(frame, thread, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
            HierarchyFrameDataView.columnTotalTime, false);

    static IEnumerable<(string name, int index)> Threads(int frame)
    {
        var result = new List<(string, int)>();
        for (int t = 0; t < 128; t++)
        {
            using RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, t);
            if (raw == null || !raw.valid)
                break;
            result.Add(($"{raw.threadGroupName}/{raw.threadName}".Trim('/'), t));
        }
        return result;
    }

    static Dictionary<string, float> AverageSelf(IEnumerable<int> frameIndices, int thread)
    {
        var sum = new Dictionary<string, float>();
        int count = 0;
        var stack = new List<int>();
        var children = new List<int>();
        foreach (int frame in frameIndices)
        {
            using HierarchyFrameDataView view = View(frame, thread);
            if (view == null || !view.valid)
                continue;
            count++;
            stack.Clear();
            stack.Add(view.GetRootItemID());
            while (stack.Count > 0)
            {
                int id = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                string name = view.GetItemName(id);
                float self = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                sum.TryGetValue(name, out float total);
                sum[name] = total + self;
                view.GetItemChildren(id, children);
                stack.AddRange(children);
            }
        }
        if (count > 0)
        {
            foreach (string key in sum.Keys.ToList())
                sum[key] /= count;
        }
        return sum;
    }

    static void DumpTree(HierarchyFrameDataView view, int id, int depth, List<string> lines)
    {
        var children = new List<int>();
        view.GetItemChildren(id, children);
        foreach (int child in children)
        {
            float total = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnTotalTime);
            if (total < TreeThresholdMs || depth > 12)
                continue;
            float self = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnSelfTime);
            float calls = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnCalls);
            float gc = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnGcMemory);
            var text = new StringBuilder();
            text.Append(' ', depth * 2).Append(view.GetItemName(child)).Append(" : ").Append(total.ToString("F2")).Append(" ms (propre ")
                .Append(self.ToString("F2")).Append(", appels ").Append(calls.ToString("F0"));
            if (gc > 0f)
                text.Append(", GC ").Append((gc / 1024f).ToString("F1")).Append(" Ko");
            text.Append(')');
            lines.Add(text.ToString());
            DumpTree(view, child, depth + 1, lines);
        }
    }
}
