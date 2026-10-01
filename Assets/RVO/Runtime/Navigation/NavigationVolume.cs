using System;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Immutable baked voxel graph. One int per voxel: -1 blocked, positive connected component.</summary>
    public sealed class NavigationVolume
    {
        public int Resolution { get; }
        public int Count => labels.Length;
        public int Version => VolumeBake.FormatVersion;
        public float CellSize { get; }
        public float ClearanceRadius { get; }
        public float3 Min { get; }
        public float3 Max => Min + Resolution * CellSize;
        public int LargestComponent { get; }
        public int FreeCells { get; }
        public NavigationVolume Coarse { get; internal set; }
        public long StorageBytes => labels.LongLength * 4 + Boxes.Length * 24L + Nodes.Length * 32L + (Coarse?.StorageBytes ?? 0);
        internal readonly int[] labels;
        internal readonly VolumeBox[] Boxes;
        internal readonly VolumeBvhNode[] Nodes;
        public int ObstacleCount => Boxes.Length;
        public VolumeBox Obstacle(int i) => Boxes[i];
        internal NavigationVolume(int resolution, float cellSize, float clearance, float3 origin,
            int[] components, VolumeBox[] boxes, VolumeBvhNode[] nodes, int largest, int free)
        {
            Resolution = resolution; CellSize = cellSize; ClearanceRadius = clearance; Min = origin;
            labels = components; Boxes = boxes; Nodes = nodes; LargestComponent = largest; FreeCells = free;
        }
        public int Index(int3 cell) => (cell.z * Resolution + cell.y) * Resolution + cell.x;
        public int3 Cell(int index) => new int3(index % Resolution, index / Resolution % Resolution, index / (Resolution * Resolution));
        public float3 Center(int index) => Min + ((float3)Cell(index) + 0.5f) * CellSize;
        public bool Contains(int3 c) => math.all(c >= 0) && math.all(c < Resolution);
        public int Component(int index) => labels[index];
        public int Anchor(float3 point, NavigationVolume endpointMap = null)
        {
            if (!math.all(math.isfinite(point))) return -1;
            int3 cell = (int3)math.floor((point - Min) / CellSize); int best = -1; float distance = float.PositiveInfinity;
            for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                int3 c = cell + new int3(x, y, z); if (!Contains(c)) continue;
                int index = Index(c); if (labels[index] <= 0) continue;
                float d = math.distancesq(point, Center(index));
                // Fine-valid endpoints may lie inside an outward-expanded coarse obstacle.
                // Only the connector uses fine geometry; graph edges still use the coarse solids.
                if (d < distance && (endpointMap ?? this).SegmentClear(point, Center(index))) { distance = d; best = index; }
            }
            return best;
        }
        public bool Edge(int from, int3 direction, out int to)
        {
            int3 c = Cell(from) + direction; to = Contains(c) ? Index(c) : -1;
            return to >= 0 && labels[to] > 0 && SegmentClear(Center(from), Center(to));
        }
        public bool PointClear(float3 p) => SegmentClear(p, p);
        public bool SegmentClear(float3 a, float3 b)
        {
            float r = ClearanceRadius;
            if (math.any(a <= Min + r) || math.any(a >= Max - r) || math.any(b <= Min + r) || math.any(b >= Max - r)) return false;
            for (int i = 0; i < Nodes.Length;)
            {
                var n = Nodes[i];
                if (!VolumeGeometry.SegmentBox(a, b, n.Min - r, n.Max + r, out _)) { i = n.Escape; continue; }
                if (n.Obstacle >= 0) return false; i++;
            }
            return true;
        }
        // Conservative L-infinity distance to source solids/boundary, NOT voxel-center distance.
        public float Clearance(float3 p)
        {
            float result = math.cmin(math.min(p - Min, Max - p));
            for (int i = 0; i < Boxes.Length; i++)
                result = math.min(result, math.cmax(math.max(math.max(Boxes[i].Min - p, p - Boxes[i].Max), 0)));
            return result;
        }
    }
}
