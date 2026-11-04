using System;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Houle partagée (4 vagues de Gerstner) entre :
    ///  - le culling GPU (flottaison des sargasses, éclatement des bulles en surface),
    ///  - les shaders, via les variables globales _NyxWaterLevel / _NyxWaveTime / _NyxWaves (OceanWaves.hlsl),
    ///  - le gameplay, via SampleDisplacement / SampleHeight (même calcul côté CPU).
    /// Pour que les sargasses collent à l'eau, le shader d'eau doit déplacer ses sommets avec NyxWaveDisplacement.
    /// Sans ce composant dans la scène : eau plate à Y = 0.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Nyx/GPU Instancing/Ocean Waves")]
    public sealed class OceanWaves : MonoBehaviour
    {
        public const int WaveCount = 4;

        [Serializable]
        public struct Wave
        {
            [Tooltip("Direction de propagation (degrés, 0 = +X, 90 = +Z).")]
            public float directionDegrees;
            [Tooltip("Longueur d'onde (m). La vitesse en découle (eau profonde).")]
            [Min(0.1f)] public float wavelength;
            [Tooltip("Cambrure 0..1. Garder la somme des 4 sous 1, sinon la surface se replie sur elle-même.")]
            [Range(0f, 1f)] public float steepness;
        }

        [Tooltip("Hauteur Y de l'eau au repos.")]
        [SerializeField] float waterLevel;
        [SerializeField, Min(0f)] float timeScale = 1f;
        [SerializeField] Wave[] waves =
        {
            new Wave { directionDegrees = 0f, wavelength = 24f, steepness = 0.12f },
            new Wave { directionDegrees = 35f, wavelength = 13f, steepness = 0.10f },
            new Wave { directionDegrees = -50f, wavelength = 7f, steepness = 0.08f },
            new Wave { directionDegrees = 80f, wavelength = 3.5f, steepness = 0.05f },
        };

        static readonly int WaterLevelId = Shader.PropertyToID("_NyxWaterLevel");
        static readonly int WaveTimeId = Shader.PropertyToID("_NyxWaveTime");
        static readonly int WavesId = Shader.PropertyToID("_NyxWaves");

        static readonly Vector4[] s_Packed = new Vector4[WaveCount];
        static float s_WaterLevel;
        static float s_MaxHorizontal;
        static float s_MaxVertical;
        static float s_MaxSlope;

        static OceanWaves() => SetFlat(0f);

        /// <summary>Composant actif (le dernier activé).</summary>
        public static OceanWaves Active { get; private set; }

        /// <summary>Levé quand le niveau ou les vagues changent : les champs instanciés recalculent leurs bounds.</summary>
        public static event Action Changed;

        public static float WaterLevel => s_WaterLevel;

        /// <summary>Temps des vagues, calculé à la demande : CPU et GPU voient la même valeur dans une frame, quel que soit l'ordre des scripts.</summary>
        public static float WaveTime => MeshSpawner.CurrentTime * (Active != null ? Active.timeScale : 1f);

        /// <summary>Somme des amplitudes : déplacement horizontal / vertical maximal possible (m).</summary>
        public static float MaxHorizontalDisplacement => s_MaxHorizontal;
        public static float MaxVerticalDisplacement => s_MaxVertical;

        /// <summary>Pente maximale de la surface (somme des cambrures).</summary>
        public static float MaxSlope => s_MaxSlope;

        void OnEnable()
        {
            Active = this;
            Pack();
            Changed?.Invoke();
        }

        void OnDisable()
        {
            if (Active != this)
                return;
            Active = null;
            SetFlat(0f);
            Changed?.Invoke();
        }

        void OnValidate()
        {
            if (Active != this)
                return;
            Pack();
            Changed?.Invoke();
        }

        void Update()
        {
            if (Active == this)
                PushGlobals();
        }

        /// <summary>Pose les paramètres de houle sur un compute shader (et rafraîchit les globales des shaders).</summary>
        public static void ApplyTo(ComputeShader shader)
        {
            float time = WaveTime;
            PushGlobals(time);
            shader.SetFloat(WaterLevelId, s_WaterLevel);
            shader.SetFloat(WaveTimeId, time);
            shader.SetVectorArray(WavesId, s_Packed);
        }

        static void PushGlobals() => PushGlobals(WaveTime);

        static void PushGlobals(float time)
        {
            Shader.SetGlobalFloat(WaterLevelId, s_WaterLevel);
            Shader.SetGlobalFloat(WaveTimeId, time);
            Shader.SetGlobalVectorArray(WavesId, s_Packed);
        }

        void Pack()
        {
            s_WaterLevel = waterLevel;
            s_MaxHorizontal = 0f;
            s_MaxVertical = 0f;
            s_MaxSlope = 0f;
            for (int i = 0; i < WaveCount; i++)
            {
                Wave wave = waves != null && i < waves.Length ? waves[i] : default;
                float wavelength = Mathf.Max(wave.wavelength, 0.1f);
                float steepness = Mathf.Clamp01(wave.steepness);
                float radians = wave.directionDegrees * Mathf.Deg2Rad;
                s_Packed[i] = new Vector4(Mathf.Cos(radians), Mathf.Sin(radians), steepness, wavelength);

                float amplitude = steepness * wavelength / (2f * Mathf.PI);
                s_MaxHorizontal += amplitude;
                s_MaxVertical += amplitude;
                s_MaxSlope += steepness;
            }
        }

        static void SetFlat(float level)
        {
            s_WaterLevel = level;
            s_MaxHorizontal = 0f;
            s_MaxVertical = 0f;
            s_MaxSlope = 0f;
            for (int i = 0; i < WaveCount; i++)
                s_Packed[i] = new Vector4(1f, 0f, 0f, 1f);   // cambrure 0 : aucune contribution, sans division par zéro
        }

        /// <summary>
        /// Déplacement de la particule d'eau au repos en (x, z), au temps courant des vagues.
        /// Identique à NyxWaveDisplacement (HLSL) : à utiliser pour faire flotter mines, bouées, bateaux.
        /// </summary>
        public static Vector3 SampleDisplacement(Vector2 xz, out Vector3 normal) => SampleDisplacement(xz, WaveTime, out normal);

        public static Vector3 SampleDisplacement(Vector2 xz, float time, out Vector3 normal)
        {
            Vector3 displacement = Vector3.zero;
            Vector3 tangent = new Vector3(1f, 0f, 0f);
            Vector3 binormal = new Vector3(0f, 0f, 1f);

            for (int i = 0; i < WaveCount; i++)
            {
                Vector4 w = s_Packed[i];
                float k = 2f * Mathf.PI / w.w;
                float c = Mathf.Sqrt(9.81f / k);
                float f = k * (w.x * xz.x + w.y * xz.y - c * time);
                float a = w.z / k;
                float s = Mathf.Sin(f);
                float co = Mathf.Cos(f);

                displacement += new Vector3(w.x * a * co, a * s, w.y * a * co);
                tangent += new Vector3(-w.x * w.x * w.z * s, w.x * w.z * co, -w.x * w.y * w.z * s);
                binormal += new Vector3(-w.x * w.y * w.z * s, w.y * w.z * co, -w.y * w.y * w.z * s);
            }

            normal = Vector3.Cross(binormal, tangent).normalized;
            return displacement;
        }

        /// <summary>Hauteur approchée de la surface à la verticale de 'position' (ignore le décalage horizontal).</summary>
        public static float SampleHeight(Vector3 position) =>
            s_WaterLevel + SampleDisplacement(new Vector2(position.x, position.z), out _).y;
    }
}
