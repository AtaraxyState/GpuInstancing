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
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;

namespace Nyx.Instancing.EditorTests
{
    /// <summary>
    /// Test de fumée de NyxInstancing, dans l'éditeur (GPU réel) :
    /// compilation des shaders, culling GPU (instances visibles par draw), LODs, rendu (différence d'image
    /// avec / sans champ), Emit(), ombres. Écrit Logs/NyxSmokeTest/report.md, des captures PNG et status.txt.
    ///
    /// Lancement :
    ///  - menu Tools > Nyx > Instancing Smoke Test ;
    ///  - Unity fermé : -batchmode -executeMethod Nyx.Instancing.EditorTests.NyxSmokeTest.RunFromCommandLine (sans -nographics) ;
    ///  - Unity ouvert, sans clic : créer Logs/NyxSmokeTest/run.request (refresh.request force d'abord l'import du code modifié).
    /// La scène de test est enregistrée dans Assets/NyxInstancingTest/SmokeTest.unity (dossier régénéré à chaque test).
    /// </summary>
    public static class NyxSmokeTest
    {
        const string AssetFolder = "Assets/NyxInstancingTest";
        const int ImageSize = 512;
        const float LayerThickness = 0.4f;   // épaisseur de la couche d'algues (m)

        public static string OutputFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "NyxSmokeTest"));

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

        [MenuItem("Tools/Nyx/Instancing Smoke Test")]
        public static void RunFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            int failures = Run();
            EditorUtility.DisplayDialog("NyxInstancing",
                failures == 0 ? "Tous les tests passent." : $"{failures} test(s) en échec.\nRapport : Logs/NyxSmokeTest/report.md", "OK");
        }

        /// <summary>Point d'entrée -executeMethod. Code de sortie : 0 si tout passe, 1 sinon.</summary>
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
            Debug.Log($"[NyxSmokeTest] {(report.failures == 0 ? "TOUS LES TESTS PASSENT" : report.failures + " ÉCHEC(S)")} - rapport : {Path.Combine(OutputFolder, "report.md")}");
            return report.failures;
        }

        static void RunChecks(Report report)
        {
            report.notes.Add($"Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}");
            report.notes.Add($"Compute : {SystemInfo.supportsComputeShaders}, buffers lisibles dans le vertex shader : {SystemInfo.maxComputeBufferInputsVertex}, multiview : {SystemInfo.supportsMultiview}, XR : {XRSettings.enabled}");

            // ---------------------------------------------------------------- 1. Shaders
            Shader sargassumShader = Shader.Find("Nyx/Instancing/Sargassum");
            Shader bubbleShader = Shader.Find("Nyx/Instancing/Bubble");
            var sargassumCulling = Resources.Load<ComputeShader>("NyxSargassumCulling");
            var bubbleCulling = Resources.Load<ComputeShader>("NyxBubbleCulling");
            CheckShader(report, sargassumShader, "Nyx/Instancing/Sargassum");
            CheckShader(report, bubbleShader, "Nyx/Instancing/Bubble");
            CheckCompute(report, sargassumCulling, "NyxSargassumCulling");
            CheckCompute(report, bubbleCulling, "NyxBubbleCulling");
            if (sargassumShader == null || bubbleShader == null || sargassumCulling == null || bubbleCulling == null)
                return;

            // ---------------------------------------------------------------- 2. Assets de test
            if (AssetDatabase.IsValidFolder(AssetFolder))
                AssetDatabase.DeleteAsset(AssetFolder);
            AssetDatabase.CreateFolder("Assets", "NyxInstancingTest");

            Texture2D frondTexture = BuildFrondTexture();
            AssetDatabase.CreateAsset(frondTexture, AssetFolder + "/FrondTexture.asset");
            var sargassumMaterial = new Material(sargassumShader) { name = "Sargassum_Test", enableInstancing = true };
            sargassumMaterial.SetTexture("_MainTex", frondTexture);
            AssetDatabase.CreateAsset(sargassumMaterial, AssetFolder + "/Sargassum_Test.mat");
            var bubbleMaterial = new Material(bubbleShader) { name = "Bubble_Test", enableInstancing = true };
            AssetDatabase.CreateAsset(bubbleMaterial, AssetFolder + "/Bubble_Test.mat");

            Mesh[] clumpA = { BuildClump("ClumpA_LOD0", 8, 3, 0.9f, 0.18f), BuildClump("ClumpA_LOD1", 4, 1, 0.9f, 0.24f) };
            Mesh[] clumpB = { BuildClump("ClumpB_LOD0", 6, 3, 0.7f, 0.2f), BuildClump("ClumpB_LOD1", 3, 1, 0.7f, 0.26f) };
            foreach (Mesh mesh in new[] { clumpA[0], clumpA[1], clumpB[0], clumpB[1] })
                AssetDatabase.CreateAsset(mesh, $"{AssetFolder}/{mesh.name}.asset");
            AssetDatabase.SaveAssets();

            // ---------------------------------------------------------------- 3. Scène
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.fog = false;
            // Ambiant explicite : une scène vide n'a pas de sonde ambiante calculée (faces à l'ombre noires)
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.36f, 0.42f);

            var oceanWaves = new GameObject("Ocean Waves").AddComponent<OceanWaves>();

            var mine = new GameObject("Mine (ancre)").transform;
            mine.position = new Vector3(5f, 0f, 10f);
            var decoy = new GameObject("Leurre (ancre)").transform;
            decoy.position = new Vector3(-12f, 0f, 0f);

            var sargassum = new GameObject("Sargassum Field").AddComponent<SargassumField>();
            var sargassumSettings = new SerializedObject(sargassum);
            SetVariants(sargassumSettings.FindProperty("variants"), new[] { clumpA, clumpB }, 20f, sargassumMaterial);
            sargassumSettings.FindProperty("areaSize").vector2Value = new Vector2(80f, 80f);
            sargassumSettings.FindProperty("densityPer100m2").floatValue = 6f;
            SerializedProperty anchors = sargassumSettings.FindProperty("clusterAnchors");
            anchors.arraySize = 2;
            anchors.GetArrayElementAtIndex(0).objectReferenceValue = mine;
            anchors.GetArrayElementAtIndex(1).objectReferenceValue = decoy;
            sargassumSettings.FindProperty("clusterRadius").floatValue = 4f;
            sargassumSettings.FindProperty("clumpsPerCluster").intValue = 30;
            sargassumSettings.FindProperty("maxDrawDistance").floatValue = 200f;
            sargassumSettings.FindProperty("layerThickness").floatValue = LayerThickness;
            sargassumSettings.ApplyModifiedPropertiesWithoutUndo();
            sargassum.RebuildNow();

            var bubbles = new GameObject("Bubble Field").AddComponent<BubbleField>();
            bubbles.transform.position = new Vector3(0f, -10f, 0f);
            var bubbleSettings = new SerializedObject(bubbles);
            bubbleSettings.FindProperty("bubbleMaterial").objectReferenceValue = bubbleMaterial;
            SerializedProperty emitters = bubbleSettings.FindProperty("emitters");
            Vector3[] emitterPositions = { new Vector3(-2f, 0f, 8f), new Vector3(0f, 0f, 10f), new Vector3(2.5f, 0f, 9f) };
            emitters.arraySize = emitterPositions.Length;
            for (int i = 0; i < emitterPositions.Length; i++)
                SetEmitter(emitters.GetArrayElementAtIndex(i), emitterPositions[i], 60);
            bubbleSettings.FindProperty("burstCapacity").intValue = 512;
            bubbleSettings.FindProperty("burstSettings").FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            bubbleSettings.FindProperty("maxDrawDistance").floatValue = 60f;
            bubbleSettings.ApplyModifiedPropertiesWithoutUndo();
            bubbles.RebuildNow();

            Camera surfaceCamera = MakeCamera("Cam_Surface", new Vector3(0f, 6f, -45f), new Vector3(0f, 0f, -5f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera skyCamera = MakeCamera("Cam_Sky", new Vector3(0f, 12f, 0f), new Vector3(0f, 30f, 0.01f), new Color(0.55f, 0.70f, 0.85f), 60f);
            Camera underwaterCamera = MakeCamera("Cam_Underwater", new Vector3(0f, -6f, -2f), new Vector3(0f, -4f, 10f), new Color(0.03f, 0.20f, 0.30f), 70f);

            // ---------------------------------------------------------------- 4. Draws et instances
            report.Check(sargassum.InstanceCount > 0, "Sargasses : instances générées", $"{sargassum.InstanceCount} touffes");
            report.Check(sargassum.DrawCallsPerCamera == 4, "Sargasses : 4 draws (2 variantes x 2 LODs) quel que soit le nombre de touffes", $"{sargassum.DrawCallsPerCamera} draws");
            report.Check(bubbles.DrawCallsPerCamera == 3, "Bulles : 3 draws (3 LODs)", $"{bubbles.DrawCallsPerCamera} draws, {bubbles.InstanceCount} instances (180 en boucle + 512 ponctuelles)");

            // ---------------------------------------------------------------- 5. Surface : culling, LOD, image
            var counts = new List<int>();
            Render(surfaceCamera);   // préchauffage : compilation synchrone des variantes
            Texture2D surfaceOn = Render(surfaceCamera);
            bool read = sargassum.TryReadVisibleCounts(surfaceCamera, counts);
            int visible = Sum(counts);
            report.Check(read && visible > 0 && visible < sargassum.InstanceCount,
                "Culling frustum : seule une partie des touffes est visible", $"{visible} / {sargassum.InstanceCount} ({Describe(sargassum, counts)})");
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
            SavePng(report, surfaceOff, "surface_sans_sargasses");

            // ---------------------------------------------------------------- 6. Caméra tournée vers le ciel
            Render(skyCamera);
            bool readSky = sargassum.TryReadVisibleCounts(skyCamera, counts);
            int visibleSky = readSky ? Sum(counts) : 0;
            report.Check(visibleSky == 0, "Culling : caméra tournée vers le ciel -> 0 touffe",
                readSky ? $"{visibleSky} visibles" : "champ rejeté en entier côté CPU, aucun dispatch");

            // ---------------------------------------------------------------- 6 bis. Épaisseur de la couche d'algues
            float minDepth = float.MaxValue;
            float maxDepth = float.MinValue;
            foreach (SargassumPlacement placement in sargassum.Placements)
            {
                float depth = sargassum.GetLayerDepth(placement);
                minDepth = Mathf.Min(minDepth, depth);
                maxDepth = Mathf.Max(maxDepth, depth);
            }
            report.Check(minDepth >= 0f && maxDepth <= LayerThickness && maxDepth - minDepth > LayerThickness * 0.5f,
                $"Couche d'algues : profondeurs des touffes réparties entre 0 et {LayerThickness} m", $"{minDepth:F2} à {maxDepth:F2} m");

            // GPU : eau plate, caméra orthographique au-dessus du champ, tournée vers le bas, dont le plan proche est à
            // y = -0,3 m : elle ne voit que sous cette profondeur. Sans couche, aucune touffe n'y descend (bas des bounds
            // >= -0,18 m) ; avec la couche, les plus profondes y sont. Plan lointain éloigné : le culling plafonne aussi la
            // distance (radiale) au plan lointain de la caméra.
            oceanWaves.enabled = false;
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
            UnityEngine.Object.DestroyImmediate(slabCamera.gameObject);
            SetLayerThickness(sargassum, LayerThickness);
            oceanWaves.enabled = true;
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
            SavePng(report, underwaterOff, "sous_marin_sans_bulles");

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
            // Récepteur sous la houle et la couche (creux max ≈ 0,8 m + 0,4 m) : sans lui, aucune ombre n'est visible à l'image.
            var receiverMaterial = new Material(Shader.Find("Standard")) { name = "ShadowReceiver_Test", color = new Color(0.10f, 0.35f, 0.45f) };
            AssetDatabase.CreateAsset(receiverMaterial, AssetFolder + "/ShadowReceiver_Test.mat");
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
            // Tolérance de 5 % : la houle a bougé les touffes du bord du champ entre les deux rendus
            report.Check(report.consoleErrors.Count == errorsBefore && visibleWithShadows >= visible * 0.95f,
                "Ombres activées : rendu sans erreur, l'extrusion vers la lumière garde au moins autant de touffes", $"{visible} -> {visibleWithShadows} visibles");
            int shadowPixels = CountShadowedPixels(receiverNoShadow, shadowed);
            report.Check(shadowPixels > ImageSize * ImageSize / 1000, "Ombres : les touffes ombrent le plan récepteur",
                $"{shadowPixels} px du récepteur assombris ({100.0 * shadowPixels / (ImageSize * ImageSize):F2} %)");
            SavePng(report, receiverNoShadow, "surface_recepteur_sans_ombres");
            SavePng(report, shadowed, "surface_ombres");

            // ---------------------------------------------------------------- 10. Messages des shaders après compilation réelle des variantes
            CheckShader(report, sargassumShader, "Nyx/Instancing/Sargassum (après rendu)");
            CheckShader(report, bubbleShader, "Nyx/Instancing/Bubble (après rendu)");

            // ---------------------------------------------------------------- 11. Chiffres (information)
            var watch = Stopwatch.StartNew();
            const int frames = 50;
            for (int i = 0; i < frames; i++)
                Render(surfaceCamera, readBack: false);
            watch.Stop();
            report.notes.Add($"Sargasses : {sargassum.InstanceCount} instances, {sargassum.DrawCallsPerCamera} draws, {sargassum.GpuMemoryBytes / 1024} Ko GPU (toutes caméras du test)");
            report.notes.Add($"Bulles : {bubbles.InstanceCount} instances, {bubbles.DrawCallsPerCamera} draws, {bubbles.GpuMemoryBytes / 1024} Ko GPU");
            report.notes.Add($"Rendu complet Cam_Surface (éditeur, CPU, culling + scène) : {watch.Elapsed.TotalMilliseconds / frames:F2} ms en moyenne sur {frames} images");

            surfaceCamera.enabled = true;   // la scène enregistrée s'ouvre sur cette vue

            // Caméra libre pour explorer la scène en Play mode, ajoutée après les mesures. Le script appartient au
            // projet de test, pas au package : recherche par nom, le package compile sans lui.
            Type freeCamera = Type.GetType("FreeCameraController, Assembly-CSharp");
            if (freeCamera != null)
                surfaceCamera.gameObject.AddComponent(freeCamera);
            report.notes.Add(freeCamera != null
                ? "Caméra libre : FreeCameraController ajouté à Cam_Surface (Play mode : clic droit + souris, ZQSD/WASD, A-Q/E, Maj, molette)"
                : "Caméra libre : script FreeCameraController absent du projet, non ajoutée");
            EditorSceneManager.SaveScene(scene, AssetFolder + "/SmokeTest.unity");
        }

        // ------------------------------------------------------------------------------------------
        // Vérifications
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
                report.Check(false, $"Compute {label} trouvé dans Resources", "Resources.Load a renvoyé null");
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

        // ------------------------------------------------------------------------------------------
        // Construction de la scène
        // ------------------------------------------------------------------------------------------

        static void SetVariants(SerializedProperty list, Mesh[][] lodMeshes, float lod1Distance, Material material)
        {
            list.arraySize = lodMeshes.Length;
            for (int v = 0; v < lodMeshes.Length; v++)
            {
                SerializedProperty variant = list.GetArrayElementAtIndex(v);
                SerializedProperty meshes = variant.FindPropertyRelative("lodMeshes");
                meshes.arraySize = lodMeshes[v].Length;
                for (int l = 0; l < lodMeshes[v].Length; l++)
                    meshes.GetArrayElementAtIndex(l).objectReferenceValue = lodMeshes[v][l];
                SerializedProperty distances = variant.FindPropertyRelative("lodDistances");
                distances.arraySize = 1;
                distances.GetArrayElementAtIndex(0).floatValue = lod1Distance;
                variant.FindPropertyRelative("material").objectReferenceValue = material;
                variant.FindPropertyRelative("subMeshIndex").intValue = 0;
                variant.FindPropertyRelative("cullDistance").floatValue = 0f;
            }
        }

        static void SetLayerThickness(SargassumField field, float thickness)
        {
            var settings = new SerializedObject(field);
            settings.FindProperty("layerThickness").floatValue = thickness;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Touffes visibles par 'camera' avec cette épaisseur de couche (reconstruction + rendu + relecture GPU).</summary>
        static int VisibleWithLayer(SargassumField field, Camera camera, float thickness, List<int> counts)
        {
            SetLayerThickness(field, thickness);
            field.RebuildNow();
            Render(camera, readBack: false);
            return field.TryReadVisibleCounts(camera, counts) ? Sum(counts) : 0;
        }

        static void SetEmitter(SerializedProperty emitter, Vector3 localPosition, int bubbleCount)
        {
            emitter.FindPropertyRelative("anchor").objectReferenceValue = null;
            emitter.FindPropertyRelative("localPosition").vector3Value = localPosition;
            emitter.FindPropertyRelative("radius").floatValue = 0.3f;
            emitter.FindPropertyRelative("bubbleCount").intValue = bubbleCount;
            emitter.FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            emitter.FindPropertyRelative("riseSpeed").vector2Value = new Vector2(0.22f, 0.32f);
            emitter.FindPropertyRelative("spiralRadius").floatValue = 0.015f;
            emitter.FindPropertyRelative("spiralFrequency").floatValue = 1.2f;
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

        /// <summary>Touffe de test : 'fronds' lanières qui rayonnent depuis le centre, légèrement relevées.</summary>
        static Mesh BuildClump(string name, int fronds, int segments, float length, float width)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (int f = 0; f < fronds; f++)
            {
                float angle = f * Mathf.PI * 2f / fronds;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var side = new Vector3(-direction.z, 0f, direction.x);
                int start = vertices.Count;
                for (int s = 0; s <= segments; s++)
                {
                    float t = (float)s / segments;
                    Vector3 center = direction * (t * length) + Vector3.up * (0.08f * Mathf.Sin(t * Mathf.PI));
                    vertices.Add(center - side * (width * 0.5f));
                    vertices.Add(center + side * (width * 0.5f));
                    normals.Add(Vector3.up);
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2(0f, t));
                    uvs.Add(new Vector2(1f, t));
                }
                for (int s = 0; s < segments; s++)
                {
                    // Sens horaire vu du dessus (convention Unity) : face avant du côté des normales (+Y)
                    int a = start + s * 2;
                    triangles.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Texture de fronde : brun doré, bords découpés en alpha (teste l'alpha-test).</summary>
        static Texture2D BuildFrondTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "FrondTexture" };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    float edge = 0.5f - Mathf.Abs(u - 0.5f);
                    float lobes = 0.35f + 0.15f * Mathf.Sin(v * 40f);
                    byte alpha = (byte)(edge * 2f > 1f - lobes ? 255 : 0);
                    byte shade = (byte)(200 + 40 * Mathf.Sin(v * 25f + u * 6f));
                    pixels[y * size + x] = new Color32(shade, (byte)(shade * 0.75f), (byte)(shade * 0.3f), alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        // ------------------------------------------------------------------------------------------
        // Rendu et images
        // ------------------------------------------------------------------------------------------

        static Texture2D Render(Camera camera, bool readBack = true)
        {
            RenderTexture target = RenderTexture.GetTemporary(ImageSize, ImageSize, 24, RenderTextureFormat.ARGB32);
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
            camera.targetTexture = null;
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

        /// <summary>
        /// Pixels du récepteur (teinte bleue, B > R) nettement plus sombres avec ombres que sans. Les touffes sont brunes
        /// (R > B) : une touffe déplacée par la houle entre les deux rendus n'est pas comptée comme une ombre.
        /// </summary>
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

        // ------------------------------------------------------------------------------------------
        // Rapport
        // ------------------------------------------------------------------------------------------

        static void WriteReport(Report report)
        {
            var text = new StringBuilder();
            text.AppendLine("# NyxInstancing : test de fumée");
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

    /// <summary>
    /// Lancement sans clic quand Unity est ouvert (pour un agent qui travaille en parallèle) :
    ///  - Logs/NyxSmokeTest/refresh.request : réimporte les fichiers modifiés (recompilation) ;
    ///  - Logs/NyxSmokeTest/run.request     : lance le test, sauf si la scène ouverte a des modifications non enregistrées.
    /// Vérifié une fois par seconde ; le fichier est supprimé avant d'agir.
    /// </summary>
    [InitializeOnLoad]
    static class NyxSmokeTestTrigger
    {
        static double s_NextPoll;

        static NyxSmokeTestTrigger() => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_NextPoll)
                return;
            s_NextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            string folder = NyxSmokeTest.OutputFolder;
            string refresh = Path.Combine(folder, "refresh.request");
            string run = Path.Combine(folder, "run.request");

            if (File.Exists(refresh))
            {
                File.Delete(refresh);
                AssetDatabase.Refresh();
                return;
            }
            if (!File.Exists(run))
                return;

            File.Delete(run);
            if (SceneManager.GetActiveScene().isDirty)
            {
                File.WriteAllText(Path.Combine(folder, "status.txt"),
                    "blocked : la scène ouverte a des modifications non enregistrées (enregistrer ou annuler dans Unity, puis relancer)");
                return;
            }
            NyxSmokeTest.Run();
        }
    }
}
