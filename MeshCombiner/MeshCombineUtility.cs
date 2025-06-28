    using System.Collections.Generic;
    using System.Linq;
    #if UNITY_EDITOR
    using UnityEditorInternal;
    #endif
    using UnityEngine;

    namespace MeshCombinerTool
    {
        public static class MeshCombineUtility
        {
            
            /// <summary>
            /// Options that influence the final mesh which are given by MeshCombinerWindow.cs or RuntimeMeshCombiner.cs.
            /// </summary>
            public struct CombineSettings
            {
                public bool  stripBackFaces;
                public bool  stripMutualFaces;
                public float mutualThreshold;
            }
            
            /// <summary>
            /// Snapshot of the hierarchy of a GameObject, used to restore the original hierarchy after combining.
            /// </summary>
            [System.Serializable]
            public struct HierarchySnapshot
            {
                public Transform  parent;
                public int        siblingIndex;
                public Vector3    localPosition;
                public Quaternion localRotation;
                public Vector3    localScale;
            }

            /// <summary>
            /// Combines <paramref name="sourceObjects"/> into <paramref name="target"/> and hides the sources.
            /// Colliders are replicated (world-aligned) if <paramref name="copyColliders"/> is requested.
            /// </summary>
            public static void BuildCombinedMesh(
                GameObject           target,
                IEnumerable<GameObject> sourceObjects,
                CombineSettings      settings,
                bool                 copyColliders,
                out Material[]       outMaterials)
            {
                // gather meshes
                var combine = new List<CombineInstance>();
                var mats    = new List<Material>();

                foreach (var go in sourceObjects.Where(o => o))
                {
                    var mf = go.GetComponent<MeshFilter>();
                    var mr = go.GetComponent<MeshRenderer>();
                    if (!mf || !mr) continue;

                    var mesh = mf.sharedMesh;
                    for (int s = 0; s < mesh.subMeshCount; ++s)
                        combine.Add(new CombineInstance
                        {
                            mesh         = mesh,
                            transform    = mf.transform.localToWorldMatrix,
                            subMeshIndex = s
                        });

                    mats.AddRange(mr.sharedMaterials);
                    go.SetActive(false);                      
                }

                // build unified mesh
                var merged = new Mesh { name = $"{target.name}_Combined" };
                merged.CombineMeshes(combine.ToArray(), false, true, false);

                if (settings.stripBackFaces)
                    StripInwardFacing(ref merged);
                if (settings.stripMutualFaces)
                    StripMutuallyFacing(ref merged, settings.mutualThreshold);

                // push to target
                var mfTarget = target.AddComponent<MeshFilter>();
                mfTarget.sharedMesh = merged;

                var mrTarget = target.AddComponent<MeshRenderer>();
                mrTarget.sharedMaterials = mats.ToArray();
                
                outMaterials = mats.ToArray();
                
                // duplicate colliders 
                #if UNITY_EDITOR
                if (copyColliders)
                {
                    foreach (var go in sourceObjects)
                    foreach (var col in go.GetComponents<Collider>())
                    {
                        var holder = new GameObject($"{col.GetType().Name}_Col");
                        holder.transform.SetPositionAndRotation(col.transform.position, col.transform.rotation);
                        holder.transform.localScale = col.transform.lossyScale;
                        holder.transform.SetParent(target.transform, true);

                        ComponentUtility.CopyComponent(col);
                        ComponentUtility.PasteComponentAsNew(holder);
                    }
                }
                #endif
                
                // unify tag if they all match
                var firstTag = sourceObjects.FirstOrDefault()?.tag;
                if (firstTag != null && sourceObjects.All(o => o.CompareTag(firstTag)))
                    target.tag = firstTag;
                
                // unify layer if they all match
                var firstLayer = sourceObjects.FirstOrDefault()?.layer;
                if (firstLayer != null && sourceObjects.All(o => o.layer == firstLayer))
                    target.layer = firstLayer.Value;
                
            }
            
            
            /// <summary>
            /// Strips triangles that are facing inward, i.e. those that point towards the average centre of the mesh.
            /// </summary>
            /// <param name="mesh">The Mesh to perform the operation on</param>
            public static void StripInwardFacing(ref Mesh mesh)
            {
                var verts = mesh.vertices;
                var tris  = mesh.triangles;
                var keep  = new List<int>(tris.Length);

                Vector3 centre = verts.Length > 0 ? verts.Aggregate(Vector3.zero, (a, v) => a + v) / verts.Length : Vector3.zero;
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 v0      = verts[tris[i]];
                    Vector3 v1      = verts[tris[i + 1]];
                    Vector3 v2      = verts[tris[i + 2]];
                    Vector3 normal  = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                    Vector3 triCent = (v0 + v1 + v2) / 3f;
                    if (Vector3.Dot(normal, (triCent - centre).normalized) > 0f)
                    {
                        keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]);
                    }
                }
                mesh.triangles = keep.ToArray();
                mesh.RecalculateBounds(); mesh.RecalculateNormals();
            }

            /// <summary>
            /// Strips triangles that are mutually facing, i.e. those that have nearly opposite normals and are close to each other.
            /// </summary>
            /// <param name="mesh">The Mesh to perform the operation on</param>
            /// <param name="maxDistance">Maximum distance the faces can be to be merged</param>
            public static void StripMutuallyFacing(ref Mesh mesh, float maxDistance)
            {
                var verts    = mesh.vertices;
                var tris     = mesh.triangles;
                int triCount = tris.Length / 3;
                var centers  = new Vector3[triCount];
                var normals  = new Vector3[triCount];

                // Precompute centres & normals
                for (int i = 0; i < triCount; i++)
                {
                    int i0 = tris[i * 3];
                    int i1 = tris[i * 3 + 1];
                    int i2 = tris[i * 3 + 2];

                    var v0 = verts[i0];
                    var v1 = verts[i1];
                    var v2 = verts[i2];

                    centers[i] = (v0 + v1 + v2) / 3f;
                    normals[i] = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                }

                var remove = new bool[triCount];
                for (int i = 0; i < triCount; i++)
                {
                    if (remove[i]) continue;
                    for (int j = i + 1; j < triCount; j++)
                    {
                        if (remove[j]) continue;

                        // Check for nearly opposite normals and close centres
                        if (Vector3.Dot(normals[i], normals[j]) < -0.999f &&
                            Vector3.Distance(centers[i], centers[j]) <= maxDistance)
                        {
                            remove[i] = remove[j] = true;
                            break;
                        }
                    }
                }

                // Build new triangle list
                var keep = new List<int>(tris.Length);
                for (int i = 0; i < triCount; i++)
                {
                    if (remove[i]) continue;
                    keep.Add(tris[i * 3]);
                    keep.Add(tris[i * 3 + 1]);
                    keep.Add(tris[i * 3 + 2]);
                }

                mesh.triangles = keep.ToArray();
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
            }
            
            /// <summary>
            /// Record parent, sibling-order and local transform for each object.
            /// </summary>
            public static HierarchySnapshot[] CaptureHierarchy(GameObject[] objects)
            {
                HierarchySnapshot[] snaps = new HierarchySnapshot[objects.Length];
                for (int i = 0; i < objects.Length; i++)
                {
                    Transform t = objects[i].transform;
                    snaps[i] = new HierarchySnapshot
                    {
                        parent        = t.parent,
                        siblingIndex  = t.GetSiblingIndex(),
                        localPosition = t.localPosition,
                        localRotation = t.localRotation,
                        localScale    = t.localScale
                    };
                }
                return snaps;
            }

            /// <summary>
            /// Restore everything that <see cref="CaptureHierarchy"/> stored.
            /// </summary>
            public static void RestoreHierarchy(GameObject[] objects, HierarchySnapshot[] snaps, bool useEditorUndo = false)
            {
                int n = Mathf.Min(objects.Length, snaps.Length);
                for (int i = 0; i < n; i++)
                {
                    GameObject go = objects[i];
                    if (!go) continue;

                    HierarchySnapshot s = snaps[i];
                    Transform t = go.transform;
                    go.SetActive(true);
#if UNITY_EDITOR
                    if (useEditorUndo)
                    {
                        UnityEditor.Undo.RecordObject(t, "Restore Parent");
                        UnityEditor.Undo.SetTransformParent(t, s.parent, "Restore Parent");
                    }
                    else
#endif
                        t.SetParent(s.parent, worldPositionStays: true);

                    t.SetSiblingIndex(s.siblingIndex);
                    t.localPosition = s.localPosition;
                    t.localRotation = s.localRotation;
                    t.localScale    = s.localScale;
                }
            }
        }
    }