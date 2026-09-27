using System;
using NUnit.Framework;
using Rvo.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Mathematics;
using UnityEngine;

namespace Rvo.Tests
{
    public sealed class DemoAndSurveyTests
    {
        [Test]
        public void DemoAssetsHaveValidProfilesMaterialAndSceneReferences()
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(Phase13DemoBuilder.ScenePath), Is.Not.Null);
            var scene = EditorSceneManager.OpenPreviewScene(Phase13DemoBuilder.ScenePath);
            try
            {
                SimulationBootstrap driver = null;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.TryGetComponent(out SimulationBootstrap found)) driver = found;
                Assert.That(driver, Is.Not.Null);
                Assert.That(driver.Presenter.AgentMaterial.shader.name, Is.EqualTo("RVO/Agent Colors"));
                Assert.That(driver.Presenter.ViewCamera, Is.Not.Null);
                Assert.That(driver.DemoProfiles.Length, Is.EqualTo(6));
                foreach (var profile in driver.DemoProfiles)
                {
                    profile.ValidateProfile();
                    using (var world = new SimulationWorld(profile.Simulation, profile.Scenario, Phase1ModuleFactory.Create(profile.Simulation)))
                        world.Step();
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [TestCase(ScenarioKind.Crossing, 4, 8)]
        [TestCase(ScenarioKind.CircleSwap, 16, 10)]
        [TestCase(ScenarioKind.OpposingGroups, 32, 12)]
        [TestCase(ScenarioKind.RandomCrowd, 32, 10)]
        public void VoScenarioSurveyPreservesNumericInvariants(ScenarioKind kind, int count, float extent)
        {
            var settings = Phase13Tests.Settings(count); settings.Avoidance = AvoidanceAlgorithm.VO;
            var scenario = ScenarioSettings.Default; scenario.Kind = kind; scenario.Extent = extent;
            int collisionTicks = 0, fallbackTicks = 0; float minimumGap = float.PositiveInfinity;
            QualityMetrics quality = default;
            using (var world = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
            {
                for (int tick = 0; tick < 900; tick++)
                {
                    world.Step();
                    quality = QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, settings.Epsilon);
                    if (quality.SweptCollisionPairs > 0) collisionTicks++;
                    if (quality.FallbackAgents > 0) fallbackTicks++;
                    minimumGap = math.min(minimumGap, quality.MinimumSeparation);
                    for (int i = 0; i < count; i++)
                    {
                        Assert.That(math.all(math.isfinite(world.Snapshot.Positions[i])), Is.True);
                        Assert.That(world.Snapshot.Positions[i].y, Is.Zero);
                        Assert.That(math.length(world.Snapshot.Velocities[i]), Is.LessThanOrEqualTo(scenario.MaxSpeed + 1e-5f));
                    }
                }
            }
            // 调查测试只断言数值契约；把原始质量结果交付，不能把稠密 VO 局限隐藏成“通过”。
            TestContext.WriteLine(FormattableString.Invariant(
                $"SURVEY {kind}: N={count}, ticks=900, arrived={quality.Arrived}, collisionTicks={collisionTicks}, fallbackTicks={fallbackTicks}, minGap={minimumGap:F6}"));
        }
    }
}
