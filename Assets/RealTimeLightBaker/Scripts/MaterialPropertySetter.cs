using UnityEngine;

[ExecuteAlways]
public class MaterialPropertySetter : MonoBehaviour
{
    [SerializeField]
    MaterialFloatProperty[] floatProperties;
    [SerializeField]
    MaterialColorProperty[] colorProperties;
    [SerializeField]
    MaterialVectorProperty[] vectorProperties;
    [SerializeField]
    MaterialTextureProperty[] textureProperties;
    
    Renderer r => _r ??= GetComponent<Renderer>();
    private Renderer _r;
    MaterialPropertyBlock mpb => _mpb ??= new MaterialPropertyBlock();
    private MaterialPropertyBlock _mpb;

    void OnEnable()
    {
        SetProperties();
    }
    void OnValidate()
    {
        SetProperties();
    }
    void SetProperties()
    {
        if (r == null)
        {
            Debug.LogWarning($"{nameof(MaterialPropertySetter)} on '{name}' requires a Renderer component.", this);
            return;
        }
        r.GetPropertyBlock(mpb);
        foreach (var floatProp in floatProperties)
        {
            mpb.SetFloat(floatProp.propertyName, floatProp.value);
        }
        foreach (var colorProp in colorProperties)
        {
            mpb.SetColor(colorProp.propertyName, colorProp.value);
        }
        foreach (var vectorProp in vectorProperties)
        {
            mpb.SetVector(vectorProp.propertyName, vectorProp.value);
        }
        foreach (var textureProp in textureProperties)
        {
            mpb.SetTexture(textureProp.propertyName, textureProp.value);
        }
        r.SetPropertyBlock(mpb);
    }

    [System.Serializable]
    public struct MaterialFloatProperty
    {
        public string propertyName;
        public float value;
    }
    [System.Serializable]
    public struct MaterialColorProperty
    {
        public string propertyName;
        public Color value;
    }
    [System.Serializable]
    public struct MaterialVectorProperty
    {
        public string propertyName;
        public Vector4 value;
    }
    [System.Serializable]
    public struct MaterialTextureProperty
    {
        public string propertyName;
        public Texture value;
    }
}
