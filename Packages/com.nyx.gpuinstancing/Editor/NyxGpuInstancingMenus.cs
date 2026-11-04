using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Création en un clic :
    ///  - GameObject > Nyx > GPU Instancing > Sargassum / Bubbles / Static Meshes : un MeshSpawner avec son comportement
    ///    (Float On Waves, Bubble Columns, Static Instances), ses meshes et son matériau par défaut ;
    ///  - GameObject > Nyx > GPU Instancing > Ocean Waves / Demo Water ;
    ///  - Tools > Nyx > GPU Instancing > Create Demo Scene.
    /// Une houle (Ocean Waves) est ajoutée à la scène si elle n'en a pas : rien à régler pour voir le résultat.
    /// </summary>
    public static class NyxGpuInstancingMenus
    {
        const string GameObjectMenu = "GameObject/Nyx/GPU Instancing/";
        public const string DemoScenePath = "Assets/NyxGpuInstancingDemo/NyxGpuInstancingDemo.unity";

        [MenuItem(GameObjectMenu + "Sargassum (Float On Waves)", false, 10)]
        static void CreateSargassum(MenuCommand command)
        {
            EnsureOceanWaves();
            Select(MeshSpawnerTools.Create<FloatOnWaves>("Sargassum", command.context as GameObject), command);
        }

        [MenuItem(GameObjectMenu + "Bubbles (Bubble Columns)", false, 11)]
        static void CreateBubbles(MenuCommand command)
        {
            EnsureOceanWaves();
            var bubbles = MeshSpawnerTools.Create<BubbleColumns>("Bubbles", command.context as GameObject);
            Select(bubbles, command);
            // Une colonne de bulles a besoin d'eau au-dessus : objet posé sous la surface
            Transform t = bubbles.transform;
            if (t.position.y > OceanWaves.WaterLevel - 2f)
                t.position = new Vector3(t.position.x, OceanWaves.WaterLevel - 8f, t.position.z);
        }

        [MenuItem(GameObjectMenu + "Static Meshes (Static Instances)", false, 12)]
        static void CreateStatic(MenuCommand command) =>
            Select(MeshSpawnerTools.Create<StaticInstances>("Static Meshes", command.context as GameObject), command);

        [MenuItem(GameObjectMenu + "Ocean Waves", false, 30)]
        static void CreateOceanWaves(MenuCommand command)
        {
            OceanWaves existing = FindOceanWaves();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[Nyx GPU Instancing] La scène a déjà un Ocean Waves (un seul par scène).", existing);
                return;
            }
            GameObject waves = ObjectFactory.CreateGameObject("Ocean Waves", typeof(OceanWaves));
            GameObjectUtility.SetParentAndAlign(waves, command.context as GameObject);
            Selection.activeGameObject = waves;
        }

        [MenuItem(GameObjectMenu + "Demo Water", false, 31)]
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
            return ObjectFactory.CreateGameObject("Ocean Waves", typeof(OceanWaves)).GetComponent<OceanWaves>();
        }

        /// <summary>Surface d'eau de démo (grille + shader Demo Water) qui suit la houle.</summary>
        public static GameObject CreateDemoWaterObject(float waterLevel)
        {
            var water = new GameObject("Demo Water");
            water.transform.position = new Vector3(0f, waterLevel, 0f);
            water.AddComponent<NyxDemoWater>().Material = NyxDefaultAssets.WaterMaterial;   // grille générée au chargement, aucun asset
            return water;
        }

        static void Select(Component created, MenuCommand command)
        {
            if (command.context == null && SceneView.lastActiveSceneView != null)
                created.transform.position = SceneView.lastActiveSceneView.pivot;
            Selection.activeGameObject = created.gameObject;
        }

        static OceanWaves FindOceanWaves()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<OceanWaves>();
#else
            return Object.FindObjectOfType<OceanWaves>();
