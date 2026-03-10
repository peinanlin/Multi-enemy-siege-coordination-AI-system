using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SkinnedMeshHighlighter : MonoBehaviour
{
    [SerializeField] List<SkinnedMeshRenderer> meshesToHiglight;
    [SerializeField] Material originalMaterial;
    [SerializeField] Material highlightedMaterial;

    public void HighlightMesh(bool higlight)
    {
        foreach (var mesh in meshesToHiglight)
        {
            mesh.material = (higlight) ? highlightedMaterial : originalMaterial;
        }
    }
}
