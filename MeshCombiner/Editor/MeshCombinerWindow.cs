using UnityEngine;
using System.Collections.Generic;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MeshCombinerTool
{
#if UNITY_EDITOR
    
    /// <summary>
    /// Editor window that can either merge immediately or add a runtime helper. Preserves tags
    /// and copies colliders, now with correct world‑space alignment by parenting a proxy holder
    /// GameObject that mirrors the original transform.
    /// </summary>
    public class MeshCombinerWindow : EditorWindow
    {
        private const string TITLE = "Mesh Combiner";
        
        private bool  _stripBackFaces   = false;
        private bool  _stripMutualFaces = false;
        private float _mutualThreshold  = 0.01f;

        [MenuItem("Tools/Mesh Combiner", priority = 92)]
        private static void ShowWindow()
        {
            var win     = GetWindow<MeshCombinerWindow>(false, TITLE, true);
            win.minSize = new Vector2(460, 230);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Merge meshes of selected objects & children.", EditorStyles.boldLabel);

            _stripBackFaces   = EditorGUILayout.ToggleLeft("Strip inward‑facing triangles", _stripBackFaces);
            _stripMutualFaces = EditorGUILayout.ToggleLeft("Cull mutually facing triangles", _stripMutualFaces);
            if (_stripMutualFaces)
                _mutualThreshold = EditorGUILayout.Slider("Pair distance threshold", _mutualThreshold, 0f, 0.5f);
            
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Combine Selected", GUILayout.Height(40)))
                    CombineSelected(_stripBackFaces, _stripMutualFaces, _mutualThreshold);

                if (GUILayout.Button("Uncombine", GUILayout.Height(40)))
                    UncombineSelected();
            }

            GUILayout.FlexibleSpace();
        }

        /// <summary>
        /// Combines selected mesh objects into a single mesh, preserving their hierarchy and transform.
        /// </summary>
        /// <param name="removeBackFaces"></param>
        /// <param name="removeMutual"></param>
        /// <param name="threshold"></param>
        private static void CombineSelected(bool removeBackFaces, bool removeMutual, float threshold)
        {
            GameObject[] meshObjects = GatherMeshObjectsFromSelection();
            if (meshObjects.Length < 2)
            {
                EditorUtility.DisplayDialog(TITLE, "Select at least two mesh objects (including children).", "OK");
                return;
            }

            var hierarchy = MeshCombineUtility.CaptureHierarchy(meshObjects);
            
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Combine Meshes");

            Transform commonParent = CommonParentOfSelection();


            string rootName = BaseName(meshObjects, commonParent);   
            

            GameObject originalsGroupGO = new GameObject(rootName + "_oldMesh");
            Undo.RegisterCreatedObjectUndo(originalsGroupGO, "Create COMBINED group");
            Undo.SetTransformParent(originalsGroupGO.transform, commonParent, "Set COMBINED parent");
            
            
            
            foreach (var go in meshObjects)
            {
                if (!go) continue; 
                Undo.SetTransformParent(go.transform, originalsGroupGO.transform, "Set original parent");
            }
            
            
            GameObject combinedGO = new GameObject(rootName + "_combined");
            Undo.RegisterCreatedObjectUndo(combinedGO, "Create CombinedMesh");
            Undo.SetTransformParent(combinedGO.transform, commonParent, "Set CombinedMesh parent");
            combinedGO.isStatic = true;
            
            Undo.SetTransformParent(originalsGroupGO.transform, combinedGO.transform, "Set originals group parent to CombinedMesh");

            var cfg = new MeshCombineUtility.CombineSettings
            {
                stripBackFaces   = removeBackFaces,
                stripMutualFaces = removeMutual,
                mutualThreshold  = threshold
            };

            MeshCombineUtility.BuildCombinedMesh(
                combinedGO,
                meshObjects,
                cfg,
                copyColliders:true,                 
                out var allMats);

            // metadata for undo
            var meta = combinedGO.AddComponent<CombinedMeshData>();
            meta.originalObjects = meshObjects;
            meta.originalsGroup  = originalsGroupGO.transform;
            meta.hierarchy = hierarchy;

            
            Selection.activeGameObject = combinedGO;
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
        }

        /// <summary>
        /// Reverts a previously combined mesh selection back to its original hierarchy, transform, and activation state.
        /// </summary>
        void UncombineSelected()
        {
            var data = Selection.activeGameObject?.GetComponent<CombinedMeshData>();
            if (data != null)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Uncombine Meshes");
                
                MeshCombineUtility.RestoreHierarchy(
                    data.originalObjects,
                    data.hierarchy,
                    useEditorUndo: true);
                
                if (data.originalsGroup)
                    Undo.DestroyObjectImmediate(data.originalsGroup.gameObject);

                Undo.DestroyObjectImmediate(data.gameObject);
                Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
                return;
            }

            EditorUtility.DisplayDialog(
                TITLE,
                "Select a combined mesh object to uncombine",
                "OK");
        }

        private static GameObject[] GatherMeshObjectsFromSelection()
        {
            var set = new HashSet<GameObject>();
            foreach (var root in Selection.gameObjects)
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = mf.gameObject;
                if (!go.GetComponent<MeshRenderer>())    continue; // need renderer
                if (go.GetComponent<CombinedMeshData>()) continue; // skip already combined
                set.Add(go);
            }
            return set.ToArray();
        }

        /// <summary>
        /// Finds the common parent of all selected transforms, or the first selected transform's parent
        /// </summary>
        /// <returns>Common parent</returns>
        private static Transform CommonParentOfSelection()
        {
            if (Selection.transforms.Length == 0) return null;

            // Start with the first object’s parent and walk upward until all selections are its descendants.
            Transform candidate = Selection.transforms[0].parent;
            while (candidate != null)
            {
                bool everyoneUnderCandidate = Selection.transforms.All(t => t.IsChildOf(candidate));
                if (everyoneUnderCandidate) return candidate;
                candidate = candidate.parent;
            }

            // Fallback – keep them beside the first selected object
            return Selection.transforms[0].parent;
        }
        
        /// <summary>
        /// Generates a base name for the combined mesh based on the selected objects.
        /// </summary>
        /// <param name="meshObjects">Mesh objects</param>
        /// <param name="commonParent">Common parent of the objects</param>
        /// <returns>The chosen name for the to be merged objects</returns>
        private static string BaseName(GameObject[] meshObjects, Transform commonParent)
        {
            var selectedRoots = Selection.gameObjects;

            if ( commonParent != null 
                 && selectedRoots.Any(go => go.transform == commonParent) )
            {
                return commonParent.name;
            }

            return selectedRoots
                .OrderBy(go => {
                    int depth = 0;
                    for (var t = go.transform; t.parent != null; t = t.parent) depth++;
                    return depth;
                })
                .First().name;
        }


    }
#endif 
}
