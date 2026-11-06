using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Inspecteur du MeshSpawner : choix du comportement dans une liste (tous les MeshSpawnerBehavior du projet, y compris
    /// ceux écrits pour le projet), placement masqué si le comportement fournit ses propres instances, aide sur la zone,
    /// chiffres. Vue Scene : <see cref="ZoneHandles"/>.
    /// </summary>
    [CustomEditor(typeof(MeshSpawner))]
    sealed class MeshSpawnerEditor : UnityEditor.Editor
    {
        static readonly string[] PlacementFields =
        {
            "source", "placements", "randomSeed", "areaSize", "densityPer100m2", "scaleRange", "clusterAnchors", "clusterRadius", "clumpsPerCluster"
        };

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

        void OnSceneGUI() => ZoneHandles.Draw(serializedObject, (MeshSpawner)target);

        void DrawZoneHelp(MeshSpawner spawner, MeshSpawnerBehavior behavior)
        {
            SerializedProperty points = serializedObject.FindProperty("zonePoints");
            if (points.arraySize < 3)
            {
                EditorGUILayout.HelpBox("Zone : moins de 3 sommets, le rectangle de l'ancienne taille de zone est utilisé. " +
                                        "Le convertir pour le modifier (ou déplacer un de ses sommets dans la vue Scene).", MessageType.Info);
                if (GUILayout.Button("Convertir la zone en polygone"))
                    ZoneHandles.WriteZonePoints(points, spawner.ZonePoints);
            }
            if (points.arraySize > MeshSpawner.SmoothZonePoints)
            {
                // Coût mesuré du test de zone (GTX 1060) : environ 0,008 ms par sommet pour 1 000 000 d'instances
                float perFrame = 0.008f * points.arraySize * spawner.InstanceCount / 1e6f;
                EditorGUILayout.HelpBox(
                    $"{points.arraySize} sommets : le test de la zone coûte environ {0.008f * points.arraySize:F1} ms GPU pour 1 000 000 d'instances " +
                    (spawner.ClipToVolume ? $"à chaque image (ici ≈ {perFrame:F2} ms par caméra, Clip To Volume)" : "au placement seulement (Clip To Volume coupé)") +
                    ". Pour un contour naturel, quelques points + Zone Smooth / Edge Noise / Edge Feather coûtent comme 64 sommets." +
                    (points.arraySize > ZoneHandles.MaxHandledPoints ? $" Au-delà de {ZoneHandles.MaxHandledPoints} sommets, la vue Scene ne montre que le contour (pas de poignées)." : ""),
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
}
