using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Exemple d'utilisation de BubbleColumns.Emit : expirations périodiques depuis un point mobile
    /// (détendeur d'un plongeur, échappement d'un drone sous-marin...). À poser sur l'objet émetteur.
    /// </summary>
    [AddComponentMenu("Nyx/GPU Instancing/Bubble Burst Emitter")]
    public sealed class BubbleBurstEmitter : MonoBehaviour
    {
        [SerializeField] BubbleColumns field;
        [Tooltip("Secondes entre deux expirations (un plongeur : environ 4 à 5 s).")]
        [SerializeField, Min(0.05f)] float interval = 4.5f;
        [Tooltip("Variation aléatoire de l'intervalle (s).")]
        [SerializeField, Min(0f)] float intervalJitter = 0.8f;
        [Tooltip("Bulles par expiration.")]
        [SerializeField, Min(1)] int bubblesPerBurst = 14;
        [Tooltip("Durée d'une expiration (s) : les bulles sont étalées sur cette durée.")]
        [SerializeField, Min(0f)] float burstDuration = 1.2f;
        [Tooltip("Rayon de dispersion au point d'émission (m).")]
        [SerializeField, Min(0f)] float spread = 0.03f;

        float _nextBurst;
        float _burstEnd;
        float _emitAccumulator;

        void OnEnable() => _nextBurst = Time.time + Random.Range(0f, interval);

        void Update()
        {
            if (field == null)
                return;

            float now = Time.time;
            if (now >= _nextBurst)
            {
                _burstEnd = now + burstDuration;
                _nextBurst = now + interval + Random.Range(-intervalJitter, intervalJitter);
            }

            if (now > _burstEnd)
                return;

            // Débit constant pendant l'expiration : quelques bulles par frame, jamais de pic d'upload
            float rate = bubblesPerBurst / Mathf.Max(burstDuration, 0.01f);
            _emitAccumulator += rate * Time.deltaTime;
            int count = (int)_emitAccumulator;
            if (count > 0)
            {
                _emitAccumulator -= count;
                field.Emit(transform.position, count, spread);
            }
        }
    }
}
