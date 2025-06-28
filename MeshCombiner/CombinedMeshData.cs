using System;
using UnityEngine;

namespace MeshCombinerTool
{
    /// <summary>
    /// Stores metadata for meshes merged in the editor, to allow perfect unmerge.
    /// All original GameObjects are destroyed on Awake
    /// </summary>
    public class CombinedMeshData : MonoBehaviour
    {
        public GameObject[] originalObjects;
        public Transform   originalsGroup;
        public MeshCombineUtility.HierarchySnapshot[] hierarchy;


        public void Awake()
        {
            for (int i = originalObjects.Length - 1; i >= 0; i--)
            {
                Destroy(originalObjects[i]);
            }
        }
    }
}