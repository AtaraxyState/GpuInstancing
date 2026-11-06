using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Outil de développement : build de développement de la scène de démo (telle qu'enregistrée) dans Builds/NyxDevDemo/, pour
/// la plateforme active si c'est Windows (pas de changement de plateforme). Les réglages de build du projet ne sont pas modifiés.
/// Résultat : Logs/NyxGpuInstancingTest/build.txt. Lancement : invoke.request « NyxDevBuild, Assembly-CSharp-Editor|BuildDemo ».
/// Mesure ensuite : Builds/NyxDevDemo/NyxDevDemo.exe -nyxPerfLog &lt;dossier&gt; (voir NyxBuildPerfProbe).
/// </summary>
static class NyxDevBuild
{
    const string DemoScene = "Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity";
    const string Output = "Builds/NyxDevDemo/NyxDevDemo.exe";

    public static void BuildDemo()
    {
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        var lines = new List<string> { $"cible active : {target}" };
        if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows)
        {
            lines.Add("ARRÊT : la plateforme active n'est pas Windows ; pas de changement de plateforme automatique.");
            Write(lines);
            return;
        }

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { DemoScene },
            locationPathName = Output,
            target = target,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development
        });
        BuildSummary summary = report.summary;
        lines.Add($"résultat : {summary.result}, durée {summary.totalTime.TotalSeconds:F0} s, taille {summary.totalSize / (1024 * 1024)} Mo, " +
                  $"erreurs {summary.totalErrors}, avertissements {summary.totalWarnings}");
        lines.Add($"sortie : {Path.GetFullPath(Output)}");
        foreach (BuildStep step in report.steps)
        {
            foreach (BuildStepMessage message in step.messages)
            {
                if (message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Warning)
                    lines.Add($"  [{message.type}] {step.name} : {message.content}");
            }
        }
        Write(lines);
    }

    /// <summary>Coche « Exclude From Depth Texture » sur les sargasses (Float On Waves) de la scène de démo ouverte et l'enregistre.</summary>
    public static void ExcludeDemoSargassumFromDepth()
    {
        var lines = new List<string>();
        UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Nyx.GpuInstancing.MeshSpawner spawner = Object.FindObjectsOfType<Nyx.GpuInstancing.MeshSpawner>()
            .FirstOrDefault(s => s.GetComponent<Nyx.GpuInstancing.FloatOnWaves>() != null && s.gameObject.scene == scene);
        if (scene.path != DemoScene || spawner == null)
        {
            lines.Add($"ARRÊT : scène active {scene.path}, sargasses {(spawner != null ? "trouvées" : "introuvables")}.");
        }
        else
        {
            var settings = new SerializedObject(spawner);
            settings.FindProperty("excludeFromDepthTexture").boolValue = true;
            settings.ApplyModifiedProperties();
            bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            lines.Add($"{spawner.name} : Exclude From Depth Texture = {spawner.ExcludeFromDepthTexture}, scène enregistrée : {saved}");
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "depthless.txt"), lines);
    }

    static void Write(List<string> lines)
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "build.txt"), lines);
    }
}
