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
///  - demo.request    : reconstruit la scène de démo (Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity) ;
///  - state.request   : écrit dans state.txt les scènes ouvertes et si elles ont des modifications non enregistrées ;
///  - invoke.request  : appelle la méthode statique « Type, Assembly|Méthode » écrite dans le fichier, résultat dans invoke.txt.
/// Le test et la démo ne sont pas lancés si une scène ouverte a des modifications non enregistrées (sauf la scène de démo,
/// qui est reconstruite).
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
        string invoke = Path.Combine(folder, "invoke.request");
        if (File.Exists(invoke))
        {
            // Contenu : « Type, Assembly|Méthode » (méthode statique sans paramètre)
            string[] target = File.ReadAllText(invoke).Trim().Split('|');
            File.Delete(invoke);
            Type type = Type.GetType(target[0]);
            MethodInfo method = target.Length > 1 ? type?.GetMethod(target[1].Trim(), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            string result;
            try
            {
                if (method == null)
                    throw new MissingMethodException(string.Join("|", target));
                method.Invoke(null, null);
                result = "ok";
            }
            catch (Exception exception)
            {
                result = "exception " + exception;
            }
            File.WriteAllText(Path.Combine(folder, "invoke.txt"), result);
            return;
        }
        if (Take(folder, "state.request"))
        {
            var state = new System.Text.StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene open = SceneManager.GetSceneAt(i);
                state.AppendLine($"{open.path} dirty={open.isDirty}");
            }
            File.WriteAllText(Path.Combine(folder, "state.txt"), state.ToString());
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
            // Rien à préserver : scène dont le fichier a été supprimé, ou scène de démo (reconstruite par demo.request)
            bool fileDeleted = !string.IsNullOrEmpty(scene.path) && !File.Exists(Path.Combine(Application.dataPath, "..", scene.path));
            bool generatedDemo = scene.path == DemoScenePath;
            if (scene.isDirty && !fileDeleted && !generatedDemo)
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
