using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Outil de développement du package Nyx GPU Instancing (propre à ce projet, hors package) : lancement sans clic quand Unity
/// est ouvert. Appels par réflexion : ce script compile même si le package n'est pas (encore) chargé.
/// Fichiers dans Logs/NyxGpuInstancingTest/ :
///  - resolve.request : relit Packages/ (enregistre un package ajouté ou modifié) ;
///  - refresh.request : réimporte les fichiers modifiés (recompilation) ;
///  - run.request     : test de fumée du package ;
///  - demo.request    : reconstruit la scène de démo (Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity).
/// Le test et la démo ne sont pas lancés si une scène ouverte a des modifications non enregistrées.
/// </summary>
[InitializeOnLoad]
static class NyxGpuInstancingDevTrigger
{
    const string DemoScenePath = "Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity";
    static double s_NextPoll;

    static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));

    static NyxGpuInstancingDevTrigger() => EditorApplication.update += Poll;

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < s_NextPoll)
            return;
        s_NextPoll = EditorApplication.timeSinceStartup + 1.0;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        string folder = Folder;
        if (Take(folder, "resolve.request"))
        {
            UnityEditor.PackageManager.Client.Resolve();
            return;
        }
        if (Take(folder, "refresh.request"))
        {
            AssetDatabase.Refresh();
            return;
        }
        bool run = File.Exists(Path.Combine(folder, "run.request"));
        bool demo = File.Exists(Path.Combine(folder, "demo.request"));
        if (!run && !demo)
            return;
        Take(folder, "run.request");
        Take(folder, "demo.request");
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            // Scène dont le fichier a été supprimé (ex. démo d'un outil retiré) : rien à préserver
            bool fileDeleted = !string.IsNullOrEmpty(scene.path) && !File.Exists(Path.Combine(Application.dataPath, "..", scene.path));
            if (scene.isDirty && !fileDeleted)
            {
                File.WriteAllText(Path.Combine(folder, "status.txt"),
                    "blocked : une scène ouverte a des modifications non enregistrées (enregistrer ou annuler dans Unity, puis relancer)");
                return;
            }
        }
        if (run)
            Invoke("Nyx.GpuInstancing.Editor.NyxGpuInstancingSmokeTest", "Run");
        if (demo)
        {
            Invoke("Nyx.GpuInstancing.Editor.NyxGpuInstancingDemo", "Build", DemoScenePath);
            File.WriteAllText(Path.Combine(folder, "demo.txt"), "built " + DateTime.Now.ToString("s"));
        }
    }

    static bool Take(string folder, string file)
    {
        string path = Path.Combine(folder, file);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    static void Invoke(string typeName, string method, params object[] arguments)
    {
        Type type = Type.GetType(typeName + ", Nyx.GpuInstancing.Editor");
        MethodInfo info = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        if (info == null)
        {
            File.WriteAllText(Path.Combine(Folder, "status.txt"), $"blocked : {typeName}.{method} introuvable (package non chargé ?)");
            return;
        }
        info.Invoke(null, arguments);
    }
}
