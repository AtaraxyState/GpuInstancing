using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nyx.GpuInstancing.Editor
{
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
