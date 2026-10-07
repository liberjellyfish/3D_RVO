using System.Collections.Generic;
using UnityEngine;

namespace Rvo.Editor
{
    /// <summary>Author navigation volumes first; meshes are inscribed in these boxes.</summary>
    public static class ReefRouteLayout
    {
        public const float Size = 96;
        public struct Piece
        {
            public string Name;
            public Vector3 Center, Size;
            public int Seed;
            public bool Pale;
            public Piece(string name, Vector3 center, Vector3 size, int seed, bool pale = false)
            { Name=name; Center=center; Size=size; Seed=seed; Pale=pale; }
        }
        public struct Portal
        {
            public int Axis, Wall, Lane;
            public float Coordinate, Along, BaffleY;
            public Vector3 Center(float height) => Axis==0
                ? new Vector3(Coordinate,height,Along) : new Vector3(Along,height,Coordinate);
        }
        // Nine rooms per horizontal layer, 12 distinct horizontal connections; each
        // connection has upper/lower apertures. Room interiors connect the layers.
        public static Portal[] Portals()
        {
            var result=new List<Portal>();
            for(int axis=0;axis<2;axis++) for(int wall=0;wall<2;wall++) for(int lane=0;lane<3;lane++)
                result.Add(new Portal { Axis=axis,Wall=wall,Lane=lane,Coordinate=wall==0 ? -16 : 16,
                    Along=(lane-1)*30, BaffleY=((axis+wall+lane)%3-1)*14 });
            return result.ToArray();
        }
        public static Piece[] Pieces()
        {
            var result=new List<Piece> { new Piece("Network seabed",new Vector3(0,-44,0),new Vector3(140,20,140),3) };
            float[] low={-40,-25,5,35}, high={-35,-5,25,40};
            for(int axis=0;axis<2;axis++) for(int wall=0;wall<2;wall++) for(int segment=0;segment<4;segment++) for(int layer=0;layer<2;layer++)
            {
                float along=(low[segment]+high[segment])*0.5f, length=high[segment]-low[segment];
                float coordinate=wall==0 ? -16 : 16;
                float y=layer==0 ? -17 : 18, height=layer==0 ? 34 : 36;
                var center=axis==0 ? new Vector3(coordinate,y,along) : new Vector3(along,y,coordinate);
                var size=axis==0 ? new Vector3(4,height,length) : new Vector3(length,height,4);
                result.Add(new Piece($"Network wall {axis}-{wall}-{segment}-{layer}",center,size,
                    101+axis*101+wall*37+segment*7+layer,layer==1));
            }
            foreach(var portal in Portals())
                result.Add(new Piece($"Network baffle {portal.Axis}-{portal.Wall}-{portal.Lane}",portal.Center(portal.BaffleY),
                    portal.Axis==0 ? new Vector3(4,12,10) : new Vector3(10,12,4),
                    401+portal.Axis*43+portal.Wall*17+portal.Lane*3,true));
            result.Add(new Piece("Network central island",Vector3.zero,new Vector3(12,18,12),503));
            result.Add(new Piece("Network northwest island",new Vector3(-32,-4,32),new Vector3(12,20,10),509,true));
            result.Add(new Piece("Network southeast island",new Vector3(32,8,-32),new Vector3(12,16,14),521));
            return result.ToArray();
        }
    }
}
