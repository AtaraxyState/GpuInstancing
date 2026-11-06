using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Jobs;
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
    /// Les instances placées sont construites sur GPU (lacet + échelle + position, graine en custom.x). Pour des données
    /// propres au comportement, les calculer de préférence dans NyxEvaluateInstance à partir de la graine (FloatOnWaves :
    /// profondeur dans la couche d'algues). Surcharger CreateInstance(s) en C# reste possible mais plus lent : les
    /// placements sont lus sur GPU, puis les instances écrites en C# sont envoyées.
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
        /// Donnée GPU d'une instance placée, écrite en C#. Défaut : matrice position + lacet + échelle, graine en custom.x
        /// (la même chose est construite sur GPU si ni CreateInstance ni CreateInstances ne sont surchargées).
        /// Surcharger pour y écrire des données propres au comportement (custom.y/z, ou une autre interprétation des lignes) :
        /// le placement est alors lu sur GPU et les instances envoyées, bien plus lent qu'un calcul dans le compute shader.
        /// En C# classique, une instance après l'autre : pour des centaines de milliers d'instances, surcharger plutôt
        /// CreateInstances avec un job Burst.
        /// </summary>
        public virtual GpuInstanceData CreateInstance(MeshPlacement placement) =>
            MeshSpawnerJobs.TrsInstance(placement.position, placement.yaw, placement.scale, placement.seed, placement.variant);

        /// <summary>
        /// Toutes les instances d'un coup, en C#, en mémoire native (output a la taille de placements). Appelée seulement si
        /// le comportement surcharge CreateInstance ou CreateInstances (ou si MeshSpawner.CreateInstancesOnGpu est faux).
        /// Défaut : CreateInstance pour chaque placement s'il est surchargé, sinon job Burst parallèle (matrice position +
        /// lacet + échelle).
        /// </summary>
        public virtual void CreateInstances(NativeArray<MeshPlacement> placements, NativeArray<GpuInstanceData> output)
        {
            if (Overrides.single)
            {
                for (int i = 0; i < placements.Length; i++)
                    output[i] = CreateInstance(placements[i]);
                return;
            }
            new MeshSpawnerJobs.TrsInstancesJob { placements = placements, output = output }
                .Schedule(placements.Length, MeshSpawnerJobs.BatchSize).Complete();
        }

        /// <summary>Le comportement écrit ses instances placées en C# (surcharge de CreateInstance ou CreateInstances).</summary>
        internal bool CreatesInstancesOnCpu => Overrides.any;

        (bool single, bool any) Overrides
        {
            get
            {
                Type type = GetType();
                if (!s_Overrides.TryGetValue(type, out (bool single, bool any) overrides))
                {
                    bool single = Declared(type.GetMethod(nameof(CreateInstance), new[] { typeof(MeshPlacement) }));
                    bool all = Declared(type.GetMethod(nameof(CreateInstances), new[] { typeof(NativeArray<MeshPlacement>), typeof(NativeArray<GpuInstanceData>) }));
                    overrides = (single, single || all);
                    s_Overrides.Add(type, overrides);
                }
                return overrides;
            }
        }

        static bool Declared(MethodInfo method) => method != null && method.DeclaringType != typeof(MeshSpawnerBehavior);

        static readonly Dictionary<Type, (bool single, bool any)> s_Overrides = new Dictionary<Type, (bool single, bool any)>();

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

        /// <summary>Pour UsesPlacement = false : nombre d'instances que BuildInstances va écrire.</summary>
        public virtual int GetInstanceCount() => 0;

        /// <summary>
        /// Pour UsesPlacement = false : écrit les instances directement dans le buffer GPU du spawner ('count' =
        /// GetInstanceCount(), buffer d'au moins 1 élément), typiquement par un noyau de son compute shader lancé avec
        /// MeshSpawner.DispatchInstances, et renvoie les bounds monde de toutes les positions possibles. Renvoie false pour
        /// passer par BuildInstances (C#). Les variantes sont comptées sur GPU ensuite.
        /// </summary>
        public virtual bool BuildInstancesOnGpu(ComputeBuffer instances, int count, out Bounds bounds)
        {
            bounds = default;
            return false;
        }

        /// <summary>
        /// Pour UsesPlacement = false, si BuildInstancesOnGpu renvoie false : remplit 'output' en C# (taille GetInstanceCount(),
        /// mémoire native libérée après l'envoi au GPU) et renvoie les bounds monde de toutes les positions possibles.
        /// </summary>
        public virtual Bounds BuildInstances(NativeArray<GpuInstanceData> output) => new Bounds(transform.position, Vector3.zero);

        /// <summary>Paramètres posés sur le compute shader avant chaque dispatch (une fois par caméra et par frame).</summary>
        public virtual void SetShaderParameters(ComputeShader shader, Camera camera) { }

        /// <summary>Marge (espace local du mesh) ajoutée aux bounds d'une variante pour couvrir la déformation du vertex shader.</summary>
        public virtual float GetBoundsPadding(InstanceVariant variant, Bounds localBounds, float defaultPadding) => defaultPadding;

        /// <summary>
        /// « Clip To Volume » donné au MeshSpawner quand ce comportement est ajouté dans l'éditeur. Faux pour un comportement
        /// dont les instances sortent normalement de la zone (colonnes de bulles qui montent jusqu'à la surface).
        /// </summary>
        public virtual bool ClipToVolumeByDefault => true;

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

        /// <summary>Ajout du comportement dans l'éditeur : meshes par défaut si le spawner n'en a pas, destruction par le volume selon le comportement.</summary>
        protected virtual void Reset()
        {
            ApplyDefaultVariants();
            MeshSpawner spawner = Spawner;
            if (spawner != null && spawner.ClipToVolume != ClipToVolumeByDefault)
            {
                UnityEditor.Undo.RecordObject(spawner, "Volume de la zone");
                spawner.ClipToVolume = ClipToVolumeByDefault;
                UnityEditor.EditorUtility.SetDirty(spawner);
            }
        }
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
