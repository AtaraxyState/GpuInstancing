using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Volume de la zone d'un <see cref="MeshSpawner"/> : polygone de la face du haut extrudé vers le bas, contour courbe ou
    /// non, bord naturel (bruit, lisière). Valeur figée au moment où elle est lue (réglages et position de l'objet), sans
    /// ressource GPU. Même calcul que InstancingZone.hlsl : sert au gameplay et aux outils (Contains, Edge) et prépare les
    /// paramètres du placement et de la destruction sur GPU.
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

        /// <summary>Irrégularité du bord (m) : le contour peut sortir du polygone de ± cette distance.</summary>
        public float EdgeNoise => _noise;

        /// <summary>
        /// Contour effectif (X, Z) par rapport au centre : les sommets, ou la courbe qui passe par eux (contour courbe,
        /// MeshSpawner.SmoothZonePoints sommets environ). Polygone du placement et de la destruction, avant le bruit du bord.
        /// </summary>
        public IReadOnlyList<Vector2> Outline => _smooth ? ZoneShape.Smooth(_points, MeshSpawner.SmoothZonePoints) : _points;

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
            ZoneShape.Edge(xz, worldOutline, _noise, 1f / _noiseScale, IsNatural(withFeather), new Vector2(Top.x, Top.z), _seed);

        /// <summary>Point monde dans le volume : hauteur, puis contour (courbe et bord naturel compris, lisière non).</summary>
        public bool Contains(Vector3 world)
        {
            if (world.y > Top.y + HeightTolerance || world.y < Top.y - _depth - HeightTolerance)
                return false;
            return Edge(new Vector2(world.x, world.z), WorldOutline(), false) <= 0f;
        }

        /// <summary>Sommets du contour effectif en coordonnées monde (x, z) et leur boîte (min x, min z, max x, max z).</summary>
        public NativeArray<float2> WorldPolygon(Allocator allocator, out float4 bounds)
        {
            IReadOnlyList<Vector2> points = Outline;
            var polygon = new NativeArray<float2>(points.Count, allocator, NativeArrayOptions.UninitializedMemory);
            bounds = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int i = 0; i < points.Count; i++)
            {
                var p = new float2(Top.x + points[i].x, Top.z + points[i].y);
                polygon[i] = p;
                bounds = new float4(math.min(bounds.xy, p), math.max(bounds.zw, p));
            }
            return polygon;
        }

        /// <summary>Boîte monde du volume (polygone élargi du bruit du bord × profondeur).</summary>
        public Bounds WorldBounds()
        {
            NativeArray<float2> polygon = WorldPolygon(Allocator.Temp, out float4 b);
            polygon.Dispose();
            float topY = Top.y;
            var bounds = new Bounds();
            bounds.SetMinMax(new Vector3(b.x - _noise, topY - _depth, b.y - _noise), new Vector3(b.z + _noise, topY, b.w + _noise));
            return bounds;
        }

        /// <summary>Hauteurs monde du volume pour le GPU (_NyxZoneHeight) : bas, haut, tolérance comprise.</summary>
        public Vector4 HeightRange => new Vector4(Top.y - _depth - HeightTolerance, Top.y + HeightTolerance, 0f, 0f);

        /// <summary>Bord naturel pour le GPU (_NyxZoneNatural) : amplitude, 1 / taille, lisière (0 si elle ne compte pas).</summary>
        public Vector4 NaturalParameters(bool withFeather) => new Vector4(_noise, 1f / _noiseScale, withFeather ? _feather : 0f, 0f);

        /// <summary>Centre (x, z) et graine du bruit du bord pour le GPU (_NyxZoneOrigin).</summary>
        public Vector4 NoiseOrigin => new Vector4(Top.x, Top.z, _seed, 0f);

        /// <summary>Quatre sommets aux coins de leur boîte, reliés par des arêtes parallèles aux axes (pas un nœud papillon).</summary>
        public static bool IsAxisAlignedRectangle(NativeArray<float2> polygon, float4 bounds)
        {
            if (polygon.Length != 4)
                return false;
            int corners = 0;
            for (int i = 0; i < 4; i++)
            {
                float2 p = polygon[i];
                float2 next = polygon[(i + 1) & 3];
                bool cornerX = p.x == bounds.x || p.x == bounds.z;
                bool cornerZ = p.y == bounds.y || p.y == bounds.w;
                if (!cornerX || !cornerZ || (p.x != next.x && p.y != next.y))
                    return false;
                corners |= 1 << ((p.x == bounds.z ? 1 : 0) + (p.y == bounds.w ? 2 : 0));
            }
            return corners == 15;
        }
    }
}
