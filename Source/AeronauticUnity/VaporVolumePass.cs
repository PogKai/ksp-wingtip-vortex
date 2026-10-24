using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VortexVapor
{
    // After aeronautic db04669b: shared volume target and depth-aware upsampling.
    public static class VaporVolumePass
    {
        struct Draw { public Mesh Mesh; public Matrix4x4 Matrix; public Material Material; public bool Full; }
        sealed class Pass
        {
            public Camera Camera;
            public CommandBuffer Commands;
            public Material Composite;
            public readonly List<Draw> Draws = new List<Draw>();
            public int Frame = -1;
            public int LastDivisor;
            public int Divisor, Width, Height;
        }
        static readonly Dictionary<Camera, Pass> passes = new Dictionary<Camera, Pass>();
        static readonly int volume = Shader.PropertyToID("_WVVolume");
        static Mesh screen;
        public static bool Enabled = true;
        public static bool ProtectThinRopes = true;
        public static Shader CompositeShader;

        public static bool Submit(Camera camera, Mesh mesh, Matrix4x4 matrix, Material material, bool protectRopes)
        {
            if (!Enabled) return false;
            if (CompositeShader == null) CompositeShader = Shader.Find("WingtipVortex/VaporComposite");
            if (CompositeShader == null || !CompositeShader.isSupported) return false;
            Pass pass;
            if (!passes.TryGetValue(camera, out pass))
            {
                pass = new Pass { Camera = camera, Commands = new CommandBuffer { name = "Wing vapor volume pass" },
                    Composite = new Material(CompositeShader) };
                passes.Add(camera, pass);
                camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, pass.Commands);
                if (passes.Count == 1) { Camera.onPreCull += ClearStale; Camera.onPostRender += LogTarget; }
                Debug.Log("[VORTEX] wing vapor: shared adaptive depth-aware volume pass on " + camera.name);
            }
            if (pass.Frame != Time.frameCount) { pass.Draws.Clear(); pass.Frame = Time.frameCount; }
            var entry = new Draw { Mesh = mesh, Matrix = matrix, Material = material, Full = protectRopes };
            int existing = pass.Draws.FindIndex(draw => draw.Mesh == mesh);
            if (existing >= 0) pass.Draws[existing] = entry; else pass.Draws.Add(entry);
            bool full = ProtectThinRopes && pass.Draws.Exists(draw => draw.Full);
            int width = Mathf.Max(1, full ? camera.pixelWidth : (camera.pixelWidth + 1) / 2);
            int height = Mathf.Max(1, full ? camera.pixelHeight : (camera.pixelHeight + 1) / 2);
            pass.Divisor = full ? 1 : 2;
            pass.Width = width;
            pass.Height = height;
            var cb = pass.Commands;
            cb.Clear();
            cb.GetTemporaryRT(volume, width, height, 0, FilterMode.Point,
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
            cb.SetRenderTarget(volume);
            cb.ClearRenderTarget(false, true, Color.clear);
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            cb.SetViewProjectionMatrices(camera.worldToCameraMatrix, projection);
            cb.SetInvertCulling(projection.m11 * camera.projectionMatrix.m11 < 0);
            cb.SetGlobalFloat("_VaporDepthFlip", SystemInfo.graphicsUVStartsAtTop ? 1 : 0);
            foreach (Draw draw in pass.Draws) cb.DrawMesh(draw.Mesh, draw.Matrix, draw.Material, 0, 0);
            cb.SetInvertCulling(false);
            cb.SetGlobalFloat("_VaporDepthFlip", 0);
            cb.SetGlobalVector("_WVVolumeSize", new Vector4(width, height, 1f / width, 1f / height));
            cb.SetGlobalTexture("_WVVolumeTexture", volume);
            if (screen == null)
            {
                screen = new Mesh { name = "Vapor fullscreen quad", vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(-1,1,0), new Vector3(1,1,0) },
                    uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(0,1), new Vector2(1,1) }, triangles = new[] { 0,2,1, 2,3,1 } };
            }
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cb.DrawMesh(screen, Matrix4x4.identity, pass.Composite, 0, 0);
            cb.ReleaseTemporaryRT(volume);
            return true;
        }

        static void ClearStale(Camera camera)
        {
            Pass pass;
            if (passes.TryGetValue(camera, out pass) && pass.Frame != Time.frameCount) { pass.Commands.Clear(); pass.Draws.Clear(); }
        }

        static void LogTarget(Camera camera)
        {
            Pass pass;
            if (!passes.TryGetValue(camera, out pass) || pass.Frame != Time.frameCount || pass.LastDivisor == pass.Divisor) return;
            pass.LastDivisor = pass.Divisor;
            Debug.Log("[VORTEX] wing vapor: volume target " + pass.Width + "x" + pass.Height + (pass.Divisor == 1 ? " (thin-rope protection)" : " (half resolution)"));
        }

        public static void Release()
        {
            Camera.onPreCull -= ClearStale;
            Camera.onPostRender -= LogTarget;
            foreach (Pass pass in passes.Values)
            {
                if (pass.Camera != null) pass.Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, pass.Commands);
                pass.Commands.Release();
                if (Application.isPlaying) Object.Destroy(pass.Composite); else Object.DestroyImmediate(pass.Composite);
            }
            passes.Clear();
            if (screen != null) { if (Application.isPlaying) Object.Destroy(screen); else Object.DestroyImmediate(screen); }
            screen = null;
        }
    }
}
