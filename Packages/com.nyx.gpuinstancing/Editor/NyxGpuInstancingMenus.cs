using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Création en un clic :
    ///  - GameObject > Nyx > GPU Instancing > Sargassum Field / Bubble Field / Ocean Waves / Demo Water ;
    ///  - Tools > Nyx > GPU Instancing > Create Demo Scene.
    /// Les champs créés ont leurs touffes, matériaux et compute shaders par défaut, et une houle (Ocean Waves) est ajoutée
    /// à la scène si elle n'en a pas : rien à régler pour voir le résultat.
    /// </summary>
    public static class NyxGpuInstancingMenus
    {
        const string GameObjectMenu = "GameObject/Nyx/GPU Instancing/";
        public const string DemoScenePath = "Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity";

        [MenuItem(GameObjectMenu + "Sargassum Field", false, 10)]
        static void CreateSargassumField(MenuCommand command)
        {
            EnsureOceanWaves();
            var field = Create<SargassumField>("Sargassum Field", command);
            field.ApplyEditorDefaults();
        }

        [MenuItem(GameObjectMenu + "Bubble Field", false, 11)]
        static void CreateBubbleField(MenuCommand command)
        {
            EnsureOceanWaves();
            var field = Create<BubbleField>("Bubble Field", command);
            field.ApplyEditorDefaults();
            // Une colonne de bulles a besoin d'eau au-dessus : champ posé sous la surface
            if (field.transform.position.y > OceanWaves.WaterLevel - 2f)
                field.transform.position = new Vector3(field.transform.position.x, OceanWaves.WaterLevel - 8f, field.transform.position.z);
        }

        [MenuItem(GameObjectMenu + "Ocean Waves", false, 12)]
        static void CreateOceanWaves(MenuCommand command)
        {
            OceanWaves existing = FindOceanWaves();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[Nyx GPU Instancing] La scène a déjà un Ocean Waves (un seul par scène).", existing);
                return;
            }
            Create<OceanWaves>("Ocean Waves", command);
        }

        [MenuItem(GameObjectMenu + "Demo Water", false, 30)]
        static void CreateDemoWater(MenuCommand command)
        {
            EnsureOceanWaves();
            GameObject water = CreateDemoWaterObject(OceanWaves.WaterLevel);
            GameObjectUtility.SetParentAndAlign(water, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(water, "Créer Demo Water");
            Selection.activeGameObject = water;
        }

        [MenuItem("Tools/Nyx/GPU Instancing/Create Demo Scene", false, 0)]
        static void CreateDemoSceneFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            NyxGpuInstancingDemo.Build(DemoScenePath);
        }

        /// <summary>Ajoute un Ocean Waves à la scène active s'il n'y en a pas (sinon l'eau est plate à Y = 0).</summary>
        public static OceanWaves EnsureOceanWaves()
        {
            OceanWaves waves = FindOceanWaves();
            if (waves != null)
                return waves;
            waves = ObjectFactory.CreateGameObject("Ocean Waves", typeof(OceanWaves)).GetComponent<OceanWaves>();
            return waves;
        }

        /// <summary>Surface d'eau de démo (grille + shader Demo Water) qui suit la houle des sargasses.</summary>
        public static GameObject CreateDemoWaterObject(float waterLevel)
        {
            var water = new GameObject("Demo Water");
            water.transform.position = new Vector3(0f, waterLevel, 0f);
            water.AddComponent<NyxDemoWater>();   // grille générée au chargement, aucun asset
            var renderer = water.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = NyxDefaultAssets.WaterMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return water;
        }

        static OceanWaves FindOceanWaves()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<OceanWaves>();
#else
            return Object.FindObjectOfType<OceanWaves>();
