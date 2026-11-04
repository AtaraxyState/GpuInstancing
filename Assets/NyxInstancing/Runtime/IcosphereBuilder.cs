using System.Collections.Generic;
using UnityEngine;

namespace Nyx.Instancing
{
    /// <summary>
    /// Icosphère de rayon 1, normales exactes. Sommets / triangles selon les subdivisions :
    /// 0 -> 12 / 20, 1 -> 42 / 80, 2 -> 162 / 320, 3 -> 642 / 1280.
    /// Bien moins cher que la sphère Unity (515 sommets / 768 triangles) pour des milliers de bulles.
    /// </summary>
    public static class IcosphereBuilder
    {
        public static Mesh Build(int subdivisions)
        {
            subdivisions = Mathf.Clamp(subdivisions, 0, 3);
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;

            var vertices = new List<Vector3>
            {
                new Vector3(-1f,  t, 0f), new Vector3( 1f,  t, 0f), new Vector3(-1f, -t, 0f), new Vector3( 1f, -t, 0f),
                new Vector3(0f, -1f,  t), new Vector3(0f,  1f,  t), new Vector3(0f, -1f, -t), new Vector3(0f,  1f, -t),
                new Vector3( t, 0f, -1f), new Vector3( t, 0f,  1f), new Vector3(-t, 0f, -1f), new Vector3(-t, 0f,  1f),
            };
            for (int i = 0; i < vertices.Count; i++)
                vertices[i] = vertices[i].normalized;

            // Ordre des sommets : face avant vers l'extérieur avec la convention Unity
            // (normale de face = Cross(b - a, c - a) tournée vers l'observateur).
            var triangles = new List<int>
            {
                0, 11, 5,   0, 5, 1,    0, 1, 7,    0, 7, 10,   0, 10, 11,
                1, 5, 9,    5, 11, 4,   11, 10, 2,  10, 7, 6,   7, 1, 8,
                3, 9, 4,    3, 4, 2,    3, 2, 6,    3, 6, 8,    3, 8, 9,
                4, 9, 5,    2, 4, 11,   6, 2, 10,   8, 6, 7,    9, 8, 1,
            };

            var midpoints = new Dictionary<long, int>();
            for (int level = 0; level < subdivisions; level++)
            {
                var refined = new List<int>(triangles.Count * 4);
                midpoints.Clear();
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];
                    int ab = Midpoint(a, b, vertices, midpoints);
                    int bc = Midpoint(b, c, vertices, midpoints);
                    int ca = Midpoint(c, a, vertices, midpoints);
                    refined.AddRange(new[] { a, ab, ca,   b, bc, ab,   c, ca, bc,   ab, bc, ca });
                }
                triangles = refined;
            }

            var mesh = new Mesh
            {
                name = $"NyxIcosphere{subdivisions}",
                hideFlags = HideFlags.DontSave
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertices);   // sphère unité : normale = position
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static int Midpoint(int a, int b, List<Vector3> vertices, Dictionary<long, int> cache)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cache.TryGetValue(key, out int index))
                return index;
            index = vertices.Count;
            vertices.Add(((vertices[a] + vertices[b]) * 0.5f).normalized);
            cache.Add(key, index);
            return index;
        }
    }
}
