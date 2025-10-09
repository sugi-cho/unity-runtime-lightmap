using UnityEngine;

public class TexturePropSetter : MonoBehaviour
{
    [SerializeField] string propertyName = "_MainTex";
    MaterialPropertyBlock _mpb;
    Renderer _renderer;
    public Texture texture
    {
        set
        {
            _mpb ??= new MaterialPropertyBlock();
            _renderer ??= GetComponent<Renderer>();
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetTexture(propertyName, value);
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
