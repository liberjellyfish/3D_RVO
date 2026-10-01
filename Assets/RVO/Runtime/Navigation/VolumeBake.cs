using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Unity.Mathematics;

namespace Rvo
{
    public static class VolumeBake
    {
        public const int FormatVersion = 2;
        private const int Magic = 0x33564F52;
        // Two full-height partitions with alternating high/low apertures: Y motion is unavoidable.
        public static VolumeBox[] DemoBoxes(in VolumeSettings settings)
        {
            float size = settings.Resolution * settings.CellSize, h = size / 2;
            var boxes = new List<VolumeBox>();
            for (int wall = 0; wall < 2; wall++)
            {
                float x = (wall == 0 ? -0.17f : 0.17f) * size;
                float bottom = (wall == 0 ? 0.15f : -0.38f) * size, top = bottom + size * 0.23f;
                float z = size * 0.18f, thick = math.max(settings.CellSize, size * 0.012f);
                boxes.Add(new VolumeBox(new float3(x, -h, -h), new float3(x + thick, bottom, h)));
                boxes.Add(new VolumeBox(new float3(x, top, -h), new float3(x + thick, h, h)));
                boxes.Add(new VolumeBox(new float3(x, bottom, -h), new float3(x + thick, top, -z)));
                boxes.Add(new VolumeBox(new float3(x, bottom, z), new float3(x + thick, top, h)));
            }
            return boxes.ToArray();
        }
        // Keep the small two-aperture fixture for algorithm tests; the interactive demo fills all chambers.
        public static VolumeBox[] DenseDemoBoxes(in VolumeSettings settings)
        {
            float size = settings.Resolution * settings.CellSize;
            var boxes = new List<VolumeBox>(DemoBoxes(settings));
            float[] columns = { -0.40f, -0.28f, -0.08f, 0.08f, 0.28f, 0.40f };
            for (int x = 0; x < columns.Length; x++) for (int y = 0; y < 4; y++) for (int z = 0; z < 4; z++)
            {
                float3 center = new float3(columns[x], -0.36f + y * 0.24f, -0.36f + z * 0.24f) * size;
                float3 half = new float3(0.022f, 0.027f + ((x + z) % 3) * 0.009f,
                    0.027f + ((x + y) % 3) * 0.009f) * size;
                boxes.Add(new VolumeBox(center - half, center + half));
            }
            return boxes.ToArray();
        }
        public static NavigationVolume Bake(in VolumeSettings settings, float radius, VolumeBox[] source)
        {
            settings.Validate(radius); int n = settings.Resolution, count = checked(n * n * n);
            float cell = settings.CellSize, r = radius + settings.SafetyMargin; float3 origin = new float3(-n * cell / 2);
            var boxes = new VolumeBox[source.Length];
            // Snap OUTWARD to voxel faces. In particular, thin source solids cannot disappear between centers.
            for (int i = 0; i < boxes.Length; i++)
            {
                if (!math.all(math.isfinite(source[i].Min)) || !math.all(math.isfinite(source[i].Max)) || math.any(source[i].Min >= source[i].Max))
                    throw new ArgumentException("Invalid source AABB.");
                boxes[i] = new VolumeBox(origin + math.floor((source[i].Min - origin) / cell) * cell,
                    origin + math.ceil((source[i].Max - origin) / cell) * cell);
            }
            var labels = new int[count]; var nodes = VolumeGeometry.Build(boxes);
            for (int z = 0; z < n; z++) for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float3 p = ((float3)new int3(x, y, z) + 0.5f) * cell;
                if (math.any(p <= r) || math.any(p >= n * cell - r)) labels[(z * n + y) * n + x] = -1;
            }
            foreach (var box in boxes)
            {
                int3 low = math.max(0, (int3)math.ceil((box.Min - r - origin) / cell - 0.5f));
                int3 high = math.min(n - 1, (int3)math.floor((box.Max + r - origin) / cell - 0.5f));
                for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++)
                    for (int x = low.x; x <= high.x; x++) labels[(z * n + y) * n + x] = -1;
            }
            var queue = new int[count]; int components = 0, free = 0;
            // With outward-snapped boxes at least one cell thick, free axial endpoints imply a clear axial edge.
            for (int root = 0; root < count; root++) if (labels[root] == 0)
            {
                int head = 0, tail = 1; queue[0] = root; labels[root] = ++components;
                while (head < tail)
                {
                    int at = queue[head++], x = at % n, y = at / n % n, z = at / (n * n);
                    if (x > 0) Enqueue(at - 1, components, labels, queue, ref tail);
                    if (x + 1 < n) Enqueue(at + 1, components, labels, queue, ref tail);
                    if (y > 0) Enqueue(at - n, components, labels, queue, ref tail);
                    if (y + 1 < n) Enqueue(at + n, components, labels, queue, ref tail);
                    if (z > 0) Enqueue(at - n * n, components, labels, queue, ref tail);
                    if (z + 1 < n) Enqueue(at + n * n, components, labels, queue, ref tail);
                }
                free += tail;
            }
            var map = new NavigationVolume(n, cell, r, origin, labels, boxes, nodes, 0, free);
            // Merge any components connected by a legal diagonal. Thus connectivity exactly matches all 26 edges.
            var parents = new int[components + 1]; for (int i = 0; i < parents.Length; i++) parents[i] = i;
            if (components > 1)
                for (int at = 0; at < count; at++) if (labels[at] > 0)
                    for (int z = 0; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                    {
                        if (z == 0 && (y < 0 || (y == 0 && x <= 0))) continue;
                        int3 c = map.Cell(at) + new int3(x, y, z); if (!map.Contains(c)) continue;
                        int to = map.Index(c); if (labels[to] <= 0) continue;
                        int a = Root(parents, labels[at]), b = Root(parents, labels[to]);
                        if (a != b && map.SegmentClear(map.Center(at), map.Center(to))) parents[b] = a;
                    }
            var sizes = new int[parents.Length]; int largest = 0;
            for (int i = 0; i < count; i++) if (labels[i] > 0)
            { labels[i] = Root(parents, labels[i]); sizes[labels[i]]++; if (sizes[labels[i]] > sizes[largest]) largest = labels[i]; }
            if (free == 0) throw new InvalidOperationException("Volume has no navigable cells.");
            var result = new NavigationVolume(n, cell, r, origin, labels, boxes, nodes, largest, free);
            if (n >= 64 && n % 8 == 0)
            {
                var coarseSettings = settings; coarseSettings.Resolution /= 8; coarseSettings.CellSize *= 8;
                // Conservative second level: larger outward-snapped AABBs, same physical radius and margin.
                // Its paths are valid in the fine map; failure says nothing about fine-map reachability.
                try { result.Coarse = Bake(coarseSettings, radius, boxes); }
                catch (InvalidOperationException) { /* No coarse free space: keep the complete fine graph. */ }
            }
            return result;
        }
        private static void Enqueue(int at, int component, int[] labels, int[] queue, ref int tail)
        { if (labels[at] == 0) { labels[at] = component; queue[tail++] = at; } }
        private static int Root(int[] parents, int i)
        { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        public static byte[] Encode(NavigationVolume map)
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(FormatVersion); w.Write(map.Resolution); w.Write(map.CellSize); w.Write(map.ClearanceRadius);
                    Write(w, map.Min); w.Write(map.LargestComponent); w.Write(map.FreeCells);
                    w.Write(map.Boxes.Length); foreach (var b in map.Boxes) { Write(w, b.Min); Write(w, b.Max); }
                    w.Write(map.Nodes.Length); foreach (var node in map.Nodes)
                    { Write(w, node.Min); Write(w, node.Max); w.Write(node.Escape); w.Write(node.Obstacle); }
                    // RLE retains every voxel/component; decoding performs no generation or connectivity work.
                    for (int i = 0; i < map.Count;)
                    { int end = i + 1; while (end < map.Count && map.labels[end] == map.labels[i]) end++; w.Write(end - i); w.Write(map.labels[i]); i = end; }
                    byte[] coarse = map.Coarse == null ? Array.Empty<byte>() : Encode(map.Coarse);
                    w.Write(coarse.Length); w.Write(coarse);
                }
                byte[] payload = stream.ToArray(); using (var sha = SHA256.Create())
                { byte[] digest = sha.ComputeHash(payload); stream.Write(digest, 0, digest.Length); }
                return stream.ToArray();
            }
        }
        public static NavigationVolume Decode(byte[] bytes, in VolumeSettings settings, float radius)
        {
            settings.Validate(radius);
            if (bytes == null || bytes.Length < 80) throw new InvalidDataException("Missing baked volume.");
            int length = bytes.Length - 32;
            using (var sha = SHA256.Create())
            { var hash = sha.ComputeHash(bytes, 0, length); for (int i = 0; i < 32; i++) if (hash[i] != bytes[length + i]) throw new InvalidDataException("Volume checksum mismatch."); }
            using (var stream = new MemoryStream(bytes, 0, length, false)) using (var r = new BinaryReader(stream))
            {
                if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion) throw new InvalidDataException("Rebake the volume format.");
                int n = r.ReadInt32(); float cell = r.ReadSingle(), clearance = r.ReadSingle();
                if (n != settings.Resolution || cell != settings.CellSize || math.abs(clearance - radius - settings.SafetyMargin) > 1e-6f)
                    throw new InvalidDataException("Volume resolution/radius/margin changed; rebake before running.");
                float3 origin = Read(r); int largest = r.ReadInt32(), free = r.ReadInt32();
                int boxCount = r.ReadInt32(); if (boxCount < 0 || boxCount > length / 24) throw new InvalidDataException("Invalid box count.");
                var boxes = new VolumeBox[boxCount]; for (int i = 0; i < boxes.Length; i++) boxes[i] = new VolumeBox(Read(r), Read(r));
                int nodeCount = r.ReadInt32(); if (nodeCount != math.max(0, boxCount * 2 - 1)) throw new InvalidDataException("Invalid BVH count.");
                var nodes = new VolumeBvhNode[nodeCount]; for (int i = 0; i < nodes.Length; i++)
                    nodes[i] = new VolumeBvhNode { Min = Read(r), Max = Read(r), Escape = r.ReadInt32(), Obstacle = r.ReadInt32() };
                var labels = new int[checked(n * n * n)];
                for (int i = 0; i < labels.Length;)
                {
                    int run = r.ReadInt32(), label = r.ReadInt32();
                    if (run < 1 || run > labels.Length - i || label == 0 || label < -1) throw new InvalidDataException("Invalid volume run.");
                    int end = i + run; for (; i < end; i++) labels[i] = label;
                }
                var result = new NavigationVolume(n, cell, clearance, origin, labels, boxes, nodes, largest, free);
                int coarseLength = r.ReadInt32();
                if (coarseLength < 0 || coarseLength > length - stream.Position) throw new InvalidDataException("Invalid coarse volume payload.");
                if (coarseLength > 0)
                {
                    var coarseSettings = settings; coarseSettings.Resolution /= 8; coarseSettings.CellSize *= 8;
                    result.Coarse = Decode(r.ReadBytes(coarseLength), coarseSettings, radius);
                }
                if (stream.Position != length) throw new InvalidDataException("Trailing volume payload.");
                return result;
            }
        }
        private static void Write(BinaryWriter w, float3 p) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
        private static float3 Read(BinaryReader r) => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
