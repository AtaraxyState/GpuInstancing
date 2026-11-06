using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

/// <summary>
/// Outil de développement : compilation de tous les compute shaders du package (et de l'exemple Bobbing) et des shaders de
/// rendu, sans ouvrir de scène. Erreurs et avertissements, noyaux trouvés. Résultat : Logs/NyxGpuInstancingTest/shader_check.txt.
/// invoke.request « ShaderCompileCheck, Assembly-CSharp-Editor|Run ».
/// </summary>
static class ShaderCompileCheck
{
    static readonly (string resource, string[] kernels)[] Computes =
    {
        ("NyxGpuInstancing/StaticInstances", new[] { "NyxCull", "NyxWriteArgs" }),
        ("NyxGpuInstancing/FloatOnWaves", new[] { "NyxCull", "NyxWriteArgs" }),
        ("NyxGpuInstancing/BubbleColumns", new[] { "NyxCull", "NyxWriteArgs" }),
        ("NyxGpuInstancing/BubbleColumnsBuild", new[] { "NyxBuildBubbles" }),
        ("NyxGpuInstancing/InstancePlacement", new[] { "NyxCountPlacements", "NyxScanGroups", "NyxWriteInstances", "NyxWritePlacements" }),
        ("NyxGpuInstancing/InstanceVariants", new[] { "NyxCountVariants" }),
        ("Bobbing", new[] { "NyxCull", "NyxWriteArgs" }),
    };

    static readonly string[] RenderShaders =
    {
        "Nyx/GPU Instancing/Sargassum", "Nyx/GPU Instancing/Bubble", "Nyx/GPU Instancing/Lit", "Nyx/GPU Instancing/Demo Water"
    };

    public static void Run()
    {
        var lines = new List<string>();
        int errors = 0;
        foreach ((string resource, string[] kernels) in Computes)
        {
            var shader = Resources.Load<ComputeShader>(resource);
            if (shader == null)
            {
                lines.Add($"ERREUR {resource} : introuvable");
                errors++;
                continue;
            }
            ShaderMessage[] messages = ShaderUtil.GetComputeShaderMessages(shader);
            int shaderErrors = messages.Count(m => m.severity == ShaderCompilerMessageSeverity.Error);
            string[] missing = kernels.Where(k => !shader.HasKernel(k)).ToArray();
            errors += shaderErrors + missing.Length;
            lines.Add($"{(shaderErrors + missing.Length == 0 ? "ok" : "ERREUR")} {resource} : noyaux {string.Join(", ", kernels)}" +
                      (missing.Length > 0 ? $" ; manquants : {string.Join(", ", missing)}" : ""));
            foreach (ShaderMessage message in messages)
                lines.Add($"    {message.severity} {message.message} ({message.file}:{message.line}, {message.platform})");
        }
        foreach (string name in RenderShaders)
        {
            Shader shader = Shader.Find(name);
            bool error = shader == null || ShaderUtil.ShaderHasError(shader);
            errors += error ? 1 : 0;
            lines.Add($"{(error ? "ERREUR" : "ok")} {name}");
        }
        lines.Insert(0, errors == 0 ? "TOUT COMPILE" : $"{errors} ERREUR(S)");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "shader_check.txt"), lines);
    }
}
