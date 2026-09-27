using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class Phase1ValidationMenu
    {
        public static string SourceFingerprint()
        {
            using (var hash = SHA256.Create())
            using (var stream = new MemoryStream())
            {
                foreach (string path in Directory.GetFiles("Assets/RVO", "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    byte[] name = Encoding.UTF8.GetBytes(path.Replace('\\', '/') + "\n"); stream.Write(name, 0, name.Length);
                    byte[] contents = File.ReadAllBytes(path); stream.Write(contents, 0, contents.Length);
                }
                return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        [MenuItem("Tools/RVO/Run Selected Profile Benchmark (explicit)")]
        public static void RunSelected()
        {
            var profile = Selection.activeObject as SimulationProfile;
            if (profile == null) throw new InvalidOperationException("请先选择 SimulationProfile；此菜单会按配置执行完整测量。");
            string directory = Path.Combine("Verification", "Manual", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
            var report = BenchmarkRunner.Run(profile.Simulation, profile.Scenario, profile.Benchmark, directory, SourceFingerprint());
            File.Copy("Packages/manifest.json", Path.Combine(directory, "manifest.json"));
            File.Copy("Packages/packages-lock.json", Path.Combine(directory, "packages-lock.json"));
            Debug.Log($"RVO report: {Path.GetFullPath(directory)} | P50 {report.P50Ms:F3} ms | collision ticks {report.CollisionTicks}");
        }
        [MenuItem("Tools/RVO/Run Selected Profile Benchmark (explicit)", true)]
        public static bool CanRunSelected() => Selection.activeObject is SimulationProfile && !EditorApplication.isPlaying;
    }
}
