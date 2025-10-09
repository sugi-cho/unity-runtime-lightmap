using UnityEngine;

[ExecuteAlways]
public class TextureBlitter : MonoBehaviour
{
    public Texture Texture { set { sourceTexture = value; } }
    Texture sourceTexture;
    [SerializeField]RenderTexture targetTexture;

    // Update is called once per frame
    void Update()
    {
        if (sourceTexture == null || targetTexture == null)
            return;
        Graphics.Blit(sourceTexture, targetTexture);
    }
}
