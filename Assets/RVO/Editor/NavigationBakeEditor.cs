using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class NavigationBakeEditor
    {
        [MenuItem("Tools/RVO/Bake Selected Navigation Profile")]
        public static void BakeSelected() => Bake(Selection.activeObject as SimulationProfile, false);
        [MenuItem("Tools/RVO/Generate New Map and Bake Selected Profile")]
        public static void GenerateSelected() => Bake(Selection.activeObject as SimulationProfile, true);
        public static void Bake(SimulationProfile profile, bool newSeed)
        {
            if (Application.isPlaying) throw new InvalidOperationException("请退出 Play 后生成/烘焙；本次寻路过程中地图保持不变。");
            if (profile == null || !profile.Navigation.Enabled) throw new InvalidOperationException("请选择启用 Navigation 的 SimulationProfile。");
            string profilePath = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(profilePath)) throw new InvalidOperationException("请先将 Profile 保存为项目资产。");
            var navigation = profile.Navigation;
            if (newSeed) { navigation.Seed++; if (navigation.Seed == 0) navigation.Seed = 1; }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var map = NavigationBake.Generate(navigation, profile.Scenario.Radius);
            byte[] bytes = NavigationBake.Encode(map, navigation.BakeSignature(profile.Scenario.Radius));
            timer.Stop();
            string root = Path.ChangeExtension(profilePath, null) + "_Map";
            // 先完整生成并编码，再替换资产；烘焙失败不会破坏现有地图和 seed。
            string temporary = root + ".bytes.tmp"; File.WriteAllBytes(temporary, bytes);
            if (File.Exists(root + ".bytes")) File.Replace(temporary, root + ".bytes", null);
            else File.Move(temporary, root + ".bytes");
            AssetDatabase.ImportAsset(root + ".bytes", ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadAssetAtPath<BakedNavigationMap>(root + ".asset");
            if (asset == null) { asset = ScriptableObject.CreateInstance<BakedNavigationMap>(); AssetDatabase.CreateAsset(asset, root + ".asset"); }
            asset.SetData(AssetDatabase.LoadAssetAtPath<TextAsset>(root + ".bytes"), map, timer.Elapsed.TotalMilliseconds);
            profile.Navigation = navigation; profile.BakedMap = asset;
            EditorUtility.SetDirty(asset); EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
            Debug.Log($"Navigation baked: {asset.Summary} | {bytes.Length / 1048576f:F2} MiB | {root}.asset", profile);
        }
    }

    [CustomEditor(typeof(SimulationProfile))]
    public sealed class NavigationProfileInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector(); var profile = (SimulationProfile)target;
            if (profile.Simulation.Dimension == SimulationDimension.Full3D)
            {
                EditorGUILayout.HelpBox("Phase 3: baked XYZ volume, 16 / 256 / 1024 agents. None means static-only; ORCA enables dynamic avoidance.", MessageType.Info);
                if (profile.BakedVolume != null) EditorGUILayout.LabelField(profile.BakedVolume.Summary, EditorStyles.wordWrappedLabel);
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                    if (GUILayout.Button("Bake XYZ demo volume"))
                        try { Phase3DemoBuilder.Bake(profile); } catch (Exception error) { Debug.LogException(error, profile); }
                return;
            }
            if (!profile.Navigation.Enabled) return;
            EditorGUILayout.HelpBox("流程：设置地图、半径与三档数量 → Generate + Bake → Play。修改地图或半径后必须重烘焙，运行中不刷新障碍。", MessageType.Info);
            if (profile.BakedMap != null) EditorGUILayout.LabelField(profile.BakedMap.Summary, EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Bake 当前 seed")) Bake(profile, false);
                if (GUILayout.Button("Generate + Bake 新 seed")) Bake(profile, true);
            }
        }
        private static void Bake(SimulationProfile profile, bool newSeed)
        { try { NavigationBakeEditor.Bake(profile, newSeed); } catch (Exception error) { Debug.LogException(error, profile); } }
    }
}