#endif
        }

        static T Create<T>(string name, MenuCommand command) where T : Component
        {
            GameObject gameObject = ObjectFactory.CreateGameObject(name);
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            if (command.context == null && SceneView.lastActiveSceneView != null)
                gameObject.transform.position = SceneView.lastActiveSceneView.pivot;
            var component = ObjectFactory.AddComponent<T>(gameObject);   // comme « Add Component » : Reset() reçoit les valeurs par défaut
            Undo.RegisterCreatedObjectUndo(gameObject, "Créer " + name);
            Selection.activeGameObject = gameObject;
            return component;
        }
    }

    /// <summary>
    /// Scène de démonstration : mer (houle + eau de démo), couche de sargasses avec amas au-dessus d'une mine et d'un leurre,
    /// colonnes de bulles et plongeur qui expire, caméra libre (clic droit + ZQSD, A/E, Maj, molette).
    /// </summary>
    public static class NyxGpuInstancingDemo
    {
        public sealed class Contents
        {
            public Scene scene;
            public Light sun;
            public OceanWaves waves;
            public GameObject water;
            public SargassumField sargassum;
            public BubbleField bubbles;
            public Transform mine;
            public Transform decoy;
            public Camera camera;
        }

        /// <summary>Construit la scène (nouvelle scène, assets par défaut) et l'enregistre à 'scenePath' si non nul.</summary>
        public static Contents Build(string scenePath)
        {
            NyxDefaultAssets.EnsureAll();
            var demo = new Contents { scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single) };

            demo.sun = new GameObject("Sun").AddComponent<Light>();
            demo.sun.type = LightType.Directional;
            demo.sun.intensity = 1.1f;
            demo.sun.shadows = LightShadows.Soft;
            demo.sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.sun = demo.sun;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;   // une scène vide n'a pas de sonde ambiante calculée
            RenderSettings.ambientLight = new Color(0.30f, 0.36f, 0.42f);

            demo.waves = new GameObject("Ocean Waves").AddComponent<OceanWaves>();
            demo.water = NyxGpuInstancingMenus.CreateDemoWaterObject(OceanWaves.WaterLevel);

            // Mine sous la surface (son point d'ancrage au repos sert aux amas d'algues) et leurre sans mine
            demo.mine = new GameObject("Mine (ancre)").transform;
            demo.mine.position = new Vector3(5f, 0f, 10f);
            GameObject mineBody = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mineBody.name = "Mine";
            Object.DestroyImmediate(mineBody.GetComponent<Collider>());
            mineBody.transform.SetParent(demo.mine, false);
            mineBody.transform.localPosition = new Vector3(0f, -1.6f, 0f);
            mineBody.transform.localScale = Vector3.one * 1.1f;
            demo.decoy = new GameObject("Leurre (ancre)").transform;
            demo.decoy.position = new Vector3(-12f, 0f, 0f);

            demo.sargassum = new GameObject("Sargassum Field").AddComponent<SargassumField>();
            demo.sargassum.ApplyEditorDefaults();
            var sargassum = new SerializedObject(demo.sargassum);
            sargassum.FindProperty("areaSize").vector2Value = new Vector2(80f, 80f);
            sargassum.FindProperty("densityPer100m2").floatValue = 6f;
            SerializedProperty anchors = sargassum.FindProperty("clusterAnchors");
            anchors.arraySize = 2;
            anchors.GetArrayElementAtIndex(0).objectReferenceValue = demo.mine;
            anchors.GetArrayElementAtIndex(1).objectReferenceValue = demo.decoy;
            sargassum.FindProperty("clusterRadius").floatValue = 4f;
            sargassum.FindProperty("clumpsPerCluster").intValue = 30;
            sargassum.FindProperty("maxDrawDistance").floatValue = 200f;
            sargassum.FindProperty("layerThickness").floatValue = 0.4f;
            sargassum.ApplyModifiedPropertiesWithoutUndo();
            demo.sargassum.RebuildNow();

            demo.bubbles = new GameObject("Bubble Field").AddComponent<BubbleField>();
            demo.bubbles.transform.position = new Vector3(0f, -10f, 0f);
            demo.bubbles.ApplyEditorDefaults();
            var bubbles = new SerializedObject(demo.bubbles);
            SerializedProperty emitters = bubbles.FindProperty("emitters");
            Vector3[] columns = { new Vector3(-2f, 0f, 8f), new Vector3(0f, 0f, 10f), new Vector3(2.5f, 0f, 9f) };
            emitters.arraySize = columns.Length;
            for (int i = 0; i < columns.Length; i++)
            {
                SerializedProperty emitter = emitters.GetArrayElementAtIndex(i);
                emitter.FindPropertyRelative("anchor").objectReferenceValue = null;
                emitter.FindPropertyRelative("localPosition").vector3Value = columns[i];
                emitter.FindPropertyRelative("radius").floatValue = 0.3f;
                emitter.FindPropertyRelative("bubbleCount").intValue = 60;
                emitter.FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            }
            bubbles.FindProperty("burstCapacity").intValue = 512;
            bubbles.FindProperty("burstSettings").FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            bubbles.FindProperty("maxDrawDistance").floatValue = 60f;
            bubbles.ApplyModifiedPropertiesWithoutUndo();
            demo.bubbles.RebuildNow();

            // Plongeur immobile qui expire : exemple de source mobile (BubbleField.Emit)
            GameObject diver = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            diver.name = "Plongeur (Bubble Burst Emitter)";
            Object.DestroyImmediate(diver.GetComponent<Collider>());
            diver.transform.SetPositionAndRotation(new Vector3(-3f, -6f, 6f), Quaternion.Euler(70f, 20f, 0f));
            diver.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            var burst = new SerializedObject(diver.AddComponent<BubbleBurstEmitter>());
            burst.FindProperty("field").objectReferenceValue = demo.bubbles;
            burst.ApplyModifiedPropertiesWithoutUndo();

            demo.camera = new GameObject("Main Camera").AddComponent<Camera>();
            demo.camera.tag = "MainCamera";
            demo.camera.clearFlags = CameraClearFlags.SolidColor;
            demo.camera.backgroundColor = new Color(0.55f, 0.70f, 0.85f);
            demo.camera.nearClipPlane = 0.1f;
            demo.camera.farClipPlane = 500f;
            demo.camera.transform.position = new Vector3(0f, 6f, -45f);
            demo.camera.transform.LookAt(new Vector3(0f, 0f, -5f));
            demo.camera.gameObject.AddComponent<NyxFlyCamera>();
            demo.camera.gameObject.AddComponent<AudioListener>();

            if (!string.IsNullOrEmpty(scenePath))
            {
                string folder = System.IO.Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/'), System.IO.Path.GetFileName(folder));
                EditorSceneManager.SaveScene(demo.scene, scenePath);
            }
            return demo;
        }
    }
}
