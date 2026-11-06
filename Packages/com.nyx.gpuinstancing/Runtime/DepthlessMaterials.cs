using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Copies de matériaux hors de la texture de profondeur de la caméra (option Exclude From Depth Texture du MeshSpawner).
    /// Unity n'y dessine que les files de rendu jusqu'à 2500 (pipeline Built-in) : la file des copies passe au-dessus. Les
    /// copies suivent leur original à chaque rendu ; les assets ne changent pas.
    /// </summary>
    internal sealed class DepthlessMaterials
    {
        /// <summary>Plus haute file de rendu dessinée dans la texture de profondeur de la caméra (pipeline Built-in).</summary>
        public const int DepthTextureMaxQueue = 2500;

        readonly Dictionary<Material, Material> _copies = new Dictionary<Material, Material>();   // original -> copie

        /// <summary>Matériau d'un draw : l'original, ou s'il faut l'exclure de la profondeur sa copie (gardée jusqu'à Release).</summary>
        public Material For(Material material, bool exclude)
        {
            if (!exclude || material.renderQueue > DepthTextureMaxQueue)
                return material;
            if (!_copies.TryGetValue(material, out Material copy) || copy == null)
            {
                copy = new Material(material) { name = material.name + " (hors profondeur)", hideFlags = HideFlags.HideAndDontSave };
                _copies[material] = copy;
            }
            copy.renderQueue = DepthTextureMaxQueue + 1;
            return copy;
        }

        /// <summary>Les copies suivent les réglages de leur original (modifiés dans l'inspecteur, animés...).</summary>
        public void Sync()
        {
            foreach (KeyValuePair<Material, Material> pair in _copies)
            {
                Material original = pair.Key;
                Material copy = pair.Value;
                if (original == null || copy == null)
                    continue;
                if (copy.shader != original.shader)
                    copy.shader = original.shader;
                copy.CopyPropertiesFromMaterial(original);   // propriétés, mots-clés... et file de rendu
                copy.enableInstancing = original.enableInstancing;
                copy.renderQueue = DepthTextureMaxQueue + 1;
            }
        }

        /// <summary>Détruit les copies : en jeu après la frame, quand aucun draw en cours ne les référence plus.</summary>
        public void Release()
        {
            foreach (Material copy in _copies.Values)
            {
                if (copy == null)
                    continue;
                if (Application.isPlaying)
                    Object.Destroy(copy);
                else
                    Object.DestroyImmediate(copy);
            }
            _copies.Clear();
        }
    }
}
