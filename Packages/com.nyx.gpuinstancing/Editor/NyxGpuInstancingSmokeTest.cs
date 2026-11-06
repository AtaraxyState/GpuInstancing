using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Vérification de l'installation dans un projet (GPU réel) : Tools > Nyx > GPU Instancing > Run Smoke Test.
    /// Construit la scène de démo dans Assets/NyxGpuInstancingTest/ (régénéré à chaque test), puis vérifie : compilation des
    /// shaders et compute shaders, assets par défaut, comportements (liste, ajout, changement), culling GPU, LODs, rendu,
    /// couche d'algues, bulles, Emit(), rochers, ombres. Écrit Logs/NyxGpuInstancingTest/report.md, des captures PNG et status.txt.
    /// En batchmode (sans -nographics) : -executeMethod Nyx.GpuInstancing.Editor.NyxGpuInstancingSmokeTest.RunFromCommandLine.
    /// </summary>
    public static class NyxGpuInstancingSmokeTest
    {
        const string TestFolder = "Assets/NyxGpuInstancingTest";
        const int ImageSize = 512;
        const float LayerThickness = 0.4f;

        public static string OutputFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxGpuInstancingTest"));

        sealed class Report
        {
            public readonly List<string> rows = new List<string>();
            public readonly List<string> notes = new List<string>();
            public readonly List<string> consoleErrors = new List<string>();
            public readonly List<string> images = new List<string>();
            public int failures;

            public void Check(bool ok, string name, string detail = "")
            {
                if (!ok)
                    failures++;
                rows.Add($"| {(ok ? "PASS" : "**FAIL**")} | {name} | {detail.Replace("|", "/").Replace("\n", " ")} |");
            }
        }

        [MenuItem("Tools/Nyx/GPU Instancing/Run Smoke Test", false, 20)]
        public static void RunFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            int failures = Run();
            EditorUtility.DisplayDialog("Nyx GPU Instancing",
                failures == 0 ? "Tous les tests passent." : $"{failures} test(s) en échec.\nRapport : Logs/NyxGpuInstancingTest/report.md", "OK");
        }

        public static void RunFromCommandLine()
        {
            int failures = -1;
            try
            {
                failures = Run();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static int Run()
        {
            Directory.CreateDirectory(OutputFolder);
            WriteStatus("running");
            var report = new Report();
            Application.LogCallback onLog = (message, stackTrace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    report.consoleErrors.Add($"{type}: {message}");
            };
            Application.logMessageReceived += onLog;
            bool asyncCompilation = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;   // variantes compilées avant le rendu, pas de shader de remplacement
            try
            {
                RunChecks(report);
            }
            catch (Exception exception)
            {
                report.Check(false, "Exception pendant le test", exception.ToString());
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncCompilation;
                Application.logMessageReceived -= onLog;
            }
            report.Check(report.consoleErrors.Count == 0, "Console sans erreur pendant le test", $"{report.consoleErrors.Count} erreur(s)");
            WriteReport(report);
            WriteStatus($"done failures={report.failures}");
            Debug.Log($"[Nyx GPU Instancing] {(report.failures == 0 ? "TOUS LES TESTS PASSENT" : report.failures + " ÉCHEC(S)")} - rapport : {Path.Combine(OutputFolder, "report.md")}");
            return report.failures;
        }

        static void RunChecks(Report report)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MeshSpawner).Assembly);
            report.notes.Add($"Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}, " +
                             $"pipeline : {(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in")}");
            report.notes.Add($"Package : {(package != null ? $"{package.name} {package.version} ({package.source}, {package.assetPath})" : "copié dans Assets")}, " +
                             $"assets par défaut : {NyxDefaultAssets.Folder}");
            report.notes.Add($"Compute : {SystemInfo.supportsComputeShaders}, buffers lisibles dans le vertex shader : {SystemInfo.maxComputeBufferInputsVertex}, " +
                             $"multiview : {SystemInfo.supportsMultiview}, XR : {XRSettings.enabled}");

            // ---------------------------------------------------------------- 1. Shaders et compute shaders du package
            Shader sargassumShader = Shader.Find(NyxDefaultAssets.SargassumShaderName);
            Shader bubbleShader = Shader.Find(NyxDefaultAssets.BubbleShaderName);
            Shader waterShader = Shader.Find(NyxDefaultAssets.WaterShaderName);
            Shader litShader = Shader.Find(NyxDefaultAssets.LitShaderName);
            CheckShader(report, sargassumShader, NyxDefaultAssets.SargassumShaderName);
            CheckShader(report, bubbleShader, NyxDefaultAssets.BubbleShaderName);
            CheckShader(report, waterShader, NyxDefaultAssets.WaterShaderName);
            CheckShader(report, litShader, NyxDefaultAssets.LitShaderName);
            bool computesFound = true;
            foreach (string compute in new[] { "FloatOnWaves", "BubbleColumns", "StaticInstances" })
            {
                var shader = Resources.Load<ComputeShader>("NyxGpuInstancing/" + compute);
                CheckCompute(report, shader, "NyxGpuInstancing/" + compute, "NyxCull", "NyxWriteArgs");
                computesFound &= shader != null;
            }
            CheckCompute(report, Resources.Load<ComputeShader>("NyxGpuInstancing/BubbleColumnsBuild"), "NyxGpuInstancing/BubbleColumnsBuild", "NyxBuildBubbles");
            CheckCompute(report, Resources.Load<ComputeShader>("NyxGpuInstancing/InstancePlacement"), "NyxGpuInstancing/InstancePlacement",
                "NyxCountPlacements", "NyxScanGroups", "NyxWriteInstances", "NyxWritePlacements");
            CheckCompute(report, Resources.Load<ComputeShader>("NyxGpuInstancing/InstanceVariants"), "NyxGpuInstancing/InstanceVariants", "NyxCountVariants");
            if (sargassumShader == null || bubbleShader == null || litShader == null || !computesFound)
                return;

            // ---------------------------------------------------------------- 2. Comportements : liste, ajout, changement
            List<Type> behaviors = MeshSpawnerTools.BehaviorTypes;
            report.Check(behaviors.Contains(typeof(FloatOnWaves)) && behaviors.Contains(typeof(BubbleColumns)) && behaviors.Contains(typeof(StaticInstances)),
                "Comportements proposés dans l'inspecteur du MeshSpawner", string.Join(", ", behaviors.ConvertAll(type => type.Name)));

            NyxGpuInstancingDemo.Contents demo = NyxGpuInstancingDemo.Build(null);
            if (AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.CreateFolder("Assets", "NyxGpuInstancingTest");

            Material sargassumMaterial = NyxDefaultAssets.SargassumMaterial;
            Material bubbleMaterial = NyxDefaultAssets.BubbleMaterial;
            Material rockMaterial = NyxDefaultAssets.RockMaterial;
            report.Check(sargassumMaterial.shader == sargassumShader && sargassumMaterial.enableInstancing && sargassumMaterial.mainTexture != null
                         && bubbleMaterial.shader == bubbleShader && bubbleMaterial.enableInstancing
                         && rockMaterial.shader == litShader && rockMaterial.enableInstancing,
                "Assets par défaut : matériaux sargasses, bulles et rochers, GPU Instancing coché", NyxDefaultAssets.Folder);

            // Ajout d'un comportement comme dans l'inspecteur (« Add Component ») : MeshSpawner ajouté, meshes par défaut
            var probe = new GameObject("Ajout de comportement");
            probe.transform.position = new Vector3(500f, -5f, 500f);
            ObjectFactory.AddComponent<FloatOnWaves>(probe);
            var probeSpawner = probe.GetComponent<MeshSpawner>();
            report.Check(probeSpawner != null && probeSpawner.Variants.Count == 2 && probeSpawner.Behavior is FloatOnWaves,
                "Add Component « Float On Waves » : MeshSpawner ajouté, touffes et matériau par défaut",
                probeSpawner != null ? $"{probeSpawner.Variants.Count} variantes" : "pas de MeshSpawner");
            if (probeSpawner != null)
            {
                probeSpawner.RebuildNow();
                int floating = probeSpawner.InstanceCount;
                MeshSpawnerTools.SetBehavior(probeSpawner, typeof(StaticInstances), replaceVariants: true);
                probeSpawner.RebuildNow();
                bool toStatic = probeSpawner.Behavior is StaticInstances && probeSpawner.GetComponents<MeshSpawnerBehavior>().Length == 1
                                && probeSpawner.Variants[0].material == rockMaterial && probeSpawner.InstanceCount == floating && probeSpawner.DrawCallsPerCamera == 2;
                MeshSpawnerTools.SetBehavior(probeSpawner, typeof(BubbleColumns), replaceVariants: true);
                probeSpawner.RebuildNow();
                int bubbleCount = probeSpawner.InstanceCount;
                bool toBubbles = probeSpawner.Behavior is BubbleColumns && probeSpawner.Variants[0].material == bubbleMaterial
                                 && bubbleCount == 40 + 1024 && probeSpawner.DrawCallsPerCamera == 3;
                report.Check(floating > 0 && toStatic && toBubbles,
                    "Changer de comportement : Float On Waves -> Static Instances -> Bubble Columns, même placement, meshes remplacés",
                    $"{floating} instances flottantes, {(toStatic ? "statiques ok" : "statiques KO")}, {bubbleCount} bulles (40 + tampon de 1024)");
                MeshSpawnerTools.SetBehavior(probeSpawner, null);
                probeSpawner.RebuildNow();
                report.Check(probeSpawner.Behavior == null && probeSpawner.InstanceCount == 0, "Sans comportement : rien n'est dessiné", $"{probeSpawner.InstanceCount} instances");
            }
            Object.DestroyImmediate(probe);

            MeshSpawner sargassum = demo.sargassum;
            MeshSpawner bubbles = demo.bubbles;
            MeshSpawner rocks = demo.rocks;
            var floatOnWaves = sargassum.GetComponent<FloatOnWaves>();
            var bubbleColumns = bubbles.GetComponent<BubbleColumns>();
            demo.water.SetActive(false);   // mesures sur les instances seules ; l'eau est vérifiée à la fin
            Camera surfaceCamera = MakeCamera("Cam_Surface", new Vector3(0f, 6f, -45f), new Vector3(0f, 0f, -5f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera skyCamera = MakeCamera("Cam_Sky", new Vector3(0f, 12f, 0f), new Vector3(0f, 30f, 0.01f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera underwaterCamera = MakeCamera("Cam_Underwater", new Vector3(0f, -6f, -2f), new Vector3(0f, -4f, 10f), new Color(0.03f, 0.20f, 0.30f), 70f);
            Camera seabedCamera = MakeCamera("Cam_Fond", new Vector3(0f, -7f, -30f), new Vector3(0f, -12f, 0f), new Color(0.03f, 0.20f, 0.30f), 60f);

            // ---------------------------------------------------------------- 3. Draws et instances
            report.Check(sargassum.InstanceCount > 0, "Sargasses : instances générées", $"{sargassum.InstanceCount} touffes");
            report.Check(sargassum.DrawCallsPerCamera == 4, "Sargasses : 4 draws (2 variantes x 2 LODs) quel que soit le nombre de touffes", $"{sargassum.DrawCallsPerCamera} draws");
            report.Check(bubbles.DrawCallsPerCamera == 3, "Bulles : 3 draws (3 LODs)", $"{bubbles.DrawCallsPerCamera} draws, {bubbles.InstanceCount} instances");
            report.Check(rocks.InstanceCount > 0 && rocks.DrawCallsPerCamera == 2, "Rochers : instances générées, 2 draws (2 LODs)", $"{rocks.InstanceCount} rochers, {rocks.DrawCallsPerCamera} draws");

            // ---------------------------------------------------------------- 4. Surface : culling, LOD, image
            var counts = new List<int>();
            Render(surfaceCamera);
            Texture2D surfaceOn = Render(surfaceCamera);
            bool read = sargassum.TryReadVisibleCounts(surfaceCamera, counts);
            int visible = Sum(counts);
            report.Check(read && visible > 0 && visible < sargassum.InstanceCount,
                "Culling GPU : seule une partie des touffes est visible", $"{visible} / {sargassum.InstanceCount} ({Describe(sargassum, counts)})");
            int lod0 = SumLod(sargassum, counts, "LOD0");
            int lod1 = SumLod(sargassum, counts, "LOD1");
            report.Check(lod0 > 0 && lod1 > 0, "LOD : touffes proches en LOD0, lointaines en LOD1 (seuil 20 m)", $"LOD0 = {lod0}, LOD1 = {lod1}");
            sargassum.enabled = false;
            Texture2D surfaceOff = Render(surfaceCamera);
            sargassum.enabled = true;
            int surfaceDiff = CountDifferentPixels(surfaceOn, surfaceOff);
            report.Check(surfaceDiff > ImageSize * ImageSize / 200, "Rendu : les sargasses apparaissent à l'image",
                $"{surfaceDiff} px différents ({100.0 * surfaceDiff / (ImageSize * ImageSize):F1} %)");
            SavePng(report, surfaceOn, "surface_avec_sargasses");

            // ---------------------------------------------------------------- 5. Caméra tournée vers le ciel
            Render(skyCamera);
            bool readSky = sargassum.TryReadVisibleCounts(skyCamera, counts);
            int visibleSky = readSky ? Sum(counts) : 0;
            report.Check(visibleSky == 0, "Culling : caméra tournée vers le ciel -> 0 touffe",
                readSky ? $"{visibleSky} visibles" : "spawner rejeté en entier côté CPU, aucun dispatch");

            // ---------------------------------------------------------------- 6. Épaisseur de la couche d'algues
            float minDepth = float.MaxValue, maxDepth = float.MinValue;
            foreach (MeshPlacement placement in sargassum.Placements)
            {
                float depth = floatOnWaves.GetLayerDepth(placement);
                minDepth = Mathf.Min(minDepth, depth);
                maxDepth = Mathf.Max(maxDepth, depth);
            }
            report.Check(minDepth >= 0f && maxDepth <= LayerThickness && maxDepth - minDepth > LayerThickness * 0.5f,
                $"Couche d'algues : profondeurs réparties entre 0 et {LayerThickness} m", $"{minDepth:F2} à {maxDepth:F2} m");
            // Eau plate, caméra orthographique au-dessus, plan proche à y = -0,3 m : elle ne voit que sous cette profondeur
            demo.waves.enabled = false;
            Camera slabCamera = MakeCamera("Cam_Couche", new Vector3(0f, 150f, 0f), new Vector3(0f, 0f, 1f), Color.black, 60f);
            slabCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            slabCamera.orthographic = true;
            slabCamera.orthographicSize = 50f;
            slabCamera.nearClipPlane = 150.3f;
            slabCamera.farClipPlane = 180f;
            int belowWithLayer = VisibleWithLayer(sargassum, floatOnWaves, slabCamera, LayerThickness, counts);
            int belowWithoutLayer = VisibleWithLayer(sargassum, floatOnWaves, slabCamera, 0f, counts);
            report.Check(belowWithoutLayer == 0 && belowWithLayer > sargassum.InstanceCount / 4,
                "Couche d'algues (GPU) : les touffes profondes descendent sous la surface",
                $"touffes sous y = -0,3 m, eau plate : {belowWithLayer} avec la couche, {belowWithoutLayer} sans");
            Object.DestroyImmediate(slabCamera.gameObject);
            SetFloat(floatOnWaves, "layerThickness", LayerThickness);
            demo.waves.enabled = true;
            sargassum.RebuildNow();
            bubbles.RebuildNow();

            // ---------------------------------------------------------------- 7. Bulles sous l'eau (brouillard actif)
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.04f;
            RenderSettings.fogColor = new Color(0.03f, 0.20f, 0.30f);
            Render(underwaterCamera);
            Texture2D underwaterOn = Render(underwaterCamera);
            bubbles.TryReadVisibleCounts(underwaterCamera, counts);
            int bubblesVisible = Sum(counts);
            report.Check(bubblesVisible > 0, "Bulles visibles depuis la caméra sous-marine", $"{bubblesVisible} visibles ({Describe(bubbles, counts)})");
            bubbles.enabled = false;
            Texture2D underwaterOff = Render(underwaterCamera);
            bubbles.enabled = true;
            int bubbleDiff = CountDifferentPixels(underwaterOn, underwaterOff);
            report.Check(bubbleDiff > 50, "Rendu : les bulles apparaissent à l'image", $"{bubbleDiff} px différents");
            SavePng(report, underwaterOn, "sous_marin_avec_bulles");

            // ---------------------------------------------------------------- 8. Emit()
            Render(underwaterCamera);
            bubbles.TryReadVisibleCounts(underwaterCamera, counts);
            int before = Sum(counts);
            bubbleColumns.Emit(new Vector3(0f, -7f, 6f), 150, 0.6f);
            Thread.Sleep(400);   // apparition des bulles : 0,12 s par défaut
            Texture2D afterEmit = Render(underwaterCamera);
            bubbles.TryReadVisibleCounts(underwaterCamera, counts);
            int after = Sum(counts);
            report.Check(after >= before + 75, "Emit() : 150 bulles émises dans le champ de vision apparaissent", $"{before} -> {after}");
            SavePng(report, afterEmit, "sous_marin_apres_emit");

            // ---------------------------------------------------------------- 9. Rochers immobiles sur le fond
            Render(seabedCamera);
            Texture2D seabedOn = Render(seabedCamera);
            rocks.TryReadVisibleCounts(seabedCamera, counts);
            int rocksVisible = Sum(counts);
            report.Check(rocksVisible > 0 && rocksVisible < rocks.InstanceCount, "Rochers : culling GPU, une partie est visible depuis le fond",
                $"{rocksVisible} / {rocks.InstanceCount} ({Describe(rocks, counts)})");
            rocks.enabled = false;
            Texture2D seabedOff = Render(seabedCamera);
            rocks.enabled = true;
            int rockDiff = CountDifferentPixels(seabedOn, seabedOff);
            report.Check(rockDiff > ImageSize * ImageSize / 100, "Rendu : les rochers apparaissent (shader Lit)", $"{100.0 * rockDiff / (ImageSize * ImageSize):F1} % de l'image change");
            SavePng(report, seabedOn, "fond_rochers");
            RenderSettings.fog = false;

            // ---------------------------------------------------------------- 10. Ombres
            var receiverMaterial = new Material(Shader.Find("Standard")) { name = "ShadowReceiver_Test", color = new Color(0.10f, 0.35f, 0.45f) };
            AssetDatabase.CreateAsset(receiverMaterial, TestFolder + "/ShadowReceiver_Test.mat");
            GameObject receiver = GameObject.CreatePrimitive(PrimitiveType.Quad);
            receiver.name = "Récepteur d'ombres";
            receiver.transform.SetPositionAndRotation(new Vector3(0f, -1.6f, 0f), Quaternion.Euler(90f, 0f, 0f));
            receiver.transform.localScale = new Vector3(120f, 120f, 1f);
            var receiverRenderer = receiver.GetComponent<MeshRenderer>();
            receiverRenderer.sharedMaterial = receiverMaterial;
            receiverRenderer.shadowCastingMode = ShadowCastingMode.Off;
            Render(surfaceCamera);
            Texture2D receiverNoShadow = Render(surfaceCamera);
            var shadowSettings = new SerializedObject(sargassum);
            shadowSettings.FindProperty("castShadows").enumValueIndex = (int)ShadowCastingMode.On;
            shadowSettings.FindProperty("shadowCullingExtrusion").floatValue = 10f;
            shadowSettings.ApplyModifiedPropertiesWithoutUndo();
            sargassum.RebuildNow();
            int errorsBefore = report.consoleErrors.Count;
            Render(surfaceCamera);
            Texture2D shadowed = Render(surfaceCamera);
            sargassum.TryReadVisibleCounts(surfaceCamera, counts);
            int visibleWithShadows = Sum(counts);
            report.Check(report.consoleErrors.Count == errorsBefore && visibleWithShadows >= visible * 0.95f,
                "Ombres activées : rendu sans erreur, l'extrusion vers la lumière garde au moins autant de touffes", $"{visible} -> {visibleWithShadows} visibles");
            int shadowPixels = CountShadowedPixels(receiverNoShadow, shadowed);
            report.Check(shadowPixels > ImageSize * ImageSize / 1000, "Ombres : les touffes ombrent le plan récepteur",
                $"{shadowPixels} px du récepteur assombris ({100.0 * shadowPixels / (ImageSize * ImageSize):F2} %)");
            SavePng(report, shadowed, "surface_ombres");

            // Hors de la passe de profondeur : touffes toujours dessinées et projetant leur ombre, matériau d'origine intact
            int queueBefore = sargassum.Variants[0].material.renderQueue;
            sargassum.ExcludeFromDepthTexture = true;
            sargassum.RebuildNow();
            Render(surfaceCamera);
            Texture2D depthless = Render(surfaceCamera);
            sargassum.TryReadVisibleCounts(surfaceCamera, counts);
            int visibleDepthless = Sum(counts);
            int shadowPixelsDepthless = CountShadowedPixels(receiverNoShadow, depthless);
            int tuftsWith = CountTuftPixels(shadowed);
            int tuftsDepthless = CountTuftPixels(depthless);
            report.Check(visibleDepthless >= visibleWithShadows * 0.95f && shadowPixelsDepthless > ImageSize * ImageSize / 1000
                         && tuftsDepthless > tuftsWith * 0.7f && sargassum.Variants[0].material.renderQueue == queueBefore && queueBefore <= 2500,
                "Hors de la passe de profondeur : touffes dessinées et ombre projetée, matériau d'origine intact",
                $"{visibleDepthless} visibles ({visibleWithShadows} avant) ; ombre : {shadowPixelsDepthless} px ; touffes : {tuftsDepthless} px ({tuftsWith} avant) ; " +
                $"file du matériau {sargassum.Variants[0].material.renderQueue}");
            SavePng(report, depthless, "surface_hors_profondeur");
            sargassum.ExcludeFromDepthTexture = false;
            Object.DestroyImmediate(receiver);
            shadowSettings.FindProperty("castShadows").enumValueIndex = (int)ShadowCastingMode.Off;
            shadowSettings.ApplyModifiedPropertiesWithoutUndo();
            sargassum.RebuildNow();

            // ---------------------------------------------------------------- 11. Eau de démo (suit la houle)
            demo.water.SetActive(true);
            Render(surfaceCamera);
            Texture2D withWater = Render(surfaceCamera);
            int waterDiff = CountDifferentPixels(withWater, surfaceOn);
            report.Check(waterDiff > ImageSize * ImageSize / 20, "Eau de démo affichée (shader Demo Water)", $"{100.0 * waterDiff / (ImageSize * ImageSize):F1} % de l'image change");
            SavePng(report, withWater, "demo_surface");
            Render(demo.camera);
            SavePng(report, Render(demo.camera), "demo_camera_principale");
            RenderSettings.fog = true;
            SavePng(report, Render(underwaterCamera), "demo_sous_marin");
            RenderSettings.fog = false;

            CheckShader(report, sargassumShader, NyxDefaultAssets.SargassumShaderName + " (après rendu)");
            CheckShader(report, bubbleShader, NyxDefaultAssets.BubbleShaderName + " (après rendu)");
            CheckShader(report, waterShader, NyxDefaultAssets.WaterShaderName + " (après rendu)");
            CheckShader(report, litShader, NyxDefaultAssets.LitShaderName + " (après rendu)");

            // ---------------------------------------------------------------- 12. Chiffres
            var watch = Stopwatch.StartNew();
            const int frames = 50;
            for (int i = 0; i < frames; i++)
                Render(surfaceCamera, readBack: false);
            watch.Stop();
            report.notes.Add($"Sargasses : {sargassum.InstanceCount} instances, {sargassum.DrawCallsPerCamera} draws, {sargassum.GpuMemoryBytes / 1024} Ko GPU (toutes caméras du test)");
            report.notes.Add($"Bulles : {bubbles.InstanceCount} instances, {bubbles.DrawCallsPerCamera} draws, {bubbles.GpuMemoryBytes / 1024} Ko GPU");
            report.notes.Add($"Rochers : {rocks.InstanceCount} instances, {rocks.DrawCallsPerCamera} draws, {rocks.GpuMemoryBytes / 1024} Ko GPU");
            report.notes.Add($"Rendu complet Cam_Surface avec l'eau (éditeur, CPU) : {watch.Elapsed.TotalMilliseconds / frames:F2} ms en moyenne sur {frames} images");

            // ---------------------------------------------------------------- 13. Mémoire
            report.Check(sargassum.CullingBufferSets == 1 && bubbles.CullingBufferSets == 1 && rocks.CullingBufferSets == 1,
                "Mémoire GPU : un seul jeu de buffers de culling pour toutes les caméras rendues l'une après l'autre",
                $"sargasses {sargassum.CullingBufferSets}, bulles {bubbles.CullingBufferSets}, rochers {rocks.CullingBufferSets} (5 caméras du test)");
            int placementSize = System.Runtime.InteropServices.Marshal.SizeOf<MeshPlacement>();
            sargassum.RebuildNow();
            long sargassumBefore = sargassum.CpuMemoryBytes;
            long rocksBefore = rocks.CpuMemoryBytes;
            long bubblesBefore = bubbles.CpuMemoryBytes;
            int placementsRead = sargassum.Placements.Count;
            report.Check(sargassumBefore == 0 && rocksBefore == 0 && bubblesBefore == 0 && placementsRead == sargassum.InstanceCount
                         && sargassum.CpuMemoryBytes == (long)placementsRead * placementSize && sargassum.LastUploadMethod == MeshSpawner.UploadMethod.BuiltOnGpu
                         && rocks.LastUploadMethod == MeshSpawner.UploadMethod.BuiltOnGpu && bubbles.LastUploadMethod == MeshSpawner.UploadMethod.BuiltOnGpu,
                "Mémoire CPU : rien n'est retenu après la reconstruction (instances construites sur GPU), placements lus à la demande",
                $"après reconstruction : sargasses {sargassumBefore} o, rochers {rocksBefore} o, bulles {bubblesBefore} o ; " +
                $"Placements : {placementsRead} lus ({sargassum.CpuMemoryBytes / 1024} Ko, {placementSize} o chacun) ; " +
                $"origine : {sargassum.LastUploadMethod}, {rocks.LastUploadMethod}, {bubbles.LastUploadMethod}");

            // ---------------------------------------------------------------- 14. Instances construites sur GPU / en C#
            CheckCpuInstances(report);

            // ---------------------------------------------------------------- 15. Zone : polygone extrudé
            report.Check(sargassum.ClipToVolume && rocks.ClipToVolume && !bubbles.ClipToVolume && !sargassum.MotionExceedsVolumeHeight,
                "Zone : volume actif pour les sargasses et les rochers, pas pour les bulles ; houle dans le volume des sargasses",
                $"sargasses {sargassum.ClipToVolume}, rochers {rocks.ClipToVolume}, bulles {bubbles.ClipToVolume} ; " +
                $"sommets : sargasses {sargassum.ZonePoints.Count}, rochers {rocks.ZonePoints.Count}");
            CheckZonePlacement(report);
            CheckNaturalZone(report);
            CheckLongZoneList(report);
            CheckZoneDestruction(report);

            Object.DestroyImmediate(surfaceCamera.gameObject);
            Object.DestroyImmediate(skyCamera.gameObject);
            Object.DestroyImmediate(underwaterCamera.gameObject);
            Object.DestroyImmediate(seabedCamera.gameObject);
            EditorSceneManager.SaveScene(demo.scene, TestFolder + "/SmokeTest.unity");
        }

        // ------------------------------------------------------------------------------------------

        static void CheckShader(Report report, Shader shader, string label)
        {
            if (shader == null)
            {
                report.Check(false, $"Shader {label} trouvé", "Shader.Find a renvoyé null");
                return;
            }
            var errors = new List<string>();
            foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(shader))
            {
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    errors.Add($"{message.message} ({message.file}:{message.line}, {message.platform})");
            }
            report.Check(!ShaderUtil.ShaderHasError(shader) && errors.Count == 0, $"Shader {label} compile sans erreur", string.Join(" ; ", errors));
        }

        static void CheckCompute(Report report, ComputeShader shader, string label, params string[] kernelNames)
        {
            if (shader == null)
            {
                report.Check(false, $"Compute {label} trouvé", "Resources.Load a renvoyé null");
                return;
            }
            var errors = new List<string>();
            foreach (ShaderMessage message in ShaderUtil.GetComputeShaderMessages(shader))
            {
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    errors.Add($"{message.message} ({message.file}:{message.line}, {message.platform})");
            }
            bool kernels = errors.Count == 0 && Array.TrueForAll(kernelNames, shader.HasKernel);
            report.Check(errors.Count == 0 && kernels, $"Compute {label} compile, noyaux {string.Join(" et ", kernelNames)} présents", string.Join(" ; ", errors));
        }

        /// <summary>
        /// Instances construites sur GPU identiques (au sinus près) à celles écrites en C# à partir des mêmes placements, lus
        /// sur GPU (MeshSpawner.CreateInstancesOnGpu = false, envoi par SetData), deux fois de suite sur le même buffer.
        /// Spawner temporaire, zone en étoile (placement filtré par le polygone).
        /// </summary>
        static void CheckCpuInstances(Report report)
        {
            var go = new GameObject("Instances en C# (test)");
            go.transform.position = new Vector3(5000f, -50f, 5000f);
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<StaticInstances>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            var settings = new SerializedObject(spawner);
            settings.FindProperty("densityPer100m2").floatValue = 600f;   // ~52 000 candidats sur la boîte, ~27 000 dans l'étoile
            settings.ApplyModifiedPropertiesWithoutUndo();
            var star = new List<Vector2>();
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI * 2f / 12f;
                star.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (i % 2 == 0 ? 50f : 30f));
            }
            spawner.SetZone(Vector3.zero, star, 10f);
            try
            {
                spawner.RebuildNow();
                MeshSpawner.UploadMethod gpu = spawner.LastUploadMethod;
                var onGpu = new GpuInstanceData[spawner.InstanceCount];
                spawner.TryReadInstances(onGpu);

                MeshSpawner.CreateInstancesOnGpu = false;
                spawner.RebuildNow();
                MeshSpawner.UploadMethod first = spawner.LastUploadMethod;
                var expected = new GpuInstanceData[spawner.InstanceCount];
                spawner.TryReadInstances(expected);
                spawner.RebuildNow();
                var again = new GpuInstanceData[spawner.InstanceCount];
                spawner.TryReadInstances(again);

                float worst = 0f;
                int otherVariant = 0;
                int mismatches = 0;
                bool sameCount = onGpu.Length == expected.Length && again.Length == expected.Length;
                for (int i = 0; sameCount && i < expected.Length; i++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        worst = Mathf.Max(worst, Mathf.Abs(onGpu[i].row0[k] - expected[i].row0[k]));
                        worst = Mathf.Max(worst, Mathf.Abs(onGpu[i].row1[k] - expected[i].row1[k]));
                        worst = Mathf.Max(worst, Mathf.Abs(onGpu[i].row2[k] - expected[i].row2[k]));
                    }
                    worst = Mathf.Max(worst, Mathf.Abs(onGpu[i].custom.x - expected[i].custom.x));
                    if (onGpu[i].custom.w != expected[i].custom.w)
                        otherVariant++;
                    if (!Same(again[i], expected[i]))
                        mismatches++;
                }
                report.Check(gpu == MeshSpawner.UploadMethod.BuiltOnGpu && first == MeshSpawner.UploadMethod.SetData && sameCount
                             && expected.Length > 25000 && expected.Length < 29000 && worst < 1e-4f && otherVariant == 0 && mismatches == 0,
                    "Construction sur GPU : instances identiques à celles écrites en C# depuis les mêmes placements (zone en étoile)",
                    $"{onGpu.Length} sur GPU ({gpu}), {expected.Length} en C# ({first}) ; écart max {worst:G3} ; variantes différentes : {otherVariant} ; " +
                    $"2e envoi C# : {mismatches} différence(s)");
            }
            finally
            {
                MeshSpawner.CreateInstancesOnGpu = true;
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Placement dans un polygone concave (L de 40 m, grille alignée sur ses bords : exactement 1 200 instances à
        /// 100 / 100 m²), amas d'une ancre hors zone retirés seulement avec le volume, ancienne zone rectangulaire reprise.
        /// </summary>
        static void CheckZonePlacement(Report report)
        {
            var root = new Vector3(6000f, -50f, 6000f);
            var go = new GameObject("Zone (test)");
            go.transform.position = root;
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<StaticInstances>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            var anchor = new GameObject("Ancre hors zone (test)").transform;
            anchor.position = root + new Vector3(60f, 0f, 0f);
            var settings = new SerializedObject(spawner);
            settings.FindProperty("densityPer100m2").floatValue = 100f;
            SerializedProperty anchors = settings.FindProperty("clusterAnchors");
            anchors.arraySize = 1;
            anchors.GetArrayElementAtIndex(0).objectReferenceValue = anchor;
            settings.FindProperty("clusterRadius").floatValue = 3f;
            settings.FindProperty("clumpsPerCluster").intValue = 20;
            settings.ApplyModifiedPropertiesWithoutUndo();
            try
            {
                Vector2[] lShape =
                {
                    new Vector2(-20f, -20f), new Vector2(20f, -20f), new Vector2(20f, 0f), new Vector2(0f, 0f), new Vector2(0f, 20f), new Vector2(-20f, 20f)
                };
                spawner.SetZone(Vector3.zero, lShape, 5f);
                spawner.ClipToVolume = true;
                spawner.RebuildNow();
                int clipped = spawner.Placements.Count;
                int outside = 0;
                int inNotch = 0;
                foreach (MeshPlacement placement in spawner.Placements)
                {
                    if (!spawner.VolumeContains(placement.position))
                        outside++;
                    if (placement.position.x > root.x && placement.position.z > root.z)
                        inNotch++;
                }
                spawner.ClipToVolume = false;
                spawner.RebuildNow();
                int unclipped = spawner.Placements.Count;
                report.Check(clipped == 1200 && outside == 0 && inNotch == 0 && unclipped == clipped + 20,
                    "Zone : placement dans un polygone concave ; amas d'une ancre hors zone retirés avec le volume, gardés sans",
                    $"{clipped} instances (1 200 attendues), {outside} hors du volume, {inNotch} dans l'encoche ; sans volume : {unclipped} (+ 20 amas)");

                // Spawner enregistré avant la zone : pas de sommets, rectangle de l'ancienne taille
                settings.Update();
                settings.FindProperty("zonePoints").arraySize = 0;
                settings.FindProperty("areaSize").vector2Value = new Vector2(10f, 10f);
                settings.FindProperty("clusterAnchors").arraySize = 0;
                settings.ApplyModifiedPropertiesWithoutUndo();
                spawner.RebuildNow();
                report.Check(spawner.ZonePoints.Count == 4 && spawner.InstanceCount == 100,
                    "Zone : un spawner sans sommets (enregistré avant la zone) garde son rectangle",
                    $"{spawner.ZonePoints.Count} sommets, {spawner.InstanceCount} instances (100 attendues pour 10 x 10 m)");
            }
            finally
            {
                Object.DestroyImmediate(anchor.gameObject);
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Longue liste de sommets (1 024, cercle de 20 m à 1 / m², volume actif) : placée sur GPU, d'accord avec VolumeContains.</summary>
        static void CheckLongZoneList(Report report)
        {
            var go = new GameObject("Longue liste de sommets (test)");
            go.transform.position = new Vector3(6600f, -50f, 6600f);
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<StaticInstances>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            SetFloat(spawner, "densityPer100m2", 100f);
            var circle = new List<Vector2>();
            for (int i = 0; i < 1024; i++)
                circle.Add(new Vector2(Mathf.Cos(i * Mathf.PI * 2f / 1024f), Mathf.Sin(i * Mathf.PI * 2f / 1024f)) * 20f);
            try
            {
                spawner.SetZone(Vector3.zero, circle, 5f);
                spawner.ClipToVolume = true;
                spawner.RebuildNow();
                int disagree = 0;
                foreach (MeshPlacement placement in spawner.Placements)
                {
                    if (!spawner.VolumeContains(placement.position))
                        disagree++;
                }
                float expected = Mathf.PI * 400f;
                report.Check(spawner.ZonePoints.Count == 1024 && Mathf.Abs(spawner.InstanceCount - expected) < expected * 0.05f && disagree == 0,
                    "Zone : longue liste de sommets (1 024) placée sur GPU, d'accord avec VolumeContains",
                    $"{spawner.ZonePoints.Count} sommets, {spawner.InstanceCount} instances ({expected:F0} attendues à 5 % près), {disagree} en désaccord");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Bord naturel : hexagone de 30 m à 1 / m², volume actif. Contour courbe, puis bruit du bord (± 4 m), puis lisière
        /// (6 m). Placement GPU d'accord avec VolumeContains (C#), contour réellement changé, densité réduite par la lisière,
        /// placement reproductible ; figé dans la liste puis reconstruit, mêmes instances (la lisière n'est pas réappliquée).
        /// </summary>
        static void CheckNaturalZone(Report report)
        {
            var root = new Vector3(6400f, -50f, 6400f);
            var go = new GameObject("Bord naturel (test)");
            go.transform.position = root;
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<StaticInstances>();
            spawner.SetVariants(NyxDefaultAssets.RockVariants());
            SetFloat(spawner, "densityPer100m2", 100f);
            var hexagon = new List<Vector2>();
            for (int i = 0; i < 6; i++)
                hexagon.Add(new Vector2(Mathf.Cos(i * Mathf.PI / 3f), Mathf.Sin(i * Mathf.PI / 3f)) * 30f);
            try
            {
                spawner.SetZone(Vector3.zero, hexagon, 5f);
                spawner.ClipToVolume = true;
                spawner.RebuildNow();
                int plain = spawner.InstanceCount;

                spawner.ZoneSmooth = true;
                spawner.RebuildNow();
                int smooth = spawner.InstanceCount;
                List<Vector2> outline = spawner.WorldOutline();

                spawner.ZoneEdgeNoise = 4f;
                spawner.ZoneEdgeNoiseScale = 10f;
                spawner.RebuildNow();
                int noisy = spawner.InstanceCount;
                int disagree = 0, bulging = 0;
                foreach (MeshPlacement placement in spawner.Placements)
                {
                    if (!spawner.VolumeContains(placement.position))
                        disagree++;
                    var xz = new Vector2(placement.position.x, placement.position.z);
                    if (ZoneEdge.Evaluate(xz, outline, 0f, 1f, false, Vector2.zero, 0u) > 0f)
                        bulging++;   // hors de la courbe : le bruit a poussé le bord vers l'extérieur
                }

                spawner.ZoneEdgeFeather = 6f;
                spawner.RebuildNow();
                int feathered = spawner.InstanceCount;
                spawner.RebuildNow();
                int again = spawner.InstanceCount;

                // Placement figé : la liste garde exactement les instances gardées par la lisière (pas de seconde lisière)
                var procedural = new GpuInstanceData[feathered];
                spawner.TryReadInstances(procedural);
                spawner.FreezePlacement();
                spawner.RebuildNow();
                int frozen = spawner.InstanceCount;
                var fromList = new GpuInstanceData[frozen];
                spawner.TryReadInstances(fromList);
                int frozenDiff = Mathf.Abs(frozen - feathered);
                for (int i = 0; i < Mathf.Min(frozen, feathered); i++)
                    frozenDiff += Same(procedural[i], fromList[i]) ? 0 : 1;

                // La courbe passe par les coins de l'hexagone : elle s'arrondit vers le cercle qui les relie (pi × 30² m²)
                report.Check(smooth > plain && smooth < Mathf.PI * 900f * 1.03f && outline.Count > 6 && disagree <= noisy / 1000 && bulging > 0
                             && noisy != smooth && feathered < noisy * 0.95f && feathered > noisy * 0.5f && again == feathered
                             && spawner.Source == MeshSpawner.PlacementSource.List && frozenDiff == 0,
                    "Zone : bord naturel (courbe, bruit du bord, lisière) placé sur GPU, d'accord avec VolumeContains ; placement figé identique",
                    $"hexagone {plain} ; courbe ({outline.Count} sommets) {smooth} ; + bruit ± 4 m {noisy} (dont {bulging} hors de la courbe, " +
                    $"{disagree} en désaccord avec le C#) ; + lisière 6 m {feathered} (reconstruit : {again}) ; " +
                    $"figé puis reconstruit : {frozen} ({frozenDiff} différence(s))");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Destruction sur GPU : Float On Waves dont la face du haut est au niveau de l'eau, les crêtes en sortent. Les touffes
        /// détruites le restent aux images suivantes, toutes reviennent à la reconstruction, aucune sans le volume.
        /// </summary>
        static void CheckZoneDestruction(Report report)
        {
            float water = OceanWaves.WaterLevel;
            var root = new Vector3(6200f, water, 6200f);
            var go = new GameObject("Destruction par le volume (test)");
            go.transform.position = root;
            var spawner = go.AddComponent<MeshSpawner>();
            go.AddComponent<FloatOnWaves>();
            spawner.SetVariants(NyxDefaultAssets.SargassumVariants());
            SetFloat(spawner, "densityPer100m2", 100f);
            Camera camera = MakeCamera("Caméra destruction (test)", root + new Vector3(0f, 8f, -18f), root, Color.black, 60f);
            try
            {
                spawner.SetZone(Vector3.zero, MeshSpawner.RectanglePoints(new Vector2(20f, 20f)), 5f);
                spawner.ClipToVolume = true;
                spawner.RebuildNow();
                bool warned = spawner.MotionExceedsVolumeHeight;
                int deadVariant = spawner.Variants.Count;
                var data = new GpuInstanceData[spawner.InstanceCount];

                Render(camera, readBack: false);
                spawner.TryReadInstances(data);
                var firstDead = new List<int>();
                for (int i = 0; i < data.Length; i++)
                {
                    if ((int)data[i].custom.w == deadVariant)
                        firstDead.Add(i);
                }
                Thread.Sleep(300);   // les vagues avancent
                Render(camera, readBack: false);
                Render(camera, readBack: false);
                spawner.TryReadInstances(data);
                int stillDead = 0;
                int deadLater = 0;
                foreach (int i in firstDead)
                {
                    if ((int)data[i].custom.w == deadVariant)
                        stillDead++;
                }
                foreach (GpuInstanceData instance in data)
                {
                    if ((int)instance.custom.w == deadVariant)
                        deadLater++;
                }

                spawner.RebuildNow();
                spawner.TryReadInstances(data);
                int deadAfterRebuild = CountVariant(data, deadVariant);
                spawner.ClipToVolume = false;
                spawner.RebuildNow();
                Render(camera, readBack: false);
                spawner.TryReadInstances(data);
                int deadWithoutVolume = CountVariant(data, deadVariant);

                report.Check(warned && firstDead.Count > 0 && firstDead.Count < data.Length && stillDead == firstDead.Count
                             && deadLater >= firstDead.Count && deadAfterRebuild == 0 && deadWithoutVolume == 0,
                    "Zone : touffes sorties du volume détruites définitivement (jusqu'à la reconstruction), aucune sans le volume",
                    $"{data.Length} touffes ; détruites : {firstDead.Count} au 1er rendu, {deadLater} ensuite (dont {stillDead} des premières) ; " +
                    $"après reconstruction {deadAfterRebuild} ; sans volume {deadWithoutVolume} ; avertissement houle : {warned}");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                Object.DestroyImmediate(go);
            }
        }

        static int CountVariant(GpuInstanceData[] instances, int variant)
        {
            int count = 0;
            foreach (GpuInstanceData instance in instances)
            {
                if ((int)instance.custom.w == variant)
                    count++;
            }
            return count;
        }

        static bool Same(GpuInstanceData a, GpuInstanceData b) =>
            a.row0.Equals(b.row0) && a.row1.Equals(b.row1) && a.row2.Equals(b.row2) && a.custom.Equals(b.custom);

        static void SetFloat(Object target, string property, float value)
        {
            var settings = new SerializedObject(target);
            settings.FindProperty(property).floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        static int VisibleWithLayer(MeshSpawner spawner, FloatOnWaves floating, Camera camera, float thickness, List<int> counts)
        {
            SetFloat(floating, "layerThickness", thickness);
            spawner.RebuildNow();
            Render(camera, readBack: false);
            return spawner.TryReadVisibleCounts(camera, counts) ? Sum(counts) : 0;
        }

        static Camera MakeCamera(string name, Vector3 position, Vector3 target, Color background, float fieldOfView)
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 300f;
            camera.transform.position = position;
            camera.transform.LookAt(target);
            camera.enabled = false;   // rendu manuel uniquement
            return camera;
        }

        static Texture2D Render(Camera camera, bool readBack = true)
        {
            RenderTexture target = RenderTexture.GetTemporary(ImageSize, ImageSize, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            camera.targetTexture = target;
            camera.Render();
            Texture2D image = null;
            if (readBack)
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                image = new Texture2D(ImageSize, ImageSize, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, ImageSize, ImageSize), 0, 0);
                image.Apply();
                RenderTexture.active = previous;
            }
            camera.targetTexture = previousTarget;
            RenderTexture.ReleaseTemporary(target);
            return image;
        }

        static int CountDifferentPixels(Texture2D a, Texture2D b)
        {
            Color32[] pa = a.GetPixels32();
            Color32[] pb = b.GetPixels32();
            int different = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int delta = Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b);
                if (delta > 6)
                    different++;
            }
            return different;
        }

        /// <summary>Pixels du récepteur (bleu, B > R) nettement plus sombres avec ombres ; les touffes brunes (R > B) sont ignorées.</summary>
        static int CountShadowedPixels(Texture2D withoutShadows, Texture2D withShadows)
        {
            Color32[] pa = withoutShadows.GetPixels32();
            Color32[] pb = withShadows.GetPixels32();
            int shadowed = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                bool receiverA = pa[i].b > pa[i].r + 8;
                bool receiverB = pb[i].b > pb[i].r + 8;
                int lumA = pa[i].r + pa[i].g + pa[i].b;
                int lumB = pb[i].r + pb[i].g + pb[i].b;
                if (receiverA && receiverB && lumB * 4 < lumA * 3)
                    shadowed++;
            }
            return shadowed;
        }

        /// <summary>Pixels bruns des touffes (R nettement plus grand que B).</summary>
        static int CountTuftPixels(Texture2D image)
        {
            int count = 0;
            foreach (Color32 pixel in image.GetPixels32())
            {
                if (pixel.r > pixel.b + 8)
                    count++;
            }
            return count;
        }

        static void SavePng(Report report, Texture2D image, string name)
        {
            string file = name + ".png";
            File.WriteAllBytes(Path.Combine(OutputFolder, file), image.EncodeToPNG());
            report.images.Add(file);
        }

        static int Sum(List<int> values)
        {
            int total = 0;
            foreach (int value in values)
                total += value;
            return total;
        }

        static int SumLod(MeshSpawner field, List<int> counts, string lod)
        {
            int total = 0;
            IReadOnlyList<string> labels = field.DrawLabels;
            for (int i = 0; i < counts.Count && i < labels.Count; i++)
            {
                if (labels[i].EndsWith(lod, StringComparison.Ordinal))
                    total += counts[i];
            }
            return total;
        }

        static string Describe(MeshSpawner field, List<int> counts)
        {
            var text = new StringBuilder();
            IReadOnlyList<string> labels = field.DrawLabels;
            for (int i = 0; i < counts.Count && i < labels.Count; i++)
                text.Append(i == 0 ? "" : ", ").Append(labels[i]).Append('=').Append(counts[i]);
            return text.ToString();
        }

        static void WriteReport(Report report)
        {
            var text = new StringBuilder();
            text.AppendLine("# Nyx GPU Instancing : test de fumée");
            text.AppendLine();
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} : **{(report.failures == 0 ? "TOUS LES TESTS PASSENT" : report.failures + " ÉCHEC(S)")}**");
            text.AppendLine();
            text.AppendLine("| Résultat | Test | Détail |");
            text.AppendLine("|---|---|---|");
            foreach (string row in report.rows)
                text.AppendLine(row);
            text.AppendLine();
            text.AppendLine("## Environnement et chiffres");
            text.AppendLine();
            foreach (string note in report.notes)
                text.AppendLine("- " + note);
            text.AppendLine();
            text.AppendLine("## Captures (512 x 512)");
            text.AppendLine();
            foreach (string image in report.images)
                text.AppendLine($"- {image}");
            if (report.consoleErrors.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("## Erreurs console pendant le test");
                text.AppendLine();
                for (int i = 0; i < report.consoleErrors.Count && i < 30; i++)
                    text.AppendLine("- " + report.consoleErrors[i]);
            }
            File.WriteAllText(Path.Combine(OutputFolder, "report.md"), text.ToString(), Encoding.UTF8);
        }

        static void WriteStatus(string status) =>
            File.WriteAllText(Path.Combine(OutputFolder, "status.txt"), $"{status} {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
    }
}
