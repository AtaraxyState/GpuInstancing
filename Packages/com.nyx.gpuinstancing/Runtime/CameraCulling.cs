using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Caméra du culling, pour un rendu : plans du frustum (deux yeux en stéréo), distance max, LOD, taille mini à l'écran,
    /// extrusion le long de la lumière des ombres, multiplicateur VR. Posée pour les noyaux (InstancingCamera.hlsl) ; le même
    /// test de boîte rejette sur CPU le spawner entier (Sees).
    /// </summary>
    internal sealed class CameraCulling
    {
        readonly Vector4[] _planes = new Vector4[12];
        Vector4 _cameraPosition;
        Vector4 _cullParams;

        /// <summary>Caméra stéréo : deux jeux de plans (un par œil).</summary>
        public bool Stereo { get; private set; }

        /// <summary>Instances dessinées par instance visible : 2 en Single Pass Instanced, sinon 1.</summary>
        public uint Multiplier { get; private set; } = 1;

        /// <summary>Demi-vecteur d'extrusion le long de la lumière des ombres (0 sans ombres).</summary>
        public Vector3 ShadowSweep { get; private set; }

        /// <summary>Paramètres de 'camera' pour ce rendu.</summary>
        public void Setup(Camera camera, in CullingSettings settings)
        {
            Multiplier = InstanceMultiplier(camera, settings.stereoInstancing);
            Stereo = camera.stereoEnabled;
            float projectionScale;
            if (Stereo)
            {
                Matrix4x4 left = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 right = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                ExtractFrustumPlanes(left * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), 0);
                ExtractFrustumPlanes(right * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), 6);
                projectionScale = Mathf.Min(left.m11, right.m11);
            }
            else
            {
                Matrix4x4 projection = camera.projectionMatrix;
                ExtractFrustumPlanes(projection * camera.worldToCameraMatrix, 0);
                Array.Copy(_planes, 0, _planes, 6, 6);
                projectionScale = projection.m11;
            }

            float maxDistance = Mathf.Min(settings.maxDrawDistance, camera.farClipPlane);
            float sizeRatio = camera.orthographic ? 0f : settings.minScreenSize / Mathf.Max(projectionScale, 1e-4f);

            ShadowSweep = Vector3.zero;
            Light light = settings.shadowLight != null ? settings.shadowLight : RenderSettings.sun;
            if (settings.castShadows != ShadowCastingMode.Off && settings.shadowCullingExtrusion > 0f && light != null)
                ShadowSweep = light.transform.forward * (settings.shadowCullingExtrusion * 0.5f);

            Vector3 position = camera.transform.position;
            _cameraPosition = new Vector4(position.x, position.y, position.z, maxDistance * maxDistance);
            _cullParams = new Vector4(1f / (settings.lodBias * settings.lodBias), sizeRatio * sizeRatio, 0f, 0f);
        }

        /// <summary>Pose les paramètres du dernier Setup pour le noyau de culling (InstancingCamera.hlsl).</summary>
        public void Apply(ComputeShader shader)
        {
            shader.SetVectorArray(ShaderIds.FrustumPlanes, _planes);
            shader.SetInt(ShaderIds.PlaneSetCount, Stereo ? 2 : 1);
            shader.SetVector(ShaderIds.CameraPosition, _cameraPosition);
            shader.SetVector(ShaderIds.CullParams, _cullParams);
            shader.SetVector(ShaderIds.ShadowSweep, ShadowSweep);
            shader.SetInt(ShaderIds.InstanceMultiplier, (int)Multiplier);
        }

        /// <summary>
        /// Boîte monde à moins de 'maxDrawDistance' de la caméra et dans le frustum d'un des yeux, étirée le long de la lumière
        /// des ombres (= NyxCameraSeesBox). Faux : ni dispatch, ni draw pour ce spawner.
        /// </summary>
        public bool Sees(Bounds bounds, Vector3 cameraPosition, float maxDrawDistance)
        {
            bounds.center += ShadowSweep;
            bounds.extents += Abs(ShadowSweep);
            if (bounds.SqrDistance(cameraPosition) > maxDrawDistance * maxDrawDistance)
                return false;
            return AabbInsidePlanes(bounds.center, bounds.extents, 0) || (Stereo && AabbInsidePlanes(bounds.center, bounds.extents, 6));
        }

        /// <summary>
        /// En Single Pass Instanced, chaque instance est dessinée 2 fois (une par œil) : le nombre d'instances
        /// des arguments indirects doit être doublé à la main (SetInstanceMultiplier ne s'applique pas aux draws indirects).
        /// En multiview (Android : PICO, Quest), l'œil vient de gl_ViewID : pas de doublement.
        /// </summary>
        static uint InstanceMultiplier(Camera camera, StereoInstancingMode mode)
        {
            if (!camera.stereoEnabled)
                return 1;

            switch (mode)
            {
                case StereoInstancingMode.ForceSinglePassInstanced:
                    return 2;
                case StereoInstancingMode.ForceOff:
                    return 1;
                default:
                    if (SystemInfo.supportsMultiview)
                        return 1;
                    return XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass ? 1u : 2u;
            }
        }

        /// <summary>Plans de Gribb-Hartmann (convention OpenGL de Unity : z clip dans [-w, w]), normales vers l'intérieur.</summary>
        void ExtractFrustumPlanes(Matrix4x4 viewProjection, int offset)
        {
            Vector4 r0 = viewProjection.GetRow(0);
            Vector4 r1 = viewProjection.GetRow(1);
            Vector4 r2 = viewProjection.GetRow(2);
            Vector4 r3 = viewProjection.GetRow(3);
            _planes[offset + 0] = NormalizePlane(r3 + r0);   // gauche
            _planes[offset + 1] = NormalizePlane(r3 - r0);   // droite
            _planes[offset + 2] = NormalizePlane(r3 + r1);   // bas
            _planes[offset + 3] = NormalizePlane(r3 - r1);   // haut
            _planes[offset + 4] = NormalizePlane(r3 + r2);   // proche
            _planes[offset + 5] = NormalizePlane(r3 - r2);   // lointain
        }

        static Vector4 NormalizePlane(Vector4 plane)
        {
            float length = Mathf.Sqrt(plane.x * plane.x + plane.y * plane.y + plane.z * plane.z);
            return length > 0f ? plane / length : plane;
        }

        bool AabbInsidePlanes(Vector3 center, Vector3 extents, int offset)
        {
            for (int p = 0; p < 6; p++)
            {
                Vector4 plane = _planes[offset + p];
                float distance = plane.x * center.x + plane.y * center.y + plane.z * center.z + plane.w;
                float radius = Mathf.Abs(plane.x) * extents.x + Mathf.Abs(plane.y) * extents.y + Mathf.Abs(plane.z) * extents.z;
                if (distance < -radius)
                    return false;
            }
            return true;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
