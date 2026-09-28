using System;
using System.IO;
using System.Security.Cryptography;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>显式离线入口。仿真模块只接收烘焙结果，不调用生成或建图。</summary>
    public static class NavigationBake
    {
        public const int FormatVersion = 2;
        private const int Magic = 0x324F5652;
        public static NavigationGrid Generate(in NavigationSettings settings, float agentRadius)
        {
            settings.Validate(agentRadius); var random = new Unity.Mathematics.Random(settings.Seed);
            var occupied = new bool[settings.Width * settings.Height];
            for (int i = 0; i < settings.ObstacleCount; i++)
            {
                int w = random.NextInt(settings.ObstacleMinSize, settings.ObstacleMaxSize + 1);
                int h = random.NextInt(settings.ObstacleMinSize, settings.ObstacleMaxSize + 1);
                int x = random.NextInt(settings.Width - w + 1), z = random.NextInt(settings.Height - h + 1);
                for (int dz = 0; dz < h; dz++) for (int dx = 0; dx < w; dx++) occupied[(z + dz) * settings.Width + x + dx] = true;
            }
            var map = new NavigationGrid(settings.Width, settings.Height, settings.CellSize, occupied,
                agentRadius + settings.SafetyMargin, 1);
            if (map.SpawnCellCount == 0) throw new InvalidOperationException("地图无可导航空间，请减少障碍或半径后重新烘焙。");
            return map;
        }
        public static byte[] Encode(NavigationGrid map, uint signature)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { writer.Write(Magic); writer.Write(FormatVersion); writer.Write(signature); map.Write(writer); }
                byte[] payload = stream.ToArray();
                using (var sha = SHA256.Create())
                { byte[] hash = sha.ComputeHash(payload); stream.Write(hash, 0, hash.Length); }
                return stream.ToArray();
            }
        }
        public static NavigationGrid Decode(byte[] bytes, uint expectedSignature)
        {
            if (bytes == null || bytes.Length < 64) throw new InvalidDataException("烘焙数据缺失或截断。");
            int length = bytes.Length - 32;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes, 0, length);
                for (int i = 0; i < 32; i++) if (hash[i] != bytes[length + i]) throw new InvalidDataException("烘焙数据校验失败，请重新烘焙。");
            }
            using (var stream = new MemoryStream(bytes, 0, length, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion) throw new InvalidDataException("烘焙格式已过期，请重新烘焙。");
                if (reader.ReadUInt32() != expectedSignature) throw new InvalidDataException("地图配置/半径已变化，请在运行前重新烘焙。");
                var map = new NavigationGrid(reader);
                if (stream.Position != length) throw new InvalidDataException("烘焙数据长度不匹配。");
                return map;
            }
        }
    }
}
