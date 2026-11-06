using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Zone telle que la lisent les noyaux (InstancingZone.hlsl) : sommets monde du contour effectif, boîte élargie du bruit
    /// du bord, hauteurs du volume, bord naturel, origine du bruit. Valeurs figées ; le buffer des sommets est créé à la
    /// première liaison et libéré par Release (les valeurs restent, pour une liaison suivante). Une seule source pour le
    /// placement (lisière comprise) et la destruction pendant le culling (sans lisière).
    /// </summary>
    internal sealed class ZoneGpu
    {
        readonly Vector2[] _vertices;
        readonly Vector4 _bounds;
        readonly Vector4 _height;
        readonly Vector4 _natural;
        readonly Vector4 _origin;
        ComputeBuffer _buffer;

        public ZoneGpu(Vector2[] vertices, Vector4 bounds, Vector4 height, Vector4 natural, Vector4 origin)
        {
            _vertices = vertices;
            _bounds = bounds;
            _height = height;
            _natural = natural;
            _origin = origin;
        }

        /// <summary>Pas de zone (culling : pas de destruction par le volume). Le noyau déclare le buffer : il reste lié.</summary>
        public static ZoneGpu Disabled() => new ZoneGpu(System.Array.Empty<Vector2>(), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero);

        /// <summary>Sommets (0 : pas de zone).</summary>
        public int PointCount => _vertices.Length;

        /// <summary>Sommets monde (x, z) du contour effectif.</summary>
        public Vector2[] Vertices => _vertices;

        /// <summary>Boîte du polygone élargie de l'amplitude du bord : min x, min z, max x, max z.</summary>
        public Vector4 Bounds => _bounds;

        /// <summary>Pose la zone pour 'kernel' : sommets, nombre de sommets, boîte, hauteurs, bord naturel, origine du bruit.</summary>
        public void Bind(ComputeShader shader, int kernel)
        {
            if (_buffer == null || !_buffer.IsValid())
            {
                _buffer = new ComputeBuffer(Mathf.Max(PointCount, 1), 2 * sizeof(float));
                if (PointCount > 0)
                    _buffer.SetData(_vertices);
            }
            shader.SetInt(ShaderIds.ZonePointCount, PointCount);
            shader.SetBuffer(kernel, ShaderIds.ZoneVertices, _buffer);
            shader.SetVector(ShaderIds.ZoneBounds, _bounds);
            shader.SetVector(ShaderIds.ZoneHeight, _height);
            shader.SetVector(ShaderIds.ZoneNatural, _natural);
            shader.SetVector(ShaderIds.ZoneOrigin, _origin);
        }

        /// <summary>Libère le buffer des sommets (détruit par Unity après les dispatchs déjà soumis qui le lisent).</summary>
        public void Release()
        {
            _buffer?.Release();
            _buffer = null;
        }
    }
}
