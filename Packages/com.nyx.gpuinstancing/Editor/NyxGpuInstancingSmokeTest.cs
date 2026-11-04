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
    /// shaders et compute shaders, assets par défaut, valeurs par défaut à l'ajout d'un composant, culling GPU, LODs, rendu,
    /// couche d'algues, bulles, Emit(), ombres. Écrit Logs/NyxGpuInstancingTest/report.md, des captures PNG et status.txt.
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
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SargassumField).Assembly);
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
            var sargassumCulling = Resources.Load<ComputeShader>("NyxGpuInstancing/NyxSargassumCulling");
            var bubbleCulling = Resources.Load<ComputeShader>("NyxGpuInstancing/NyxBubbleCulling");
            CheckShader(report, sargassumShader, NyxDefaultAssets.SargassumShaderName);
            CheckShader(report, bubbleShader, NyxDefaultAssets.BubbleShaderName);
            CheckShader(report, waterShader, NyxDefaultAssets.WaterShaderName);
            CheckCompute(report, sargassumCulling, "NyxGpuInstancing/NyxSargassumCulling");
            CheckCompute(report, bubbleCulling, "NyxGpuInstancing/NyxBubbleCulling");
            if (sargassumShader == null || bubbleShader == null || sargassumCulling == null || bubbleCulling == null)
                return;

            // ---------------------------------------------------------------- 2. Scène de démo (assets par défaut)
            NyxGpuInstancingDemo.Contents demo = NyxGpuInstancingDemo.Build(null);
            if (AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.CreateFolder("Assets", "NyxGpuInstancingTest");

            Material sargassumMaterial = NyxDefaultAssets.SargassumMaterial;
            Material bubbleMaterial = NyxDefaultAssets.BubbleMaterial;
            report.Check(sargassumMaterial.shader == sargassumShader && sargassumMaterial.enableInstancing && sargassumMaterial.mainTexture != null
                         && bubbleMaterial.shader == bubbleShader && bubbleMaterial.enableInstancing,
                "Assets par défaut : matériaux sargasses et bulles, GPU Instancing coché", AssetDatabase.GetAssetPath(sargassumMaterial));

            // Ajout comme dans l'inspecteur (« Add Component ») : touffes, matériaux et compute shader sans réglage
            var probe = new GameObject("Ajout de composants");
            var addedSargassum = ObjectFactory.AddComponent<SargassumField>(probe);
            var addedBubbles = ObjectFactory.AddComponent<BubbleField>(probe);
            var probeSargassum = new SerializedObject(addedSargassum);
            var probeBubbles = new SerializedObject(addedBubbles);
            report.Check(probeSargassum.FindProperty("variants").arraySize == 2 && probeSargassum.FindProperty("cullingShader").objectReferenceValue == sargassumCulling
                         && probeBubbles.FindProperty("bubbleMaterial").objectReferenceValue == bubbleMaterial && probeBubbles.FindProperty("emitters").arraySize == 1,
                "Add Component : le champ reçoit touffes, matériau et compute shader par défaut",
                $"{probeSargassum.FindProperty("variants").arraySize} variantes, compute {(probeSargassum.FindProperty("cullingShader").objectReferenceValue != null ? "ok" : "absent")}, " +
                $"{probeBubbles.FindProperty("emitters").arraySize} émetteur");
            Object.DestroyImmediate(probe);

            SargassumField sargassum = demo.sargassum;
            BubbleField bubbles = demo.bubbles;
            demo.water.SetActive(false);   // mesures sur les instances seules ; l'eau est vérifiée à la fin
            Camera surfaceCamera = MakeCamera("Cam_Surface", new Vector3(0f, 6f, -45f), new Vector3(0f, 0f, -5f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera skyCamera = MakeCamera("Cam_Sky", new Vector3(0f, 12f, 0f), new Vector3(0f, 30f, 0.01f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera underwaterCamera = MakeCamera("Cam_Underwater", new Vector3(0f, -6f, -2f), new Vector3(0f, -4f, 10f), new Color(0.03f, 0.20f, 0.30f), 70f);

            // ---------------------------------------------------------------- 3. Draws et instances
            report.Check(sargassum.InstanceCount > 0, "Sargasses : instances générées", $"{sargassum.InstanceCount} touffes");
            report.Check(sargassum.DrawCallsPerCamera == 4, "Sargasses : 4 draws (2 variantes x 2 LODs) quel que soit le nombre de touffes", $"{sargassum.DrawCallsPerCamera} draws");
            report.Check(bubbles.DrawCallsPerCamera == 3, "Bulles : 3 draws (3 LODs)", $"{bubbles.DrawCallsPerCamera} draws, {bubbles.InstanceCount} instances");

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
                readSky ? $"{visibleSky} visibles" : "champ rejeté en entier côté CPU, aucun dispatch");

            // ---------------------------------------------------------------- 6. Épaisseur de la couche d'algues
            float minDepth = float.MaxValue, maxDepth = float.MinValue;
            foreach (SargassumPlacement placement in sargassum.Placements)
            {
                float depth = sargassum.GetLayerDepth(placement);
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
            int belowWithLayer = VisibleWithLayer(sargassum, slabCamera, LayerThickness, counts);
            int belowWithoutLayer = VisibleWithLayer(sargassum, slabCamera, 0f, counts);
            report.Check(belowWithoutLayer == 0 && belowWithLayer > sargassum.InstanceCount / 4,
                "Couche d'algues (GPU) : les touffes profondes descendent sous la surface",
                $"touffes sous y = -0,3 m, eau plate : {belowWithLayer} avec la couche, {belowWithoutLayer} sans");
            Object.DestroyImmediate(slabCamera.gameObject);
            SetFloat(sargassum, "layerThickness", LayerThickness);
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
            bubbles.Emit(new Vector3(0f, -7f, 6f), 150, 0.6f);
            Thread.Sleep(400);   // apparition des bulles : 0,12 s par défaut
            Texture2D afterEmit = Render(underwaterCamera);
            bubbles.TryReadVisibleCounts(underwaterCamera, counts);
            int after = Sum(counts);
            report.Check(after >= before + 75, "Emit() : 150 bulles émises dans le champ de vision apparaissent", $"{before} -> {after}");
            SavePng(report, afterEmit, "sous_marin_apres_emit");
            RenderSettings.fog = false;

            // ---------------------------------------------------------------- 9. Ombres
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
            Object.DestroyImmediate(receiver);
            shadowSettings.FindProperty("castShadows").enumValueIndex = (int)ShadowCastingMode.Off;
            shadowSettings.ApplyModifiedPropertiesWithoutUndo();
            sargassum.RebuildNow();

            // ---------------------------------------------------------------- 10. Eau de démo (suit la houle)
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

            // ---------------------------------------------------------------- 11. Chiffres
            var watch = Stopwatch.StartNew();
            const int frames = 50;
            for (int i = 0; i < frames; i++)
                Render(surfaceCamera, readBack: false);
            watch.Stop();
            report.notes.Add($"Sargasses : {sargassum.InstanceCount} instances, {sargassum.DrawCallsPerCamera} draws, {sargassum.GpuMemoryBytes / 1024} Ko GPU (toutes caméras du test)");
            report.notes.Add($"Bulles : {bubbles.InstanceCount} instances, {bubbles.DrawCallsPerCamera} draws, {bubbles.GpuMemoryBytes / 1024} Ko GPU");
            report.notes.Add($"Rendu complet Cam_Surface avec l'eau (éditeur, CPU) : {watch.Elapsed.TotalMilliseconds / frames:F2} ms en moyenne sur {frames} images");

            Object.DestroyImmediate(surfaceCamera.gameObject);
            Object.DestroyImmediate(skyCamera.gameObject);
            Object.DestroyImmediate(underwaterCamera.gameObject);
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

        static void CheckCompute(Report report, ComputeShader shader, string label)
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
            bool kernels = errors.Count == 0 && shader.HasKernel("NyxCull") && shader.HasKernel("NyxWriteArgs");
            report.Check(errors.Count == 0 && kernels, $"Compute {label} compile, noyaux NyxCull et NyxWriteArgs présents", string.Join(" ; ", errors));
        }

        static void SetFloat(Object target, string property, float value)
        {
            var settings = new SerializedObject(target);
            settings.FindProperty(property).floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        static int VisibleWithLayer(SargassumField field, Camera camera, float thickness, List<int> counts)
        {
            SetFloat(field, "layerThickness", thickness);
            field.RebuildNow();
            Render(camera, readBack: false);
            return field.TryReadVisibleCounts(camera, counts) ? Sum(counts) : 0;
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

        static int SumLod(InstancedFieldBase field, List<int> counts, string lod)
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

        static string Describe(InstancedFieldBase field, List<int> counts)
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
