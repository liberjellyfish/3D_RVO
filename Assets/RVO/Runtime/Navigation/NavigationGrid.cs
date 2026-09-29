using System;
using System.IO;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>不可变导航数据。构造器用于离线烘焙；运行时从二进制直接加载预计算结果。</summary>
    public sealed class NavigationGrid
    {
        public int Width { get; }
        public int Height { get; }
        public int Count => Width * Height;
        public float CellSize { get; }
        public float2 Min { get; }
        public float2 Max => Min + new float2(Width, Height) * CellSize;
        public float ClearanceRadius { get; }
        public int Version { get; }
        public int ObstacleCount { get; private set; }
        public int RectangleCount => (nodes.Length + 1) / 2;
        public int SpawnCellCount => spawnCells.Length;
        internal readonly bool[] Occupied;
        internal readonly float[] Clearances;
        internal readonly int[] Components;
        internal readonly byte[] Edges;
        private readonly ObstacleNode[] nodes;
        private readonly int[] spawnCells;
        private NavigationLandmarks landmarks;
        private int[] jumpTargets;
        // 不可变地图按需构建一次，所有 agent / 重置共享；不改变现有二进制格式。
        public NavigationLandmarks Landmarks => landmarks ?? (landmarks = new NavigationLandmarks(this));
        // 同一邻接掩码区间内的直线捷径；保留原始边，因此不依赖 JPS 的剪枝假设。
        internal int[] JumpTargets
        {
            get
            {
                if (jumpTargets != null) return jumpTargets;
                jumpTargets = new int[Count * 8];
                for (int edge = 0; edge < 8; edge++)
                {
                    int2 d = Direction(edge); int offset = d.y*Width+d.x;
                    for (int n = 0; n < Count; n++)
                    {
                        int cell = offset > 0 ? Count-1-n : n;
                        if ((Edges[cell] & (1 << edge)) == 0) { jumpTargets[cell*8+edge] = cell; continue; }
                        int next = cell+offset;
                        jumpTargets[cell*8+edge] = Edges[next] == Edges[cell] ? jumpTargets[next*8+edge] : next;
                    }
                }
                return jumpTargets;
            }
        }
        public long RuntimeIndexBytes => (landmarks?.PayloadBytes ?? 0) + (jumpTargets?.LongLength ?? 0) * sizeof(int);
        public int NodeCount => nodes.Length;
        public ObstacleNode Node(int index) => nodes[index];
        public int SpawnCell(int index) => spawnCells[index];
        public long PayloadBytes => Count * 10L + nodes.Length * 24L + spawnCells.Length * 4L;
        public static int2 Direction(int slot)
        {
            switch (slot) { case 0: return new int2(-1,-1); case 1: return new int2(0,-1); case 2: return new int2(1,-1);
                case 3: return new int2(-1,0); case 4: return new int2(1,0); case 5: return new int2(-1,1);
                case 6: return new int2(0,1); default: return new int2(1,1); }
        }
        public NavigationGrid(int width, int height, float cellSize, bool[] occupancy, float radius, int version)
        {
            ValidateDimensions(width, height, cellSize, radius);
            if (occupancy == null || occupancy.Length != width * height) throw new ArgumentException("Invalid occupancy.");
            Width = width; Height = height; CellSize = cellSize; ClearanceRadius = radius; Version = version;
            Min = -new float2(width, height) * (cellSize * 0.5f);
            Occupied = (bool[])occupancy.Clone(); Clearances = new float[Count]; Components = new int[Count]; Edges = new byte[Count];
            // 两遍 chessboard 距离变换：对轴对齐方格的方形膨胀给出精确净空，O(V)，替代 O(V²)。
            var distance = new int[Count];
            for (int i = 0; i < Count; i++) { distance[i] = Occupied[i] ? 0 : width + height; if (Occupied[i]) ObstacleCount++; }
            for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                if (x > 0) distance[i] = math.min(distance[i], distance[i - 1] + 1);
                if (z > 0) for (int dx = -1; dx <= 1; dx++) if (x + dx >= 0 && x + dx < width)
                    distance[i] = math.min(distance[i], distance[(z - 1) * width + x + dx] + 1);
            }
            for (int z = height - 1; z >= 0; z--) for (int x = width - 1; x >= 0; x--)
            {
                int i = z * width + x;
                if (x + 1 < width) distance[i] = math.min(distance[i], distance[i + 1] + 1);
                if (z + 1 < height) for (int dx = -1; dx <= 1; dx++) if (x + dx >= 0 && x + dx < width)
                    distance[i] = math.min(distance[i], distance[(z + 1) * width + x + dx] + 1);
            }
            for (int i = 0; i < Count; i++)
            {
                float2 p = Center(i);
                Clearances[i] = math.min(math.cmin(math.min(p - Min, Max - p)), math.max(0, distance[i] - 0.5f) * cellSize);
                Components[i] = -1;
            }
            // 预烘焙八邻接掩码。方格正交边由端点净空覆盖；斜边还要求两个正交侧格均可走。
            for (int i = 0; i < Count; i++) if (IsWalkable(i))
                for (int slot = 0; slot < 8; slot++)
                {
                    int2 d = Direction(slot); int x = i % width, z = i / width;
                    int next = Index(x + d.x, z + d.y);
                    if (!IsWalkable(next)) continue;
                    if (d.x != 0 && d.y != 0 && (!IsWalkable(Index(x + d.x, z)) || !IsWalkable(Index(x, z + d.y)))) continue;
                    Edges[i] |= (byte)(1 << slot);
                }
            int component = 0, largest = -1, largestCount = 0; var queue = new int[Count];
            for (int i = 0; i < Count; i++) if (IsWalkable(i) && Components[i] < 0)
            {
                int head = 0, tail = 0; queue[tail++] = i; Components[i] = component;
                while (head < tail)
                {
                    int current = queue[head++];
                    for (int slot = 0; slot < 8; slot++) if ((Edges[current] & (1 << slot)) != 0)
                    {
                        int2 d = Direction(slot); int next = current + d.y * width + d.x;
                        if (Components[next] >= 0) continue; Components[next] = component; queue[tail++] = next;
                    }
                }
                if (tail > largestCount) { largestCount = tail; largest = component; } component++;
            }
            spawnCells = new int[largestCount]; int cursor = 0;
            for (int i = 0; i < Count; i++) if (largest >= 0 && Components[i] == largest) spawnCells[cursor++] = i;
            nodes = ObstacleBvh.Build(width, height, cellSize, Min, Occupied);
        }

        internal NavigationGrid(BinaryReader reader)
        {
            Width = reader.ReadInt32(); Height = reader.ReadInt32(); CellSize = reader.ReadSingle();
            ClearanceRadius = reader.ReadSingle(); Version = reader.ReadInt32();
            ValidateDimensions(Width, Height, CellSize, ClearanceRadius);
            Min = -new float2(Width, Height) * (CellSize * 0.5f);
            Occupied = new bool[Count]; Clearances = new float[Count]; Components = new int[Count]; Edges = new byte[Count];
            for (int i = 0; i < Count; i++) { Occupied[i] = reader.ReadBoolean(); if (Occupied[i]) ObstacleCount++; }
            for (int i = 0; i < Count; i++) Clearances[i] = reader.ReadSingle();
            for (int i = 0; i < Count; i++) Components[i] = reader.ReadInt32();
            for (int i = 0; i < Count; i++) Edges[i] = reader.ReadByte();
            int spawnCount = reader.ReadInt32(); if (spawnCount < 0 || spawnCount > Count) throw new InvalidDataException("Invalid spawn cells.");
            spawnCells = new int[spawnCount];
            for (int i = 0; i < spawnCount; i++) { spawnCells[i] = reader.ReadInt32(); if (!IsWalkable(spawnCells[i])) throw new InvalidDataException("Invalid spawn index."); }
            int nodeCount = reader.ReadInt32(); if (nodeCount < 0 || nodeCount > Count * 2) throw new InvalidDataException("Invalid BVH.");
            nodes = new ObstacleNode[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                nodes[i] = new ObstacleNode { Min = new float2(reader.ReadSingle(), reader.ReadSingle()), Max = new float2(reader.ReadSingle(), reader.ReadSingle()),
                    Escape = reader.ReadInt32(), Leaf = reader.ReadInt32() };
                if (nodes[i].Escape <= i || nodes[i].Escape > nodeCount) throw new InvalidDataException("Invalid BVH escape index.");
            }
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(ClearanceRadius); writer.Write(Version);
            for (int i = 0; i < Count; i++) writer.Write(Occupied[i]);
            for (int i = 0; i < Count; i++) writer.Write(Clearances[i]);
            for (int i = 0; i < Count; i++) writer.Write(Components[i]);
            writer.Write(Edges); writer.Write(spawnCells.Length); foreach (int cell in spawnCells) writer.Write(cell);
            writer.Write(nodes.Length);
            foreach (var node in nodes) { writer.Write(node.Min.x); writer.Write(node.Min.y); writer.Write(node.Max.x); writer.Write(node.Max.y); writer.Write(node.Escape); writer.Write(node.Leaf); }
        }
        private static void ValidateDimensions(int w, int h, float cell, float radius)
        {
            if (w <= 0 || h <= 0 || w > 512 || h > 512 || !math.isfinite(cell) || cell <= 0 ||
                !math.isfinite(math.max(w,h) * cell) || !math.isfinite(radius) || radius < 0) throw new ArgumentException("Invalid grid dimensions.");
        }
        public int Index(int x, int z) => x < 0 || z < 0 || x >= Width || z >= Height ? -1 : z * Width + x;
        public int Cell(float2 p)
        {
            if (!math.all(math.isfinite(p))) return -1;
            int2 c = (int2)math.floor((p - Min) / CellSize); return Index(c.x, c.y);
        }
        public float2 Center(int index) => Min + new float2(index % Width + 0.5f, index / Width + 0.5f) * CellSize;
        public bool IsOccupied(int i) => Occupied[i];
        public bool IsWalkable(int i) => i >= 0 && i < Count && !Occupied[i] && Clearances[i] > ClearanceRadius + 1e-5f;
        public float Clearance(int i) => Clearances[i];
        public int Component(int i) => i < 0 || i >= Count ? -1 : Components[i];
        public byte EdgeMask(int cell) => Edges[cell];
        public void Bounds(int index, out float2 low, out float2 high)
        { low = Min + new float2(index % Width, index / Width) * CellSize; high = low + CellSize; }
        public bool CanStep(int from, int to)
        {
            if (from < 0 || from >= Count || to < 0 || to >= Count || from == to) return false;
            int dx = to % Width - from % Width, dz = to / Width - from / Width;
            if (math.abs(dx) > 1 || math.abs(dz) > 1) return false;
            int slot = (dz + 1) * 3 + dx + 1; if (slot > 4) slot--;
            return (Edges[from] & (1 << slot)) != 0;
        }
        public int Anchor(float2 point)
        {
            if (!SegmentClear(point, point, ClearanceRadius)) return -1;
            int cell = Cell(point);
            if (IsWalkable(cell) && SegmentClear(point, Center(cell), ClearanceRadius)) return cell;
            // 只修复局部偏移，避免每个重规划扫描 262144 格；远距离改目标应给出合法导航位置。
            int2 center = (int2)math.floor((point - Min) / CellSize); int best = -1; float distance = float.PositiveInfinity;
            int span = math.max(2, (int)math.ceil(ClearanceRadius / CellSize) + 1);
            for (int z = -span; z <= span; z++) for (int x = -span; x <= span; x++)
            {
                int i = Index(center.x + x, center.y + z); if (!IsWalkable(i)) continue;
                float d = math.distancesq(point, Center(i));
                if (d < distance && SegmentClear(point, Center(i), ClearanceRadius)) { distance = d; best = i; }
            }
            return best;
        }
        public bool SegmentClear(float2 a, float2 b, float radius)
        {
            if (!math.isfinite(radius) || radius < 0 || !math.all(math.isfinite(a)) || !math.all(math.isfinite(b)) ||
                math.any(a <= Min + radius) || math.any(a >= Max - radius) || math.any(b <= Min + radius) || math.any(b >= Max - radius)) return false;
            int index = 0;
            while (index < nodes.Length)
            {
                var node = nodes[index];
                if (!ObstacleBvh.SegmentBox(a, b, node.Min - radius, node.Max + radius, out _)) index = node.Escape;
                else if (node.Leaf != 0) return false;
                else index++;
            }
            return true;
        }
        // 只在物理合法、却落入导航安全余量的情况下回到局部可走格；不投影/传送位置。
        public bool TryRecoveryPoint(float2 point, float physicalRadius, out float2 target)
        {
            target = point;
            if (!SegmentClear(point,point,physicalRadius)) return false;
            int cell = Cell(point); if (cell < 0) return false;
            int span = math.max(2,(int)math.ceil(ClearanceRadius/CellSize)+2); float best = float.PositiveInfinity;
            for (int z = -span; z <= span; z++) for (int x = -span; x <= span; x++)
            {
                int candidate = Index(cell%Width+x,cell/Width+z); if (!IsWalkable(candidate)) continue;
                float2 p = Center(candidate); float distance = math.distancesq(point,p);
                if (distance < best && SegmentClear(point,p,physicalRadius)) { best = distance; target = p; }
            }
            return math.isfinite(best);
        }
        public static bool IntersectsBox(float2 a, float2 b, float2 low, float2 high) => ObstacleBvh.SegmentBox(a,b,low,high,out _);
    }
}
