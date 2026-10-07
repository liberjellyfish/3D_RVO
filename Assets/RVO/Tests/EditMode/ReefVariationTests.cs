using System;
using NUnit.Framework;
using Rvo.Rendering;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Rvo.Tests
{
    public sealed class ReefVariationTests
    {
        [Test]
        public void PlannedNetworkHasTwelveSeparateConnectionsWithTwoDepthOptions()
        {
            var profile=AssetDatabase.LoadAssetAtPath<SimulationProfile>("Assets/RVO/Demo/OceanReef/ReefNavigation.asset");
            var map=profile.BakedVolume.Load(profile.Volume,profile.Scenario.VolumeClearanceRadius);
            Assert.That(map.Resolution,Is.EqualTo(192));
            Assert.That(profile.AgentCountTiers.z,Is.EqualTo(2048));
            Assert.That(profile.Scenario.VolumeSpawns,Is.EqualTo(VolumeSpawnPattern.DistributedRooms));
            var portals=Rvo.Editor.ReefRouteLayout.Portals();
            Assert.That(portals.Length,Is.EqualTo(12));
            foreach(var portal in portals)
            {
                var direction=portal.Axis==0 ? Vector3.right : Vector3.forward;
                foreach(float height in new[] { (-34+portal.BaffleY-6)*0.5f,(36+portal.BaffleY+6)*0.5f })
                {
                    var center=portal.Center(height);
                    Assert.That(map.SegmentClear(center-direction*6,center+direction*6),Is.True,$"Portal {portal.Axis}/{portal.Wall}/{portal.Lane} at y={height}");
                }
                var blocked=portal.Center(portal.BaffleY);
                Assert.That(map.SegmentClear(blocked-direction*6,blocked+direction*6),Is.False,"Depth baffle must affect routing");
            }
        }
        [Test]
        public void TraitsAndSpawnsAreStableAcrossTiersAndKeepIndividualClearance()
        {
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>("Assets/RVO/Demo/OceanReef/ReefNavigation.asset");
            profile.ValidateProfile();
            var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
            var settings = profile.Simulation;
            using var small = new AgentStorage(16);
            using var large = new AgentStorage(2048);
            var initializer = new VolumeScenarioInitializer(map);
            settings.AgentCount = 16; initializer.Initialize(settings, profile.Scenario, small.Initialization);
            settings.AgentCount = 2048; initializer.Initialize(settings, profile.Scenario, large.Initialization);
            float minRadius = float.MaxValue, maxRadius = 0, minSpeed = float.MaxValue, maxSpeed = 0;
            for (int i=0;i<2048;i++)
            {
                var p = large.Read.Parameters[i];
                minRadius = math.min(minRadius,p.Radius); maxRadius = math.max(maxRadius,p.Radius);
                minSpeed = math.min(minSpeed,p.MaxSpeed); maxSpeed = math.max(maxSpeed,p.MaxSpeed);
                Assert.That(p.Radius,Is.InRange(0.65f*0.7f,0.65f*1.3f));
                Assert.That(p.MaxSpeed,Is.InRange(3.75f,6.25f));
                Assert.That(map.PointClear(large.Read.Positions[i]),Is.True);
                Assert.That(map.PointClear(large.Read.Goals[i]),Is.True);
                for(int j=0;j<i;j++)
                {
                    float gap = p.Radius + large.Read.Parameters[j].Radius + 0.2999f;
                    Assert.That(math.distance(large.Read.Positions[i],large.Read.Positions[j]),Is.GreaterThanOrEqualTo(gap));
                    Assert.That(math.distance(large.Read.Goals[i],large.Read.Goals[j]),Is.GreaterThanOrEqualTo(gap));
                }
                if(i>=16) continue;
                Assert.That(large.Read.Positions[i],Is.EqualTo(small.Read.Positions[i]));
                Assert.That(large.Read.Goals[i],Is.EqualTo(small.Read.Goals[i]));
                Assert.That(p.Radius,Is.EqualTo(small.Read.Parameters[i].Radius));
                Assert.That(p.MaxSpeed,Is.EqualTo(small.Read.Parameters[i].MaxSpeed));
            }
            Assert.That(maxRadius-minRadius,Is.GreaterThan(0.37f));
            Assert.That(maxSpeed-minSpeed,Is.GreaterThan(2.4f));
            Assert.That(map.ClearanceRadius,Is.GreaterThanOrEqualTo(maxRadius+profile.Volume.SafetyMargin));
            var origins=new int[18]; var destinations=new int[18]; var directions=new int[6];
            for(int i=0;i<large.Read.Count;i++)
            {
                var start=large.Read.Positions[i]; var goal=large.Read.Goals[i];
                int Room(float3 p) => (p.y<0 ? 0 : 9)+(p.z<-16 ? 0 : p.z>16 ? 2 : 1)*3+(p.x<-16 ? 0 : p.x>16 ? 2 : 1);
                origins[Room(start)]++; destinations[Room(goal)]++;
                for(int axis=0;axis<3;axis++) if(math.abs(goal[axis]-start[axis])>20) directions[axis*2+(goal[axis]>start[axis] ? 1 : 0)]++;
            }
            foreach(int count in origins) Assert.That(count,Is.GreaterThan(100),"All 18 start rooms must be populated");
            foreach(int count in destinations) Assert.That(count,Is.GreaterThan(100),"All 18 destination rooms must be populated");
            foreach(int count in directions) Assert.That(count,Is.GreaterThan(300),"Traffic must exercise +/- X, Y and Z");

            // Probe real crossing routes, including those whose straight line hits the reef.
            Assert.That(map.ObstacleCount,Is.EqualTo(48));
            var search = new VolumePathfinder(profile.Volume.SearchCapacity);
            var points = new float3[profile.Volume.SearchCapacity+2];
            int detours = 0;
            for(int i=0;i<2048;i+=61)
            {
                var start = large.Read.Positions[i]; var goal = large.Read.Goals[i];
                if(!map.SegmentClear(start,goal)) detours++;
                search.Begin(map,start,goal,1.5f,true); search.Advance(1000000);
                Assert.That(search.Status,Is.EqualTo(VolumePathStatus.Ready),"Route "+i);
                int count = search.CopyPath(points);
                for(int j=1;j<count;j++) Assert.That(map.SegmentClear(points[j-1],points[j]),Is.True);
            }
            Assert.That(detours,Is.GreaterThan(10),"The added reef must exercise pathfinding.");
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void FreeNavigationUsesRandomizedSpeedAndRejectsStaleClearance(ExecutionBackend backend)
        {
            var settings = SimulationSettings.Default;
            settings.Dimension=SimulationDimension.Full3D; settings.Avoidance=AvoidanceAlgorithm.None;
            settings.Backend=backend; settings.AgentCount=64; settings.PreferredSideBias=0;
            var scenario = ScenarioSettings.Default;
            scenario.Kind=ScenarioKind.RandomCrowd; scenario.VolumeSizeVariation=0.3f; scenario.VolumeSpeedVariation=0.25f;
            var volume = VolumeSettings.Default; volume.Resolution=32;
            var map = VolumeBake.Bake(volume,scenario.VolumeClearanceRadius,Array.Empty<VolumeBox>());
            var stale = scenario; stale.VolumeSizeVariation=0;
            Assert.Throws<ArgumentException>(()=>Phase3ModuleFactory.Create(settings,stale,volume,map,out _,out _));
            var modules=Phase3ModuleFactory.Create(settings,scenario,volume,map,out _,out _);
            using var world = new SimulationWorld(settings,scenario,modules);
            world.Step();
            bool slower=false,faster=false;
            for(int i=0;i<settings.AgentCount;i++)
            {
                float speed=math.length(world.Snapshot.Velocities[i]);
                Assert.That(speed,Is.EqualTo(world.Snapshot.Parameters[i].MaxSpeed).Within(0.0001f));
                slower |= speed < scenario.MaxSpeed*0.9f; faster |= speed > scenario.MaxSpeed*1.1f;
            }
            Assert.That(slower && faster,Is.True);
        }

        [Test]
        public void AllReefLodsKeepStripesAndAnimatedBoundsAtEverySize()
        {
            for(int lod=0;lod<4;lod++)
            {
                var mesh=ReefFishMesh.Create(lod);
                try
                {
                    Assert.That(mesh.uv.Length,Is.EqualTo(mesh.vertexCount));
                    float maxMask=0;
                    foreach(var uv in mesh.uv) maxMask=Mathf.Max(maxMask,uv.x);
                    Assert.That(maxMask,Is.GreaterThan(0.9f));
                    foreach(var vertex in mesh.vertices)
                        for(int frame=0;frame<32;frame++)
                        {
                            var p=vertex+Vector3.right*(FishAnimationBaker.Wave(vertex.z,frame*Mathf.PI/16).x*0.24f);
                            Assert.That(p.magnitude,Is.LessThan(1.7f),"Must fit the pose culling sphere at any uniform scale.");
                        }
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }
    }
}
