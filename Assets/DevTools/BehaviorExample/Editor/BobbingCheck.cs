using System.Collections.Generic;
using System.IO;
using Nyx.GpuInstancing;
using Nyx.GpuInstancing.Editor;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Vérifie l'exemple Bobbing du README (comportement écrit hors du package) dans une scène temporaire :
/// listé dans l'inspecteur, compute shader compilé, instances dessinées. Résultat dans Logs/NyxGpuInstancingTest/bobbing.txt.
/// Lancement : Logs/NyxGpuInstancingTest/invoke.request contenant « BobbingCheck, Assembly-CSharp-Editor|Run ».
/// </summary>
static class BobbingCheck
{
    public static void Run()
    {
        var lines = new List<string>();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            lines.Add($"listé dans l'inspecteur : {MeshSpawnerTools.BehaviorTypes.Contains(typeof(Bobbing))}");
            var compute = Resources.Load<ComputeShader>("Bobbing");
            int errors = 0;
            if (compute != null)
            {
                foreach (ShaderMessage message in ShaderUtil.GetComputeShaderMessages(compute))
                {
                    if (message.severity == ShaderCompilerMessageSeverity.Error)
                    {
                        errors++;
                        lines.Add($"erreur compute : {message.message}");
                    }
                }
            }
            lines.Add($"compute trouvé : {compute != null}, erreurs : {errors}, noyaux : {compute != null && compute.HasKernel("NyxCull") && compute.HasKernel("NyxWriteArgs")}");

            var go = new GameObject("Bobbing test");
            SceneManager_Move(go, scene);
            go.AddComponent<MeshSpawner>();
            var behavior = go.AddComponent<Bobbing>();
            MeshSpawner spawner = go.GetComponent<MeshSpawner>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            var settings = new SerializedObject(spawner);
            settings.FindProperty("densityPer100m2").floatValue = 10f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            // Le dandinement monte au-dessus de la face du haut, où se fait le placement : sans destruction par le volume
            spawner.SetZone(Vector3.zero, MeshSpawner.RectanglePoints(new Vector2(20f, 20f)), 10f);
            spawner.ClipToVolume = false;
            spawner.RebuildNow();
            lines.Add($"comportement actif : {spawner.Behavior == behavior}, instances : {spawner.InstanceCount}, draws : {spawner.DrawCallsPerCamera}");

            var camera = new GameObject("Camera").AddComponent<Camera>();
            SceneManager_Move(camera.gameObject, scene);
            camera.transform.position = new Vector3(0f, 8f, -20f);
            camera.transform.LookAt(Vector3.zero);
            camera.enabled = false;
            var target = RenderTexture.GetTemporary(256, 256, 24);
            camera.targetTexture = target;
            camera.Render();
            var counts = new List<int>();
            spawner.TryReadVisibleCounts(camera, counts);
            int visible = 0;
            foreach (int count in counts)
                visible += count;
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(target);
            lines.Add($"visibles : {visible}");
            lines.Add(visible > 0 && errors == 0 && compute != null ? "RESULTAT : OK" : "RESULTAT : ECHEC");
        }
        catch (System.Exception exception)
        {
            lines.Add("RESULTAT : EXCEPTION " + exception);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));
        File.WriteAllLines(Path.Combine(folder, "bobbing.txt"), lines);
    }

    static void SceneManager_Move(GameObject go, UnityEngine.SceneManagement.Scene scene) =>
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
}
