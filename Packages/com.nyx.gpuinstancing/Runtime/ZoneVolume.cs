using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Volume de la zone d'un <see cref="MeshSpawner"/> : polygone de la face du haut extrudé vers le bas, contour courbe ou
    /// non, bord naturel (bruit, lisière). Valeur figée au moment où elle est lue (réglages et position de l'objet), sans
    /// ressource GPU. Sert au gameplay et aux outils (Contains, Edge) et donne la zone des noyaux (ToGpu).
    /// Polygone : <see cref="ZonePolygon"/> ; bord : <see cref="ZoneEdge"/> ; GPU : <see cref="ZoneGpu"/>.
    /// </summary>
    internal readonly struct ZoneVolume
    {
        /// <summary>Tolérance de hauteur : une instance posée sur la face du haut est dedans.</summary>
        public const float HeightTolerance = 1e-3f;

        readonly IReadOnlyList<Vector2> _points;   // sommets (X, Z) par rapport au centre, dans l'ordre du contour
        readonly bool _smooth;
        readonly float _depth;
        readonly float _noise;
        readonly float _noiseScale;
        readonly float _feather;
        readonly uint _seed;

        /// <param name="top">Centre monde de la face du haut.</param>
        /// <param name="noiseSeed">Graine du bruit du bord, sur 24 bits (passée au GPU dans un flottant).</param>
        public ZoneVolume(Vector3 top, IReadOnlyList<Vector2> points, bool smooth, float depth, float edgeNoise, float edgeNoiseScale,
                          float edgeFeather, uint noiseSeed)
        {
            Top = top;
            _points = points;
            _smooth = smooth;
            _depth = depth;
            _noise = edgeNoise;
            _noiseScale = edgeNoiseScale;
            _feather = edgeFeather;
            _seed = noiseSeed;
        }

        /// <summary>Centre monde de la face du haut.</summary>
        public Vector3 Top { get; }

        /// <summary>
        /// Contour effectif (X, Z) par rapport au centre : les sommets, ou la courbe qui passe par eux (contour courbe,
        /// MeshSpawner.SmoothZonePoints sommets environ). Polygone du placement et de la destruction, avant le bruit du bord.
        /// </summary>
        public IReadOnlyList<Vector2> Outline => _smooth ? ZonePolygon.Smooth(_points, MeshSpawner.SmoothZonePoints) : _points;

        /// <summary>Le bord demande une distance au contour : bruit, ou lisière quand elle compte.</summary>
        public bool IsNatural(bool withFeather) => _noise > 0f || (withFeather && _feather > 0f);

        /// <summary>Contour effectif en coordonnées monde (x, z).</summary>
        public List<Vector2> WorldOutline()
        {
            IReadOnlyList<Vector2> outline = Outline;
            var world = new List<Vector2>(outline.Count);
            foreach (Vector2 point in outline)
                world.Add(new Vector2(Top.x + point.x, Top.z + point.y));
            return world;
        }

        /// <summary>
        /// Bord en (x, z) monde, &lt;= 0 dedans (même calcul que le GPU). 'withFeather' : la lisière compte, la distance au
        /// bord est calculée même sans bruit (la densité du placement vaut min(1, -bord / lisière)).
        /// </summary>
        public float Edge(Vector2 xz, IReadOnlyList<Vector2> worldOutline, bool withFeather) =>
            ZoneEdge.Evaluate(xz, worldOutline, _noise, 1f / _noiseScale, IsNatural(withFeather), new Vector2(Top.x, Top.z), _seed);

        /// <summary>Point monde dans le volume : hauteur, puis contour (courbe et bord naturel compris, lisière non).</summary>
        public bool Contains(Vector3 world)
        {
            if (world.y > Top.y + HeightTolerance || world.y < Top.y - _depth - HeightTolerance)
                return false;
            return Edge(new Vector2(world.x, world.z), WorldOutline(), false) <= 0f;
        }

        /// <summary>Boîte monde du volume (polygone élargi du bruit du bord × profondeur).</summary>
        public Bounds WorldBounds()
        {
            Vector4 b = ZonePolygon.Bounds(WorldOutline());
            float topY = Top.y;
            var bounds = new Bounds();
            bounds.SetMinMax(new Vector3(b.x - _noise, topY - _depth, b.y - _noise), new Vector3(b.z + _noise, topY, b.w + _noise));
            return bounds;
        }

        /// <summary>
        /// Zone des noyaux, figée : contour effectif monde, boîte élargie du bruit, hauteurs (tolérance comprise), bord naturel
        /// avec ou sans lisière (placement procédural : avec ; liste et destruction : sans), centre et graine du bruit.
        /// </summary>
        public ZoneGpu ToGpu(bool withFeather)
        {
            Vector2[] polygon = WorldOutline().ToArray();
            Vector4 b = ZonePolygon.Bounds(polygon);
            return new ZoneGpu(
                polygon,
                new Vector4(b.x - _noise, b.y - _noise, b.z + _noise, b.w + _noise),
                new Vector4(Top.y - _depth - HeightTolerance, Top.y + HeightTolerance, 0f, 0f),
                new Vector4(_noise, 1f / _noiseScale, withFeather ? _feather : 0f, 0f),
                new Vector4(Top.x, Top.z, _seed, 0f));
        }
    }
}
