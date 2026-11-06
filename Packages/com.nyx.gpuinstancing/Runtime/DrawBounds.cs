using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Bounds des draws d'un spawner, et du rejet CPU du spawner entier : instances à la reconstruction (mouvement du
    /// comportement compris), plus les émissions en vol (bounds dynamiques), le tout limité au volume de la zone quand les
    /// instances qui en sortent sont détruites.
    /// </summary>
    internal sealed class DrawBounds
    {
        Bounds _build;          // instances à la reconstruction
        Bounds? _dynamic;       // émissions en vol (SetDynamicBounds)
        Bounds? _zoneLimit;     // volume agrandi de la taille des meshes : les instances détruites hors du volume ne sont pas dessinées

        /// <summary>Bounds monde des draws.</summary>
        public Bounds World { get; private set; }

        /// <summary>Aucune instance : petite boîte autour du pivot.</summary>
        public void SetEmpty(Vector3 pivot) => SetBuild(new Bounds(pivot, Vector3.one), null);

        /// <summary>Instances propres au comportement : ses bounds, sans limite de volume.</summary>
        public void SetBehavior(Bounds bounds) => SetBuild(bounds, null);

        /// <summary>
        /// Instances placées : boîte des pivots élargie de la taille des meshes ('meshExtent') et du mouvement du comportement.
        /// Avec 'volume' (Clip To Volume), les draws y sont limités. Renvoie vrai si le mouvement du comportement dépasse la
        /// hauteur du volume : les instances qui en sortent seront détruites.
        /// </summary>
        public bool SetPlaced(MeshSpawnerBehavior behavior, Bounds positions, float meshExtent, Bounds? volume)
        {
            Bounds build = behavior.GetPlacementBounds(positions, meshExtent);
            if (!volume.HasValue)
            {
                SetBuild(build, null);
                return false;
            }
            Bounds limit = volume.Value;
            Bounds motion = behavior.GetPlacementBounds(positions, 0f);   // pivots seulement
            bool exceeds = motion.max.y > limit.max.y + ZoneVolume.HeightTolerance || motion.min.y < limit.min.y - ZoneVolume.HeightTolerance;
            limit.Expand(meshExtent * 2f);
            SetBuild(build, limit);
            return exceeds;
        }

        /// <summary>Bounds qui évoluent sans reconstruction (émissions en vol). null = aucune.</summary>
        public void SetDynamic(Bounds? bounds)
        {
            _dynamic = bounds;
            Update();
        }

        void SetBuild(Bounds build, Bounds? zoneLimit)
        {
            _build = build;
            _zoneLimit = zoneLimit;
            Update();
        }

        void Update()
        {
            Bounds world = _build;
            if (_dynamic.HasValue)
                world.Encapsulate(_dynamic.Value);
            if (_zoneLimit.HasValue)
            {
                Bounds zone = _zoneLimit.Value;
                Vector3 min = Vector3.Max(world.min, zone.min);
                Vector3 max = Vector3.Max(min, Vector3.Min(world.max, zone.max));
                world.SetMinMax(min, max);
            }
            World = world;
        }
    }
}