#endif
        }
    }

    /// <summary>
    /// Scène de démonstration : mer (houle + eau de démo), sargasses avec amas au-dessus d'une mine et d'un leurre
    /// (Float On Waves), colonnes de bulles et plongeur qui expire (Bubble Columns), rochers sur le fond (Static Instances),
    /// caméra libre (clic droit + ZQSD, A/E, Maj, molette).
    /// </summary>
    public static class NyxGpuInstancingDemo
    {
        public sealed class Contents
        {
            public Scene scene;
            public Light sun;
            public OceanWaves waves;
            public GameObject water;
            public MeshSpawner sargassum;
            public MeshSpawner bubbles;
            public MeshSpawner rocks;
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

            GameObject seabed = GameObject.CreatePrimitive(PrimitiveType.Plane);
            seabed.name = "Fond marin";
            Object.DestroyImmediate(seabed.GetComponent<Collider>());
            seabed.transform.position = new Vector3(0f, -12f, 0f);
            seabed.transform.localScale = new Vector3(16f, 1f, 16f);
            seabed.GetComponent<MeshRenderer>().sharedMaterial = NyxDefaultAssets.SeabedMaterial;

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

            // Sargasses : MeshSpawner + Float On Waves
            demo.sargassum = AddSpawner<FloatOnWaves>("Sargasses (Float On Waves)", Vector3.zero, out FloatOnWaves floating);
            var spawner = new SerializedObject(demo.sargassum);
            spawner.FindProperty("areaSize").vector2Value = new Vector2(80f, 80f);
            spawner.FindProperty("densityPer100m2").floatValue = 6f;
            SerializedProperty anchors = spawner.FindProperty("clusterAnchors");
            anchors.arraySize = 2;
            anchors.GetArrayElementAtIndex(0).objectReferenceValue = demo.mine;
            anchors.GetArrayElementAtIndex(1).objectReferenceValue = demo.decoy;
            spawner.FindProperty("clusterRadius").floatValue = 4f;
            spawner.FindProperty("clumpsPerCluster").intValue = 30;
            spawner.FindProperty("maxDrawDistance").floatValue = 200f;
            spawner.ApplyModifiedPropertiesWithoutUndo();
            var floatSettings = new SerializedObject(floating);
            floatSettings.FindProperty("layerThickness").floatValue = 0.4f;
            floatSettings.ApplyModifiedPropertiesWithoutUndo();
            demo.sargassum.RebuildNow();

            // Bulles : MeshSpawner + Bubble Columns
            demo.bubbles = AddSpawner<BubbleColumns>("Bulles (Bubble Columns)", new Vector3(0f, -10f, 0f), out BubbleColumns columns);
            var bubbles = new SerializedObject(columns);
            SerializedProperty emitters = bubbles.FindProperty("emitters");
            Vector3[] positions = { new Vector3(-2f, 0f, 8f), new Vector3(0f, 0f, 10f), new Vector3(2.5f, 0f, 9f) };
            emitters.arraySize = positions.Length;
            for (int i = 0; i < positions.Length; i++)
            {
                SerializedProperty emitter = emitters.GetArrayElementAtIndex(i);
                emitter.FindPropertyRelative("anchor").objectReferenceValue = null;
                emitter.FindPropertyRelative("localPosition").vector3Value = positions[i];
                emitter.FindPropertyRelative("radius").floatValue = 0.3f;
                emitter.FindPropertyRelative("bubbleCount").intValue = 60;
                emitter.FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            }
            bubbles.FindProperty("burstCapacity").intValue = 512;
            bubbles.FindProperty("burstSettings").FindPropertyRelative("surfaceRadius").vector2Value = new Vector2(0.012f, 0.03f);
            bubbles.ApplyModifiedPropertiesWithoutUndo();
            SetFloat(demo.bubbles, "maxDrawDistance", 60f);
            demo.bubbles.RebuildNow();

            // Rochers sur le fond : MeshSpawner + Static Instances
            demo.rocks = AddSpawner<StaticInstances>("Rochers (Static Instances)", new Vector3(0f, -12f, 0f), out _);
            var rocks = new SerializedObject(demo.rocks);
            rocks.FindProperty("areaSize").vector2Value = new Vector2(70f, 70f);
            rocks.FindProperty("densityPer100m2").floatValue = 4f;
            rocks.FindProperty("scaleRange").vector2Value = new Vector2(0.6f, 2.2f);
            rocks.FindProperty("castShadows").enumValueIndex = (int)ShadowCastingMode.On;
            rocks.ApplyModifiedPropertiesWithoutUndo();
            demo.rocks.RebuildNow();

            // Plongeur immobile qui expire : exemple de source mobile (BubbleColumns.Emit)
            GameObject diver = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            diver.name = "Plongeur (Bubble Burst Emitter)";
            Object.DestroyImmediate(diver.GetComponent<Collider>());
            diver.transform.SetPositionAndRotation(new Vector3(-3f, -6f, 6f), Quaternion.Euler(70f, 20f, 0f));
            diver.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            var burst = new SerializedObject(diver.AddComponent<BubbleBurstEmitter>());
            burst.FindProperty("field").objectReferenceValue = columns;
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
                // Les ajouts de comportement enregistrent un Undo, appliqué en fin de frame : l'appliquer avant d'enregistrer,
                // sinon la scène repasse « modifiée » juste après la sauvegarde.
                Undo.FlushUndoRecordObjects();
                string folder = System.IO.Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/'), System.IO.Path.GetFileName(folder));
                EditorSceneManager.SaveScene(demo.scene, scenePath);
            }
            return demo;
        }

        /// <summary>Objet avec MeshSpawner + comportement T et ses meshes par défaut (sans Undo : scène neuve).</summary>
        static MeshSpawner AddSpawner<T>(string name, Vector3 position, out T behavior) where T : MeshSpawnerBehavior
        {
            var gameObject = new GameObject(name);
            gameObject.transform.position = position;
            var spawner = gameObject.AddComponent<MeshSpawner>();
            behavior = gameObject.AddComponent<T>();
            spawner.SetVariants(behavior.CreateDefaultVariants());
            return spawner;
        }

        static void SetFloat(Object target, string property, float value)
        {
            var settings = new SerializedObject(target);
            settings.FindProperty(property).floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
