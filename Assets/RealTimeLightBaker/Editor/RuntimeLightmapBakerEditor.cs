using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RealTimeLightBaker.Editor
{
    [CustomEditor(typeof(RuntimeLightmapBaker))]
    public sealed class RuntimeLightmapBakerEditor : UnityEditor.Editor
    {
        private const float MaxPreviewHeight = 256f;

        private static bool s_ShowPreview = true;

        private FieldInfo _targetsField;
        private FieldInfo _lightmapField;
        private FieldInfo _rendererField;

        private void OnEnable()
        {
            var bakerType = typeof(RuntimeLightmapBaker);
            _targetsField = bakerType.GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic);
            var entryType = bakerType.GetNestedType("TargetEntry", BindingFlags.NonPublic);
            if (entryType == null)
            {
                return;
            }

            // Runtime data lives on the nested TargetEntry; cache accessors once.
            _lightmapField = entryType.GetField("lightmap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _rendererField = entryType.GetField("renderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            DrawRuntimeLightmapPreview();
        }

        private void DrawRuntimeLightmapPreview()
        {
            if (_targetsField == null || _lightmapField == null)
            {
                return;
            }

            var baker = (RuntimeLightmapBaker)target;
            if (baker == null)
            {
                return;
            }

            var entries = _targetsField.GetValue(baker) as IEnumerable;
            if (entries == null)
            {
                return;
            }

            EditorGUILayout.Space();
            s_ShowPreview = EditorGUILayout.BeginFoldoutHeaderGroup(s_ShowPreview, "Runtime Lightmap Preview");
            if (!s_ShowPreview)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            bool anyPreviewDrawn = false;
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                var lightmap = _lightmapField.GetValue(entry) as RenderTexture;
                if (lightmap == null || !lightmap.IsCreated())
                {
                    continue;
                }

                anyPreviewDrawn = true;
                var renderer = _rendererField?.GetValue(entry) as Renderer;
                string label = renderer != null ? renderer.name : "Target";

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    float aspect = Mathf.Approximately(lightmap.height, 0f) ? 1f : (float)lightmap.width / lightmap.height;
                    Rect previewRect = GUILayoutUtility.GetAspectRect(aspect, GUILayout.MaxHeight(MaxPreviewHeight));
                    EditorGUI.DrawPreviewTexture(previewRect, lightmap, null, ScaleMode.ScaleToFit);
                    EditorGUILayout.ObjectField("RenderTexture", lightmap, typeof(RenderTexture), false);
                }
            }

            if (!anyPreviewDrawn)
            {
                EditorGUILayout.HelpBox("Runtime lightmaps are not available yet. They will refresh after the next bake.", MessageType.Info);
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            if (Event.current.type == EventType.Repaint)
            {
                Repaint();
            }
        }
    }
}

