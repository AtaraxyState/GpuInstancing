using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Inspecteur du MeshSpawner : choix du comportement dans une liste (tous les MeshSpawnerBehavior du projet, y compris
    /// ceux écrits pour le projet), placement masqué si le comportement fournit ses propres instances, chiffres.
    /// Vue Scene : volume de la zone, sommets à déplacer (Ctrl+clic : supprimer), points au milieu des arêtes (clic :
    /// insérer un sommet), poignées du centre et de la profondeur.
    /// </summary>
    [CustomEditor(typeof(MeshSpawner))]
    sealed class MeshSpawnerEditor : UnityEditor.Editor
    {
        static readonly string[] PlacementFields =
        {
            "source", "placements", "randomSeed", "areaSize", "densityPer100m2", "scaleRange", "clusterAnchors", "clusterRadius", "clumpsPerCluster"
        };

        static readonly Color ClipColor = new Color(1f, 0.55f, 0.1f);
        static readonly Color ZoneColor = new Color(0.3f, 0.8f, 1f);

        public override void OnInspectorGUI()
        {
            var spawner = (MeshSpawner)target;
            DrawBehaviorPicker(spawner);
            EditorGUILayout.Space();

            serializedObject.Update();
            MeshSpawnerBehavior behavior = spawner.Behavior;
            if (behavior == null || behavior.UsesPlacement)
            {
                DrawPropertiesExcluding(serializedObject, "m_Script");
            }
            else
            {
                DrawPropertiesExcluding(serializedObject, PlacementFields.Append("m_Script").ToArray());
                EditorGUILayout.HelpBox($"Placement fourni par {ObjectNames.NicifyVariableName(behavior.GetType().Name)} (réglages du comportement). " +
                                        "La zone ne sert qu'à détruire les instances qui en sortent (Clip To Volume).", MessageType.None);
            }
            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
                DrawZoneHelp(spawner, behavior);

            if (targets.Length == 1 && spawner.InstanceCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{spawner.InstanceCount} instances, {spawner.DrawCallsPerCamera} draw(s) par caméra, {spawner.GpuMemoryBytes / 1024} Ko GPU.",
                    MessageType.Info);
            }
        }

        void DrawZoneHelp(MeshSpawner spawner, MeshSpawnerBehavior behavior)
        {
            SerializedProperty points = serializedObject.FindProperty("zonePoints");
            if (points.arraySize < 3)
            {
                EditorGUILayout.HelpBox("Zone : moins de 3 sommets, le rectangle de l'ancienne taille de zone est utilisé. " +
                                        "Le convertir pour le modifier (ou déplacer un de ses sommets dans la vue Scene).", MessageType.Info);
                if (GUILayout.Button("Convertir la zone en polygone"))
                    WriteZonePoints(points, spawner.ZonePoints);
            }
            if (points.arraySize > MeshSpawner.SmoothZonePoints)
            {
                // Coût mesuré du test de zone (GTX 1060) : environ 0,008 ms par sommet pour 1 000 000 d'instances
                float perFrame = 0.008f * points.arraySize * spawner.InstanceCount / 1e6f;
                EditorGUILayout.HelpBox(
                    $"{points.arraySize} sommets : le test de la zone coûte environ {0.008f * points.arraySize:F1} ms GPU pour 1 000 000 d'instances " +
                    (spawner.ClipToVolume ? $"à chaque image (ici ≈ {perFrame:F2} ms par caméra, Clip To Volume)" : "au placement seulement (Clip To Volume coupé)") +
                    ". Pour un contour naturel, quelques points + Zone Smooth / Edge Noise / Edge Feather coûtent comme 64 sommets." +
                    (points.arraySize > MaxHandledPoints ? $" Au-delà de {MaxHandledPoints} sommets, la vue Scene ne montre que le contour (pas de poignées)." : ""),
                    MessageType.Info);
            }
            if (spawner.MotionExceedsVolumeHeight)
            {
                EditorGUILayout.HelpBox("Le mouvement des instances dépasse la hauteur du volume : celles qui en sortent sont détruites " +
                                        "(jusqu'à la prochaine reconstruction). Monter la face du haut ou augmenter la profondeur.", MessageType.Warning);
                if (behavior is FloatOnWaves floating && GUILayout.Button("Poser le volume sur l'eau (crêtes, creux et couche compris)"))
                {
                    Undo.RecordObject(spawner, "Zone sur l'eau");
                    floating.FitZoneToWater();
                    EditorUtility.SetDirty(spawner);
                }
            }
        }

        void OnSceneGUI()
        {
            var spawner = (MeshSpawner)target;
            MeshSpawnerBehavior behavior = spawner.Behavior;
            if (behavior != null && !behavior.UsesPlacement && !spawner.ClipToVolume)
                return;   // zone inutilisée : ni placement, ni destruction

            serializedObject.Update();
            Vector3 top = spawner.ZoneCenter;
            float depth = spawner.ZoneDepth;
            var points = new List<Vector2>(spawner.ZonePoints);   // rectangle de l'ancienne zone si moins de 3 sommets
            int count = points.Count;
            var topRing = new Vector3[count + 1];
            var bottomRing = new Vector3[count + 1];
            for (int i = 0; i <= count; i++)
            {
                Vector2 p = points[i % count];
                topRing[i] = top + new Vector3(p.x, 0f, p.y);
                bottomRing[i] = topRing[i] + Vector3.down * depth;
            }

            Color color = spawner.ClipToVolume ? ClipColor : ZoneColor;
            Handles.color = color;
            bool shaped = spawner.ZoneSmooth || spawner.ZoneEdgeNoise > 0f || PlacementFeather(spawner) > 0f;
            if (shaped)
            {
                // Points de contrôle en pointillés ; contour courbe et bord naturel en traits pleins
                for (int i = 0; i < count; i++)
                    Handles.DrawDottedLine(topRing[i], topRing[i + 1], 3f);
                DrawShapedOutline(spawner, top, depth, color);
            }
            else
            {
                Handles.DrawAAPolyLine(3f, topRing);
                Handles.DrawPolyLine(bottomRing);
                for (int i = 0; i < count; i += Mathf.Max(1, count / 64))   // arêtes verticales : 64 au plus
                    Handles.DrawDottedLine(topRing[i], bottomRing[i], 4f);
            }
            Handles.color = color;

            Event e = Event.current;
            var plane = new Plane(Vector3.up, top);
            bool changed = false;
            int remove = -1;

            // Sommets : glisser sur le plan de la face du haut ; Ctrl+clic : supprimer (3 sommets minimum)
            for (int i = 0; i < count && count <= MaxHandledPoints; i++)
            {
                Vector3 world = topRing[i];
                float size = HandleUtility.GetHandleSize(world) * 0.07f;
                int id = GUIUtility.GetControlID(FocusType.Passive);
                if (e.type == EventType.MouseDown && e.button == 0 && (e.control || e.command) && HandleUtility.nearestControl == id)
                {
                    if (count > 3)
                        remove = i;
                    e.Use();
                    continue;
                }
                EditorGUI.BeginChangeCheck();
                Handles.FreeMoveHandle(id, world, Quaternion.identity, size, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck() && plane.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out float distance))
                {
                    Vector3 hit = HandleUtility.GUIPointToWorldRay(e.mousePosition).GetPoint(distance);
                    points[i] = new Vector2(hit.x - top.x, hit.z - top.z);
                    changed = true;
                }
            }
            if (remove >= 0)
            {
                points.RemoveAt(remove);
                changed = true;
            }

            // Milieux des arêtes : clic = nouveau sommet
            if (!changed && count < MeshSpawner.MaxZonePoints && count <= MaxHandledPoints)
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 middle = (topRing[i] + topRing[i + 1]) * 0.5f;
                    float size = HandleUtility.GetHandleSize(middle) * 0.045f;
                    if (Handles.Button(middle, Quaternion.identity, size, size * 1.5f, Handles.CircleHandleCap))
                    {
                        points.Insert(i + 1, (points[i] + points[(i + 1) % count]) * 0.5f);
                        changed = true;
                        break;
                    }
                }
            }

            // Centre de la face du haut et profondeur
            SerializedProperty centerProperty = serializedObject.FindProperty("zoneCenter");
            SerializedProperty depthProperty = serializedObject.FindProperty("zoneDepth");
            EditorGUI.BeginChangeCheck();
            Vector3 movedTop = Handles.PositionHandle(top, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
                centerProperty.vector3Value = movedTop - spawner.transform.position;
            Vector3 bottom = top + Vector3.down * depth;
            EditorGUI.BeginChangeCheck();
            Vector3 movedBottom = Handles.Slider(bottom, Vector3.down, HandleUtility.GetHandleSize(bottom) * 0.12f, Handles.CubeHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
                depthProperty.floatValue = Mathf.Max(0f, top.y - movedBottom.y);

            if (changed)
                WriteZonePoints(serializedObject.FindProperty("zonePoints"), points);
            serializedObject.ApplyModifiedProperties();   // annulable (Ctrl+Z), reconstruction via OnValidate
        }

        // Bord naturel : contour calculé par carrés marchants (même fonction que le GPU), recalculé seulement si la zone change
        static string s_shapeKey;
        static readonly List<Vector3> s_edgeLines = new List<Vector3>();
        static readonly List<Vector3> s_featherLines = new List<Vector3>();
        const int ContourResolution = 160;

        /// <summary>Au-delà, pas de poignée par sommet dans la vue Scene (trop lourd à dessiner) : liste ou SetZone.</summary>
        const int MaxHandledPoints = 256;

        /// <summary>
        /// Contour courbe (haut et bas du volume), bord naturel déplacé par le bruit (trait épais) et limite intérieure de la
        /// lisière (pointillés : au-delà, densité pleine).
        /// </summary>
        static void DrawShapedOutline(MeshSpawner spawner, Vector3 top, float depth, Color color)
        {
            List<Vector2> outline = spawner.WorldOutline();
            var topRing = new Vector3[outline.Count + 1];
            var bottomRing = new Vector3[outline.Count + 1];
            for (int i = 0; i <= outline.Count; i++)
            {
                Vector2 p = outline[i % outline.Count];
                topRing[i] = new Vector3(p.x, top.y, p.y);
                bottomRing[i] = topRing[i] + Vector3.down * depth;
            }
            float noise = spawner.ZoneEdgeNoise;
            float feather = PlacementFeather(spawner);
            Handles.color = noise > 0f ? new Color(color.r, color.g, color.b, 0.45f) : color;
            Handles.DrawAAPolyLine(noise > 0f ? 1.5f : 3f, topRing);
            Handles.color = color;
            Handles.DrawPolyLine(bottomRing);
            if (noise <= 0f && feather <= 0f)
                return;

            var key = new System.Text.StringBuilder();
            foreach (Vector2 p in outline)
                key.Append(p.x.ToString("R")).Append(',').Append(p.y.ToString("R")).Append(';');
            key.Append(noise).Append('|').Append(spawner.ZoneEdgeNoiseScale).Append('|').Append(feather).Append('|')
               .Append(spawner.ZoneCenter.x).Append(',').Append(spawner.ZoneCenter.z);
            if (key.ToString() != s_shapeKey)
            {
                s_shapeKey = key.ToString();
                var bounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                foreach (Vector2 p in outline)
                    bounds = new Vector4(Mathf.Min(bounds.x, p.x), Mathf.Min(bounds.y, p.y), Mathf.Max(bounds.z, p.x), Mathf.Max(bounds.w, p.y));
                float margin = noise + 1f;
                bounds += new Vector4(-margin, -margin, margin, margin);
                Fill(s_edgeLines, ZoneShape.Contour(p => spawner.ZoneEdgeAt(p, outline, false), bounds, ContourResolution));
                s_featherLines.Clear();
                if (feather > 0f)
                    Fill(s_featherLines, ZoneShape.Contour(p => spawner.ZoneEdgeAt(p, outline, true) + feather, bounds, ContourResolution));
            }
            float y = top.y;
            Handles.color = color;
            if (noise > 0f)
                Handles.DrawLines(Lift(s_edgeLines, y));
            if (s_featherLines.Count > 0)
            {
                Vector3[] feathered = Lift(s_featherLines, y);
                for (int i = 0; i + 1 < feathered.Length; i += 2)
                    Handles.DrawDottedLine(feathered[i], feathered[i + 1], 2f);
            }
        }

        /// <summary>Lisière qui agit sur le placement : aucune pour une liste (placement figé compris).</summary>
        static float PlacementFeather(MeshSpawner spawner) =>
            spawner.Source == MeshSpawner.PlacementSource.List ? 0f : spawner.ZoneEdgeFeather;

        static void Fill(List<Vector3> lines, List<Vector2> segments)
        {
            lines.Clear();
            foreach (Vector2 p in segments)
                lines.Add(new Vector3(p.x, 0f, p.y));
        }

        static Vector3[] Lift(List<Vector3> lines, float y)
        {
            var result = new Vector3[lines.Count];
            for (int i = 0; i < lines.Count; i++)
                result[i] = new Vector3(lines[i].x, y, lines[i].z);
            return result;
        }

        static void WriteZonePoints(SerializedProperty property, IReadOnlyList<Vector2> points)
        {
            property.arraySize = points.Count;
            for (int i = 0; i < points.Count; i++)
                property.GetArrayElementAtIndex(i).vector2Value = points[i];
            property.serializedObject.ApplyModifiedProperties();
        }

        static void DrawBehaviorPicker(MeshSpawner spawner)
        {
            List<Type> types = MeshSpawnerTools.BehaviorTypes;
            MeshSpawnerBehavior[] current = spawner.GetComponents<MeshSpawnerBehavior>();
            int index = current.Length > 0 ? types.IndexOf(current[0].GetType()) + 1 : 0;

            var labels = new GUIContent[types.Count + 1];
            labels[0] = new GUIContent("(aucun)");
            for (int i = 0; i < types.Count; i++)
                labels[i + 1] = new GUIContent(ObjectNames.NicifyVariableName(types[i].Name), types[i].FullName);

            int chosen = EditorGUILayout.Popup(new GUIContent("Comportement", "Comment les instances bougent. Liste : toutes les classes " +
                                                                              "qui héritent de MeshSpawnerBehavior dans le projet."), index, labels);
            if (chosen != index)
                MeshSpawnerTools.SetBehavior(spawner, chosen == 0 ? null : types[chosen - 1]);

            if (current.Length == 0)
                EditorGUILayout.HelpBox("Aucun comportement : rien n'est dessiné. En choisir un dans la liste.", MessageType.Warning);
            else if (current.Length > 1)
                EditorGUILayout.HelpBox("Plusieurs comportements sur l'objet : seul le premier activé est utilisé.", MessageType.Warning);

            MeshSpawnerBehavior behavior = spawner.Behavior;
            if (behavior != null && GUILayout.Button(new GUIContent("Meshes par défaut du comportement",
                    "Remplace les variantes par les meshes et le matériau fournis avec ce comportement.")))
                MeshSpawnerTools.ApplyDefaultVariants(spawner, behavior, replace: true);
        }
    }

    /// <summary>Opérations d'édition sur les MeshSpawner, utilisables par script (annulables avec Ctrl+Z).</summary>
    public static class MeshSpawnerTools
    {
        static List<Type> s_BehaviorTypes;

        /// <summary>Comportements disponibles : classes non abstraites qui héritent de MeshSpawnerBehavior, triées par nom.</summary>
        public static List<Type> BehaviorTypes => s_BehaviorTypes ??= TypeCache.GetTypesDerivedFrom<MeshSpawnerBehavior>()
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition)
            .OrderBy(type => type.Name)
            .ToList();

        /// <summary>
        /// Remplace le comportement du spawner par 'type' (null = aucun). Le nouveau comportement donne ses meshes par
        /// défaut si le spawner n'a pas de variantes, ou toujours si 'replaceVariants'.
        /// </summary>
        public static MeshSpawnerBehavior SetBehavior(MeshSpawner spawner, Type type, bool replaceVariants = false)
        {
            Undo.SetCurrentGroupName("Changer le comportement");
            int group = Undo.GetCurrentGroup();
            foreach (MeshSpawnerBehavior existing in spawner.GetComponents<MeshSpawnerBehavior>())
                Undo.DestroyObjectImmediate(existing);

            MeshSpawnerBehavior behavior = null;
            if (type != null)
            {
                behavior = (MeshSpawnerBehavior)ObjectFactory.AddComponent(spawner.gameObject, type);   // Reset() : meshes par défaut si aucun
                if (replaceVariants)
                    ApplyDefaultVariants(spawner, behavior, replace: true);
            }
            Undo.CollapseUndoOperations(group);
            spawner.SetDirty();
            return behavior;
        }

        /// <summary>Donne au spawner les meshes et le matériau par défaut du comportement (toujours si 'replace', sinon s'il n'en a pas).</summary>
        public static void ApplyDefaultVariants(MeshSpawner spawner, MeshSpawnerBehavior behavior, bool replace)
        {
            if (!replace && spawner.Variants.Count > 0)
                return;
            List<InstanceVariant> defaults = behavior.CreateDefaultVariants();
            if (defaults == null || defaults.Count == 0)
            {
                Debug.LogWarning($"[Nyx GPU Instancing] {behavior.GetType().Name} ne fournit pas de meshes par défaut.", behavior);
                return;
            }
            Undo.RecordObject(spawner, "Meshes par défaut");
            spawner.SetVariants(defaults);
            EditorUtility.SetDirty(spawner);
        }

        /// <summary>Crée un objet avec un MeshSpawner et le comportement T (meshes et matériau par défaut).</summary>
        public static T Create<T>(string name, GameObject parent = null) where T : MeshSpawnerBehavior
        {
            GameObject gameObject = ObjectFactory.CreateGameObject(name, typeof(MeshSpawner));
            if (parent != null)
                GameObjectUtility.SetParentAndAlign(gameObject, parent);
            var behavior = ObjectFactory.AddComponent<T>(gameObject);
            ApplyDefaultVariants(gameObject.GetComponent<MeshSpawner>(), behavior, replace: false);
            return behavior;
        }
    }
}
