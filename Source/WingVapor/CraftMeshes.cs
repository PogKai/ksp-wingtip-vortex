using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    static class CraftMeshes
    {
        public struct Surface { public Vector3[] Vertices; public int[] Triangles; public Part Owner; public string Name; }

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
