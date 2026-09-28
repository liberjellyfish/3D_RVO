using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace Rvo.Editor
{
    [CustomEditor(typeof(SimulationBootstrap))]
    public sealed class SimulationDebugEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var driver = (SimulationBootstrap)target;
            if (!Application.isPlaying) return;
            if (GUILayout.Button(driver.Paused ? "继续" : "暂停")) driver.SetPaused(!driver.Paused);
            if (GUILayout.Button("单步")) { driver.SetPaused(true); driver.StepOnce(); }
            if (GUILayout.Button("重置")) driver.ResetSimulation();
        }

        private void OnSceneGUI()
        {
            var driver = (SimulationBootstrap)target;
            var world = driver.World;
            if (world == null || world.State != WorldState.Ready) return;
            var agents = world.Snapshot;
            int index = Mathf.Clamp(driver.SelectedAgent, 0, agents.Count - 1);
            Vector3 position = agents.Positions[index];
            Handles.color = Color.white;
            Handles.DrawWireDisc(position, Vector3.up, agents.Parameters[index].Radius);
            Handles.DrawWireDisc((Vector3)agents.Goals[index], Vector3.up, 0.3f);
            Handles.DrawDottedLine(position, (Vector3)agents.Goals[index], 4f);
            if (driver.Navigation != null)
            {
                var path = driver.Navigation.Path(index);
                Handles.color = Color.green;
                Vector3 previous = position;
                if (path.Status == GridPathStatus.Ready)
                    for (int point = path.Cursor; point < path.Count; point++)
                    {
                        float2 waypoint = driver.Navigation.Waypoint(index, point);
                        Vector3 next = new Vector3(waypoint.x, position.y, waypoint.y);
                        Handles.DrawLine(previous, next); previous = next;
                    }
                Arrow(position, (Vector3)agents.Velocities[index], Color.yellow);
                Handles.Label(position + Vector3.forward,
                    $"Agent {agents.Ids[index]} | {path.Status} | map {path.MapVersion}\nRequest {path.RequestId} | safety scale {driver.NavigationSolver.LastSafetyScale:F3}");
                // 组合求解器使用独立约束 scratch，不能读取 Phase 1 的 N*K 调试视图。
                return;
            }
            if (world.Tick == 0) return;
            var step = world.DebugSnapshot;
            int start = index * step.Neighbors.MaxNeighbors;
            Handles.color = new Color(0.4f, 0.5f, 0.7f);
            for (int slot = 0; slot < step.Neighbors.Counts[index]; slot++)
                Handles.DrawLine((Vector3)step.Inputs.Positions[index],
                    (Vector3)step.Inputs.Positions[step.Neighbors.Indices[start + slot]]);

            const float scale = 2f;
            Vector3 origin = position + new Vector3(0, 0.05f, 7);
            float maxSpeed = agents.Parameters[index].MaxSpeed;
            Handles.color = Color.white;
            Handles.DrawWireDisc(origin, Vector3.up, maxSpeed * scale);
            // 按真实有限时域 VO 判定绘制速度空间，而不是误画成无限锥。
            const int grid = 16;
            for (int x = -grid; x <= grid; x++)
                for (int z = -grid; z <= grid; z++)
                {
                    float2 v = new float2(x, z) * (maxSpeed / grid);
                    if (math.lengthsq(v) > maxSpeed * maxSpeed) continue;
                    bool forbidden = false;
                    for (int slot = 0; slot < step.Neighbors.Counts[index]; slot++)
                    {
                        int other = step.Neighbors.Indices[start + slot];
                        float2 p = step.Inputs.Positions[other].xz - step.Inputs.Positions[index].xz;
                        float radius = agents.Parameters[index].Radius + agents.Parameters[other].Radius + world.Settings.Vo.SafetyMargin;
                        bool blocked;
                        if (world.Settings.Avoidance == AvoidanceAlgorithm.ORCA)
                        {
                            var plane = step.OrcaConstraints[start + slot];
                            blocked = math.dot(plane.Normal, v) < plane.Offset;
                        }
                        else blocked = world.Settings.Avoidance != AvoidanceAlgorithm.None && VelocityObstacle2D.Contains(p,
                            ReciprocalVelocityObstacle2D.Relative(v, step.Inputs.Velocities[index].xz,
                                step.Inputs.Velocities[other].xz, world.Settings.Avoidance == AvoidanceAlgorithm.RVO), radius, world.Settings.TimeHorizon);
                        if (blocked)
                        { forbidden = true; break; }
                    }
                    if (!forbidden) continue;
                    Handles.color = new Color(1, 0.2f, 0.2f, 0.6f);
                    Handles.DrawSolidDisc(origin + new Vector3(v.x, 0, v.y) * scale, Vector3.up, 0.035f);
                }
            if (world.Settings.Avoidance == AvoidanceAlgorithm.ORCA)
                for (int slot = 0; slot < step.Neighbors.Counts[index]; slot++)
                {
                    int other = step.Neighbors.Indices[start + slot];
                    var plane = step.OrcaConstraints[start + slot];
                    float2 center = plane.Normal * plane.Offset;
                    Vector3 point = origin + new Vector3(center.x, 0, center.y) * scale;
                    Vector3 tangent = new Vector3(-plane.Normal.y, 0, plane.Normal.x) * maxSpeed * scale;
                    Handles.color = Color.magenta;
                    Handles.DrawLine(point - tangent, point + tangent);
                    Arrow(point, new Vector3(plane.Normal.x, 0, plane.Normal.y) * 0.4f, Color.magenta);
                }
            Arrow(origin, (Vector3)step.Inputs.Velocities[index] * scale, Color.cyan);
            Arrow(origin, (Vector3)step.Preferred[index] * scale, Color.green);
            Arrow(origin, (Vector3)agents.Velocities[index] * scale, Color.yellow);
            Handles.color = Color.white;
            Handles.Label(origin + Vector3.forward * (maxSpeed * scale + 0.5f),
                $"Agent {agents.Ids[index]} | {step.Status[index]}\n红: 当前算法禁区  紫: ORCA 可行侧  青: 当前  绿: 期望  黄: 选定\n以 Status 区分成功、回退与不可行");
        }

        private static void Arrow(Vector3 origin, Vector3 velocity, Color color)
        {
            Handles.color = color;
            Handles.DrawAAPolyLine(3, origin, origin + velocity);
            if (velocity.sqrMagnitude > 0.0001f)
                Handles.ConeHandleCap(0, origin + velocity, Quaternion.LookRotation(velocity), 0.18f, EventType.Repaint);
        }
    }
}
