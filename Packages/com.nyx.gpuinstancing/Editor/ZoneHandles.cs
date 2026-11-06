using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>
    /// Zone d'un MeshSpawner dans la vue Scene : volume (polygone extrudé), contour courbe et bord naturel, sommets à déplacer
    /// (Ctrl+clic : supprimer), points au milieu des arêtes (clic : insérer un sommet), poignées du centre et de la
    /// profondeur. Les modifications passent par le SerializedObject : annulables (Ctrl+Z), reconstruction via OnValidate.
    /// </summary>
    static class ZoneHandles
    {
        /// <summary>Au-delà, pas de poignée par sommet dans la vue Scene (trop lourd à dessiner) : liste ou SetZone.</summary>
        public const int MaxHandledPoints = 256;

        static readonly Color ClipColor = new Color(1f, 0.55f, 0.1f);
        static readonly Color ZoneColor = new Color(0.3f, 0.8f, 1f);

        // Bord naturel : contour calculé par carrés marchants (même fonction que le GPU), recalculé seulement si la zone change
        static string s_shapeKey;
        static readonly List<Vector3> s_edgeLines = new List<Vector3>();
        static readonly List<Vector3> s_featherLines = new List<Vector3>();
        const int ContourResolution = 160;

        public static void Draw(SerializedObject serializedObject, MeshSpawner spawner)
        {
            MeshSpawnerBehavior behavior = spawner.Behavior;
            if (behavior != null && !behavior.UsesPlacement && !spawner.ClipToVolume)
                return;   // zone inutilisée : ni placement, ni destruction

            serializedObject.Update();
            Vector3 top = spawner.ZoneCenter;
            float depth = spawner.ZoneDepth;
            var points = new List<Vector2>(spawner.ZonePoints);   // rectangle de l'ancienne zone si moins de 3 sommets
            int count = points.Count;
            var topRing = new Vector3[count + 1];
            var bottomRing = new Vector3[count + 1];
            for (int i = 0; i <= count; i++)
            {
                Vector2 p = points[i % count];
                topRing[i] = top + new Vector3(p.x, 0f, p.y);
                bottomRing[i] = topRing[i] + Vector3.down * depth;
            }

            Color color = spawner.ClipToVolume ? ClipColor : ZoneColor;
            Handles.color = color;
            bool shaped = spawner.ZoneSmooth || spawner.ZoneEdgeNoise > 0f || PlacementFeather(spawner) > 0f;
            if (shaped)
            {
                // Points de contrôle en pointillés ; contour courbe et bord naturel en traits pleins
                for (int i = 0; i < count; i++)
                    Handles.DrawDottedLine(topRing[i], topRing[i + 1], 3f);
                DrawShapedOutline(spawner, top, depth, color);
            }
            else
            {
                Handles.DrawAAPolyLine(3f, topRing);
                Handles.DrawPolyLine(bottomRing);
                for (int i = 0; i < count; i += Mathf.Max(1, count / 64))   // arêtes verticales : 64 au plus
                    Handles.DrawDottedLine(topRing[i], bottomRing[i], 4f);
            }
            Handles.color = color;

            bool changed = MoveVertices(spawner, top, topRing, points);

            // Centre de la face du haut et profondeur
            SerializedProperty centerProperty = serializedObject.FindProperty("zoneCenter");
            SerializedProperty depthProperty = serializedObject.FindProperty("zoneDepth");
            EditorGUI.BeginChangeCheck();
            Vector3 movedTop = Handles.PositionHandle(top, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
                centerProperty.vector3Value = movedTop - spawner.transform.position;
            Vector3 bottom = top + Vector3.down * depth;
            EditorGUI.BeginChangeCheck();
            Vector3 movedBottom = Handles.Slider(bottom, Vector3.down, HandleUtility.GetHandleSize(bottom) * 0.12f, Handles.CubeHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
                depthProperty.floatValue = Mathf.Max(0f, top.y - movedBottom.y);

            if (changed)
                WriteZonePoints(serializedObject.FindProperty("zonePoints"), points);
            serializedObject.ApplyModifiedProperties();   // annulable (Ctrl+Z), reconstruction via OnValidate
        }

        /// <summary>
        /// Sommets : glisser sur le plan de la face du haut ; Ctrl+clic : supprimer (3 sommets minimum) ; milieux des arêtes :
        /// clic = nouveau sommet. Rien au-delà de MaxHandledPoints sommets. Renvoie true si 'points' a changé.
        /// </summary>
        static bool MoveVertices(MeshSpawner spawner, Vector3 top, Vector3[] topRing, List<Vector2> points)
        {
            int count = points.Count;
            if (count > MaxHandledPoints)
                return false;
            Event e = Event.current;
            var plane = new Plane(Vector3.up, top);
            bool changed = false;
            int remove = -1;

            for (int i = 0; i < count; i++)
            {
                Vector3 world = topRing[i];
                float size = HandleUtility.GetHandleSize(world) * 0.07f;
                int id = GUIUtility.GetControlID(FocusType.Passive);
                if (e.type == EventType.MouseDown && e.button == 0 && (e.control || e.command) && HandleUtility.nearestControl == id)
                {
                    if (count > 3)
                        remove = i;
                    e.Use();
                    continue;
                }
                EditorGUI.BeginChangeCheck();
                Handles.FreeMoveHandle(id, world, Quaternion.identity, size, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck() && plane.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out float distance))
                {
                    Vector3 hit = HandleUtility.GUIPointToWorldRay(e.mousePosition).GetPoint(distance);
                    points[i] = new Vector2(hit.x - top.x, hit.z - top.z);
                    changed = true;
                }
            }
            if (remove >= 0)
            {
                points.RemoveAt(remove);
                changed = true;
            }

            if (!changed && count < MeshSpawner.MaxZonePoints)
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 middle = (topRing[i] + topRing[i + 1]) * 0.5f;
                    float size = HandleUtility.GetHandleSize(middle) * 0.045f;
                    if (Handles.Button(middle, Quaternion.identity, size, size * 1.5f, Handles.CircleHandleCap))
                    {
                        points.Insert(i + 1, (points[i] + points[(i + 1) % count]) * 0.5f);
                        changed = true;
                        break;
                    }
                }
            }
            return changed;
        }

        /// <summary>
        /// Contour courbe (haut et bas du volume), bord naturel déplacé par le bruit (trait épais) et limite intérieure de la
        /// lisière (pointillés : au-delà, densité pleine).
        /// </summary>
        static void DrawShapedOutline(MeshSpawner spawner, Vector3 top, float depth, Color color)
        {
            List<Vector2> outline = spawner.WorldOutline();
            var topRing = new Vector3[outline.Count + 1];
            var bottomRing = new Vector3[outline.Count + 1];
            for (int i = 0; i <= outline.Count; i++)
            {
                Vector2 p = outline[i % outline.Count];
                topRing[i] = new Vector3(p.x, top.y, p.y);
                bottomRing[i] = topRing[i] + Vector3.down * depth;
            }
            float noise = spawner.ZoneEdgeNoise;
            float feather = PlacementFeather(spawner);
            Handles.color = noise > 0f ? new Color(color.r, color.g, color.b, 0.45f) : color;
            Handles.DrawAAPolyLine(noise > 0f ? 1.5f : 3f, topRing);
            Handles.color = color;
            Handles.DrawPolyLine(bottomRing);
            if (noise <= 0f && feather <= 0f)
                return;

            var key = new System.Text.StringBuilder();
            foreach (Vector2 p in outline)
                key.Append(p.x.ToString("R")).Append(',').Append(p.y.ToString("R")).Append(';');
            key.Append(noise).Append('|').Append(spawner.ZoneEdgeNoiseScale).Append('|').Append(feather).Append('|')
               .Append(spawner.ZoneCenter.x).Append(',').Append(spawner.ZoneCenter.z);
            if (key.ToString() != s_shapeKey)
            {
                s_shapeKey = key.ToString();
                Vector4 bounds = ZonePolygon.Bounds(outline);
                float margin = noise + 1f;
                bounds += new Vector4(-margin, -margin, margin, margin);
                Fill(s_edgeLines, ZoneContour.Trace(p => spawner.ZoneEdgeAt(p, outline, false), bounds, ContourResolution));
                s_featherLines.Clear();
                if (feather > 0f)
                    Fill(s_featherLines, ZoneContour.Trace(p => spawner.ZoneEdgeAt(p, outline, true) + feather, bounds, ContourResolution));
            }
            float y = top.y;
            Handles.color = color;
            if (noise > 0f)
                Handles.DrawLines(Lift(s_edgeLines, y));
            if (s_featherLines.Count > 0)
            {
                Vector3[] feathered = Lift(s_featherLines, y);
                for (int i = 0; i + 1 < feathered.Length; i += 2)
                    Handles.DrawDottedLine(feathered[i], feathered[i + 1], 2f);
            }
        }

        /// <summary>Lisière qui agit sur le placement : aucune pour une liste (placement figé compris).</summary>
        static float PlacementFeather(MeshSpawner spawner) =>
            spawner.Source == MeshSpawner.PlacementSource.List ? 0f : spawner.ZoneEdgeFeather;

        static void Fill(List<Vector3> lines, List<Vector2> segments)
        {
            lines.Clear();
            foreach (Vector2 p in segments)
                lines.Add(new Vector3(p.x, 0f, p.y));
        }

        static Vector3[] Lift(List<Vector3> lines, float y)
        {
            var result = new Vector3[lines.Count];
            for (int i = 0; i < lines.Count; i++)
                result[i] = new Vector3(lines[i].x, y, lines[i].z);
            return result;
        }

        /// <summary>Remplace les sommets de la zone (propriété « zonePoints ») et applique (annulable).</summary>
        public static void WriteZonePoints(SerializedProperty property, IReadOnlyList<Vector2> points)
        {
            property.arraySize = points.Count;
            for (int i = 0; i < points.Count; i++)
                property.GetArrayElementAtIndex(i).vector2Value = points[i];
            property.serializedObject.ApplyModifiedProperties();
        }
    }
}
