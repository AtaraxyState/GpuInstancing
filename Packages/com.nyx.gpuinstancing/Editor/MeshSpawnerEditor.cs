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
                EditorGUILayout.HelpBox($"Placement fourni par {ObjectNames.NicifyVariableName(behavior.GetType().Name)} (réglages du comportement).", MessageType.None);
            }
            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1 && spawner.InstanceCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{spawner.InstanceCount} instances, {spawner.DrawCallsPerCamera} draw(s) par caméra, {spawner.GpuMemoryBytes / 1024} Ko GPU.",
                    MessageType.Info);
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
