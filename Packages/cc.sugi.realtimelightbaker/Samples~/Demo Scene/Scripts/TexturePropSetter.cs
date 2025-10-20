using UnityEngine;

public class TexturePropSetter : MonoBehaviour
{
    [SerializeField] private string propertyName = "_MainTex";
    [SerializeField] private Renderer targetRenderer;

    private MaterialPropertyBlock _mpb;

    public Texture texture
    {
        set
        {
            if (!EnsureRendererAvailable())
            {
                return;
            }

            _mpb ??= new MaterialPropertyBlock();
            targetRenderer.GetPropertyBlock(_mpb);

            if (value != null)
            {
                _mpb.SetTexture(propertyName, value);
            }
            else
            {
                _mpb.SetTexture(propertyName, null);
            }

            targetRenderer.SetPropertyBlock(_mpb);
        }
    }

    private bool EnsureRendererAvailable()
    {
        if (targetRenderer != null)
        {
            return true;
        }

        targetRenderer = GetComponent<Renderer>();
        if (targetRenderer != null)
        {
            return true;
        }

        Debug.LogWarning($"{nameof(TexturePropSetter)} on '{name}' requires a Renderer reference.", this);
        return false;
    }
}
