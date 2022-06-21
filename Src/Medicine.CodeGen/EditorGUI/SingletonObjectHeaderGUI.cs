using System;
using Medicine;
using UnityEditor;
using UnityEngine;
using static System.Reflection.BindingFlags;
using Object = UnityEngine.Object;

[InitializeOnLoad]
static class SingletonObjectHeaderGUI
{
    private static readonly Type[] TypesWithInjectSingleAttribute;

    private static readonly GUIContent Label = new GUIContent(
        text: "Set as current active singleton instance",
        tooltip: "This adds the ScriptableObject to Preloaded Assets (in Player Settings), which makes it resolvable using [Inject.Single]."
    );

    static SingletonObjectHeaderGUI()
    {
        var types = TypeCache.GetTypesWithAttribute<Register.Single>();
        TypesWithInjectSingleAttribute = new Type[types.Count];
        types.CopyTo(TypesWithInjectSingleAttribute, 0);

        Editor.finishedDefaultHeaderGUI += DrawSingletonGUI;
    }

    static void DrawSingletonGUI(Editor editor)
    {
        var target = editor.target;

        if (!(target is ScriptableObject))
            return;

        var editorTargets = editor.targets;
        if (editorTargets.Length > 1)
            return;

        var type = target.GetType();

        bool hasInjectSingleAttr
            = Array.IndexOf(TypesWithInjectSingleAttribute, type) >= 0;

        if (!hasInjectSingleAttr)
            return;

        var preloadedAssets = PlayerSettings.GetPreloadedAssets();

        bool isAdded = false;

        foreach (var asset in preloadedAssets)
            if (asset == target)
                isAdded = true;

        var controlRect = EditorGUILayout.GetControlRect(
            hasLabel: false,
            height: 14,
            options: Array.Empty<GUILayoutOption>()
        );

        var oldColor = GUI.color;
        var color = oldColor;
        color.a = 0.8f;
        GUI.color = color;

        var shouldBeAdded = EditorGUI.ToggleLeft(
            position: controlRect,
            label: Label,
            value: isAdded
        );

        GUI.color = oldColor;

        if (shouldBeAdded == isAdded)
            return;

        var preloadedAssetsList = NonAlloc.GetList<Object>();

        // remove all objects of target object's type
        // (to avoid multiple instances of the same singleton type being registered)
        foreach (var asset in preloadedAssets)
            if (!asset || asset.GetType() != type)
                preloadedAssetsList.Add(asset);

        if (shouldBeAdded)
            preloadedAssetsList.Add(target);

        PlayerSettings.SetPreloadedAssets(preloadedAssetsList.ToArray());

        if (shouldBeAdded && Application.isPlaying)
        {
            // get generic instance of the singleton helper type
            var helper = typeof(RuntimeHelpers.Singleton<>).MakeGenericType(type);
            
            // get current singleton instance
            var currentInstance = helper.GetField("instance", Static | NonPublic)?.GetValue(null);

            // unregister current singleton instance
            if (currentInstance as Object)
                helper.GetMethod("UnregisterInstance", Static | Public)?.Invoke(null, new[] { currentInstance });

            // replace with new singleton instance
            helper.GetMethod("RegisterInstance", Static | Public)?.Invoke(null, new object[] {target});
        }
    }
}
