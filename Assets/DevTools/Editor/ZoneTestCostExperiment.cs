using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Nyx.GpuInstancing;
using UnityEngine;

/// <summary>
/// Outil de développement : coût GPU du test de zone pour 1 000 000 de points (culling, à chaque image et par caméra) et
/// 2 000 000 (placement, à la reconstruction). Liste longue (pair-impair, 3 à 4 096 sommets) contre polygone de
/// 64 sommets + bord naturel (distance signée + bruit fractal). Résultat : Logs/NyxGpuInstancingTest/zone_cost.txt et .csv.
/// invoke.request « ZoneTestCostExperiment, Assembly-CSharp-Editor|Run ».
/// </summary>
static class ZoneTestCostExperiment
{
    const int Runs = 15;

    public static void Run()
    {
        var lines = new List<string> { $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} - {SystemInfo.graphicsDeviceName} ; médiane de {Runs}, dispatch + attente du GPU, moins le cas à 3 sommets" };
        var csv = new List<string> { "methode;sommets;octaves;points;ms" };
        var shader = Resources.Load<ComputeShader>("ZoneTestCost");
        int evenOdd = shader.FindKernel("NyxEvenOdd");
        int natural = shader.FindKernel("NyxNatural");
        var result = new ComputeBuffer(2_000_000, sizeof(uint));
        var probe = new uint[1];
        try
        {
            foreach (int count in new[] { 1_000_000, 2_000_000 })
            {
                double baseline = Measure(shader, evenOdd, result, probe, Outline(3), count, Vector4.zero);
                lines.Add("");
                lines.Add($"{count:N0} points (référence à 3 sommets : {baseline:F3} ms)");
                foreach (int vertices in new[] { 16, 64, 256, 1024, 4096 })
                {
                    double ms = Measure(shader, evenOdd, result, probe, Outline(vertices), count, Vector4.zero) - baseline;
                    lines.Add($"  liste longue, pair-impair, {vertices,5} sommets : {ms,8:F3} ms");
                    csv.Add($"liste;{vertices};0;{count};{ms:F4}");
                }
                foreach (int octaves in new[] { 0, 3, 4 })
                {
                    var noise = new Vector4(octaves > 0 ? 4f : 0f, 1f / 12f, octaves, 0f);
                    double ms = Measure(shader, natural, result, probe, Outline(64), count, noise) - baseline;
                    lines.Add($"  64 sommets, distance signée{(octaves > 0 ? $" + bruit {octaves} octaves" : "")} : {ms,8:F3} ms");
                    csv.Add($"naturel;64;{octaves};{count};{ms:F4}");
                }
            }
        }
        finally
        {
            result.Release();
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "zone_cost.txt"), lines);
        File.WriteAllLines(Path.Combine(folder, "zone_cost.csv"), csv);
    }

    /// <summary>Contour organique de 'count' sommets autour de l'origine (rayon ~70 m), sens trigonométrique.</summary>
    static Vector2[] Outline(int count)
    {
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            float t = i * Mathf.PI * 2f / count;
            float r = 70f * (1f + 0.2f * Mathf.Sin(3f * t) + 0.1f * Mathf.Sin(7f * t + 1f) + 0.05f * Mathf.Sin(17f * t + 2f));
            points[i] = new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r;
        }
        return points;
    }

    static double Measure(ComputeShader shader, int kernel, ComputeBuffer result, uint[] probe, Vector2[] outline, int count, Vector4 noise)
    {
        var vertices = new ComputeBuffer(outline.Length, 8);
        vertices.SetData(outline);
        shader.SetBuffer(kernel, "_Vertices", vertices);
        shader.SetBuffer(kernel, "_Result", result);
        shader.SetInt("_VertexCount", outline.Length);
        shader.SetInt("_Count", count);
        shader.SetVector("_Bounds", new Vector4(-90f, -90f, 90f, 90f));
        shader.SetVector("_Noise", noise);
        var times = new double[Runs];
        try
        {
            for (int run = -2; run < Runs; run++)
            {
                result.GetData(probe, 0, 0, 1);
                var watch = Stopwatch.StartNew();
                MeshSpawner.DispatchInstances(shader, kernel, count);
                result.GetData(probe, 0, count - 1, 1);
                if (run >= 0)
                    times[run] = watch.Elapsed.TotalMilliseconds;
            }
        }
        finally
        {
            vertices.Release();
        }
        System.Array.Sort(times);
        return times[Runs / 2];
    }
}
