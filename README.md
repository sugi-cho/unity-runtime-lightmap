# Unity Real-time Light Baker for URP

This is a utility for baking dynamic lighting into lightmaps at runtime within Unity's Universal Render Pipeline (URP).

It provides a `RuntimeLightmapBaker` component that, when combined with the `RealTimeLightBakerFeature` render feature, allows you to bake lighting for specified renderers on-demand or every frame.

## Features

*   Bake dynamic lights (main and additional) into a renderer's second UV set (UV2).
*   Apply the baked lightmap automatically to the material.
*   Optional dilation pass to reduce seams and artifacts.
*   Live preview of the baked lightmap in the inspector.

## Target Environment

*   **Unity Version**: Unity 6 or newer
*   **URP Version**: 17.2.0 or newer

This tool is designed exclusively for the **RenderGraph** rendering path in URP. Support for older, non-RenderGraph paths has been removed for simplicity and performance.

## How to Use

1.  Add the `RealTimeLightBakerFeature` to your active URP Renderer Data asset.
2.  Assign the required materials (`BakeMat`, `UVDilation`) to the fields in the feature's settings.
3.  Add the `RuntimeLightmapBaker` component to a GameObject in your scene.
4.  In the `RuntimeLightmapBaker` component's inspector, add the `Renderer` components you wish to bake lighting for to the `Targets` list.
5.  Configure the lightmap size for each target.
6.  Enable `Bake Every Frame` for continuous updates, or call the `RequestBake()` method from a script to trigger a bake on-demand.
7.  The baked lightmap will be applied to the renderer's material using a `MaterialPropertyBlock`. Ensure your object's shader can read a lightmap texture from a property named `_RuntimeLightmap`.
