using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Émissions ponctuelles de bulles (BubbleColumns.Emit) : tampon circulaire de bulles en mémoire CPU, envoyé par plages au
    /// GPU, et zones couvertes par les émissions en vol (bounds dynamiques du draw), oubliées quand leurs bulles ont éclaté.
    /// Disposition des bulles : celle que lit NyxEvaluateInstance (BubbleColumns.compute).
    /// </summary>
    internal sealed class BubbleBursts
    {
        /// <summary>Les émissions rapprochées (moins de 0,5 s) partagent une zone : quelques dizaines de zones au plus.</summary>
        const float ZoneMergeWindow = 0.5f;

        /// <summary>Zone couverte par les émissions ponctuelles d'une demi-seconde, jusqu'à éclatement de leur dernière bulle.</summary>
        struct Zone
        {
            public Bounds bounds;
            public float createdAt;
            public float expiresAt;
        }

        readonly List<Zone> _zones = new List<Zone>();
        GpuInstanceData[] _buffer = Array.Empty<GpuInstanceData>();
        int _cursor;
        Random _random;   // état 0 = pas encore initialisé

        /// <summary>Tampon circulaire : une case par bulle ponctuelle en vol, les plus anciennes recyclées.</summary>
        public GpuInstanceData[] Buffer => _buffer;

        public int Capacity => _buffer.Length;

        /// <summary>Des émissions sont en vol (leurs bounds évoluent).</summary>
        public bool HasZones => _zones.Count > 0;

        /// <summary>Adapte le tampon à 'capacity' (les bulles en vol sont gardées). Renvoie true s'il a été redimensionné.</summary>
        public bool Resize(int capacity)
        {
            if (_buffer.Length == capacity)
                return false;
            var resized = new GpuInstanceData[capacity];
            for (int i = 0; i < resized.Length; i++)
                resized[i] = i < _buffer.Length ? _buffer[i] : DeadBubble();
            _buffer = resized;
            _cursor = 0;
            return true;
        }

        /// <summary>
        /// Écrit 'count' bulles (au plus Capacity) autour de 'position', dans un rayon 'spread', à partir de la case renvoyée
        /// (les suivantes en boucle). Tailles, vitesses et spirale : 'settings' ; tirages : 'seed'. Ajoute la colonne d'eau
        /// de l'émission, jusqu'à la surface (+ 'waveMargin'), aux zones en vol.
        /// </summary>
        public int Emit(Vector3 position, int count, float spread, BubbleEmitter settings, int seed, float now, float waterLevel, float waveMargin)
        {
            if (_random.state == 0)
                _random = new Random(math.hash(new uint2((uint)seed, 0x5bd1e995u)) | 1u);

            float travel = waterLevel - position.y;
            float margin = travel + spread + waveMargin;
            int first = _cursor;
            float longestLife = 0f;
            for (int i = 0; i < count; i++)
            {
                float speed = math.max(Lerp(settings.riseSpeed, _random.NextFloat()), 0.01f);
                float2 offset = RandomInDisk(ref _random) * spread;
                var spawn = new float3(position.x + offset.x, position.y + (_random.NextFloat() - 0.5f) * spread, position.z + offset.y);
                float lifetime = margin / speed;
                longestLife = Mathf.Max(longestLife, lifetime);
                _buffer[_cursor] = MakeBubble(
                    spawn, now, Lerp(settings.surfaceRadius, _random.NextFloat()), speed, lifetime,
                    settings.spiralRadius, settings.spiralFrequency * math.lerp(0.7f, 1.3f, _random.NextFloat()), _random.NextFloat());
                _cursor = (_cursor + 1) % _buffer.Length;
            }

            // Bounds du draw : colonne de l'émission jusqu'à la surface, oubliée quand ses bulles ont éclaté.
            float side = 2f * (spread + settings.spiralRadius + settings.surfaceRadius.y * 2f + 0.05f);
            var column = new Bounds(
                new Vector3(position.x, (position.y + waterLevel) * 0.5f, position.z),
                new Vector3(side, travel + 2f * spread + 2f * waveMargin + 0.2f, side));
            int last = _zones.Count - 1;
            if (last >= 0 && now - _zones[last].createdAt < ZoneMergeWindow)
            {
                Zone zone = _zones[last];
                zone.bounds.Encapsulate(column);
                zone.expiresAt = Mathf.Max(zone.expiresAt, now + longestLife);
                _zones[last] = zone;
            }
            else
            {
                _zones.Add(new Zone { bounds = column, createdAt = now, expiresAt = now + longestLife });
            }
            return first;
        }

        /// <summary>Oublie les zones dont toutes les bulles ont éclaté ; bounds des émissions encore en vol, null s'il n'y en a plus.</summary>
        public Bounds? Bounds(float now)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)   // boucle manuelle : pas d'allocation par frame
            {
                if (_zones[i].expiresAt < now)
                    _zones.RemoveAt(i);
            }
            if (_zones.Count == 0)
                return null;
            Bounds union = _zones[0].bounds;
            for (int i = 1; i < _zones.Count; i++)
                union.Encapsulate(_zones[i].bounds);
            return union;
        }

        /// <summary>Oublie les émissions en vol (comportement désactivé).</summary>
        public void ClearZones() => _zones.Clear();

        /// <summary>Donnée GPU d'une bulle ponctuelle (disposition lue par BubbleColumns.compute), variante 0.</summary>
        static GpuInstanceData MakeBubble(float3 spawn, float spawnTime, float surfaceRadius, float speed, float lifetime,
                                          float spiralRadius, float spiralFrequency, float seed)
        {
            return new GpuInstanceData
            {
                row0 = new Vector4(spawn.x, spawn.y, spawn.z, spawnTime),
                row1 = new Vector4(surfaceRadius, speed, lifetime, 0f),   // w = 0 : ponctuelle, pas de boucle
                row2 = new Vector4(spiralRadius, spiralFrequency, seed, 0f),
                custom = new Vector4(0f, 0f, 0f, 0f)
            };
        }

        /// <summary>Case libre du tampon : durée de vie nulle, jamais dessinée.</summary>
        static GpuInstanceData DeadBubble() => MakeBubble(float3.zero, -1e6f, 0f, 0f, 0f, 0f, 0f, 0f);

        /// <summary>Point uniforme dans le disque unité (= NyxRandomInDisk, BubbleColumnsBuild.compute).</summary>
        static float2 RandomInDisk(ref Random random)
        {
            float angle = random.NextFloat() * math.PI * 2f;
            float distance = math.sqrt(random.NextFloat());
            math.sincos(angle, out float s, out float c);
            return new float2(c, s) * distance;
        }

        static float Lerp(Vector2 range, float t) => math.lerp(range.x, range.y, t);
    }
}
