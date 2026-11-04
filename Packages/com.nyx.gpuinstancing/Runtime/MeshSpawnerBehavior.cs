using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Comportement d'un <see cref="MeshSpawner"/> : décide comment chaque instance bouge. Un seul par objet.
    ///
    /// Écrire un comportement = 2 fichiers :
    ///  1. un compute shader dans un dossier Resources, qui définit la position finale de chaque instance puis inclut le culling :
    ///     <code>
    ///     #pragma kernel NyxCull
    ///     #pragma kernel NyxWriteArgs
    ///     #include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/InstancingData.hlsl"
    ///     float _NyxTime;
    ///     bool NyxEvaluateInstance(uint index, NyxInstanceData src, out NyxRenderData dst)
    ///     {
    ///         dst = (NyxRenderData)0;
    ///         dst.row0 = src.row0; dst.row1 = src.row1; dst.row2 = src.row2;   // matrice 3x4 construite par CreateInstance
    ///         dst.row1.w += sin(_NyxTime + src.custom.x * 6.28) * 0.1;          // ex. : monte et descend
    ///         dst.custom = src.custom;
    ///         return true;                                                      // false = instance morte, jamais dessinée
    ///     }
    ///     #include "Packages/com.nyx.gpuinstancing/Shaders/ShaderLibrary/InstancingCulling.hlsl"
    ///     </code>
    ///  2. une classe qui hérite de MeshSpawnerBehavior et donne le chemin de ce compute shader (CullingShaderResource).
    ///     Les autres méthodes sont facultatives : paramètres du shader, données par instance, bounds, instances propres.
    ///
    /// Données d'instance (64 octets) : row0..row2 = matrice 3x4 objet -> monde par défaut, custom.x = graine 0..1,
    /// custom.y/z = libres, custom.w = index de variante (ne pas modifier). Le compute shader écrit dans dst.custom
    /// les valeurs lues par le shader de rendu (NyxGetRenderData().custom).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshSpawner))]
    public abstract class MeshSpawnerBehavior : MonoBehaviour
    {
        MeshSpawner _spawner;
        ComputeShader _shader;

        public MeshSpawner Spawner => _spawner != null ? _spawner : _spawner = GetComponent<MeshSpawner>();

        /// <summary>Chemin, dans un dossier Resources et sans extension, du compute shader de ce comportement.</summary>
        protected abstract string CullingShaderResource { get; }

        /// <summary>Compute shader de culling (chargé depuis Resources). À surcharger pour le fournir autrement.</summary>
        public virtual ComputeShader CullingShader => _shader != null ? _shader : _shader = Resources.Load<ComputeShader>(CullingShaderResource);

        /// <summary>
        /// Vrai (défaut) : les instances viennent du placement du MeshSpawner (zone, amas, liste), via CreateInstance.
        /// Faux : le comportement fournit ses propres instances dans BuildInstances (ex. colonnes de bulles).
        /// </summary>
        public virtual bool UsesPlacement => true;

        /// <summary>
        /// Donnée GPU d'une instance placée. Défaut : matrice position + lacet + échelle, graine en custom.x.
        /// Surcharger pour y écrire des données propres au comportement (custom.y/z, ou une autre interprétation des lignes).
        /// </summary>
        public virtual GpuInstanceData CreateInstance(MeshPlacement placement)
        {
            Matrix4x4 matrix = Matrix4x4.TRS(placement.position, Quaternion.Euler(0f, placement.yaw, 0f), Vector3.one * placement.scale);
            return GpuInstanceData.FromMatrix(matrix, placement.variant, placement.seed);
        }

        /// <summary>
        /// Bounds monde couvrant toutes les positions possibles des instances (mouvement compris), pour le culling CPU
        /// du spawner entier. positions = boîte des positions au repos, meshRadius = plus grand rayon de mesh × échelle.
        /// Défaut : positions agrandies du rayon (instances immobiles). À agrandir si le comportement déplace les instances.
        /// </summary>
        public virtual Bounds GetPlacementBounds(Bounds positions, float meshRadius)
        {
            positions.Expand(meshRadius * 2f);
            return positions;
        }

        /// <summary>Pour UsesPlacement = false : remplit 'output' et renvoie les bounds monde de toutes les positions possibles.</summary>
        public virtual Bounds BuildInstances(List<GpuInstanceData> output) => new Bounds(transform.position, Vector3.zero);

        /// <summary>Paramètres posés sur le compute shader avant chaque dispatch (une fois par caméra et par frame).</summary>
        public virtual void SetShaderParameters(ComputeShader shader, Camera camera) { }

        /// <summary>Marge (espace local du mesh) ajoutée aux bounds d'une variante pour couvrir la déformation du vertex shader.</summary>
        public virtual float GetBoundsPadding(InstanceVariant variant, Bounds localBounds, float defaultPadding) => defaultPadding;

#if UNITY_EDITOR
        /// <summary>Éditeur : variantes (meshes + matériau) données au MeshSpawner s'il n'en a pas encore. null = aucune.</summary>
        public virtual List<InstanceVariant> CreateDefaultVariants() => null;

        /// <summary>Éditeur : donne au MeshSpawner les variantes par défaut de ce comportement s'il n'en a aucune.</summary>
        public void ApplyDefaultVariants()
        {
            MeshSpawner spawner = Spawner;
            if (spawner == null || spawner.Variants.Count > 0)
                return;
            List<InstanceVariant> defaults = CreateDefaultVariants();
            if (defaults == null || defaults.Count == 0)
                return;
            UnityEditor.Undo.RecordObject(spawner, "Variantes par défaut");
            spawner.SetVariants(defaults);
            UnityEditor.EditorUtility.SetDirty(spawner);
        }

        protected virtual void Reset() => ApplyDefaultVariants();
#endif

        /// <summary>Reconstruction du spawner (après un changement de paramètre qui modifie les instances).</summary>
        protected void SetDirty()
        {
            if (Spawner != null)
                Spawner.SetDirty();
        }

        protected virtual void OnEnable() => SetDirty();

        protected virtual void OnDisable() => SetDirty();

        protected virtual void OnValidate() => SetDirty();
    }
}
