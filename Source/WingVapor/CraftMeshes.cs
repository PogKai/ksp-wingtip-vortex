using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    static class CraftMeshes
    {
        public struct Surface { public Vector3[] Vertices; public int[] Triangles; public Part Owner; public string Name; }

        // A renderer's mesh in its own frame, read once; ToWorld is taken again on each use.
        public class Cached { public Renderer Renderer; public Part Owner; public Vector3[] Local; public int[] Triangles; public SkinnedMeshRenderer Skin; }
        public struct Raw { public Vector3[] Local; public int[] Triangles; public Matrix4x4 ToWorld; }

        // The static meshes of every renderer worth reading, kept until the craft changes (skinned ones are baked each time, in Read).
        public static List<Cached> Collect(Vessel vessel)
        {
            var list = new List<Cached>();
            foreach (Part part in vessel.parts)
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(false))
                {
                    if (renderer.GetComponentInParent<Part>() != part) continue;
                    if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null && renderer.sharedMaterial.shader.name.StartsWith("Waterfall")) continue;
                    var skin = renderer as SkinnedMeshRenderer;
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = skin != null ? skin.sharedMesh : renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
                    if (mesh == null || (skin == null && !mesh.isReadable)) continue;
                    list.Add(skin != null ? new Cached { Renderer = renderer, Owner = part, Skin = skin }
                                          : new Cached { Renderer = renderer, Owner = part, Local = mesh.vertices, Triangles = mesh.triangles });
                }
            return list;
        }

        // Main thread: only the matrices are read; the vertices are moved later, on the worker.
        public static List<Raw> Snapshot(List<Cached> cache, Vector3 up)
        {
            var raw = new List<Raw>(cache.Count);
            foreach (Cached c in cache)
            {
                if (c.Renderer == null || c.Owner == null || !c.Renderer.enabled || BDArmoryCraft.IsWingStore(c.Owner, up)) continue;
                if (c.Skin == null) { raw.Add(new Raw { Local = c.Local, Triangles = c.Triangles, ToWorld = c.Renderer.transform.localToWorldMatrix }); continue; }
                Mesh baked = new Mesh();
                try
                {
                    c.Skin.BakeMesh(baked);
                    Vector3[] vertices = baked.vertices;
                    Transform t = c.Renderer.transform;
                    for (int i = 0; i < vertices.Length; i++) vertices[i] = t.position + t.rotation * vertices[i];
                    raw.Add(new Raw { Local = vertices, Triangles = baked.triangles, ToWorld = Matrix4x4.identity });
                }
                finally { Object.Destroy(baked); }
            }
            return raw;
        }

        public static IEnumerable<Surface> Read(Vessel vessel, Vector3 up)
        {
            foreach (Part part in vessel.parts)
            {
                if (BDArmoryCraft.IsWingStore(part, up)) continue;
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(false))
                {
                    if (!renderer.enabled || renderer.GetComponentInParent<Part>() != part) continue;
                    if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null && renderer.sharedMaterial.shader.name.StartsWith("Waterfall")) continue;
                    var skin = renderer as SkinnedMeshRenderer;
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = skin != null ? skin.sharedMesh : renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
                    if (mesh == null || (skin == null && !mesh.isReadable)) continue;
                    Mesh baked = null;
                    Vector3[] vertices;
                    int[] triangles;
                    try
                    {
                        if (skin != null) { baked = new Mesh(); skin.BakeMesh(baked); mesh = baked; }
                        vertices = mesh.vertices;
                        triangles = mesh.triangles;
                        for (int i = 0; i < vertices.Length; i++)
                            vertices[i] = skin != null ? renderer.transform.position + renderer.transform.rotation * vertices[i] : renderer.transform.TransformPoint(vertices[i]);
                    }
                    finally { if (baked != null) Object.Destroy(baked); }
                    yield return new Surface { Vertices = vertices, Triangles = triangles, Owner = part, Name = renderer.name };
                }
            }
        }
    }
}
