using System.Collections.Generic;
using UnityEngine;

namespace Rvo.Rendering
{
    /// <summary>同一梭形鱼的四级网格；身体封闭，薄鳍复制反面，允许整体背面剔除。</summary>
    public static class ReefFishMesh
    {
        public static Mesh Create(int lod)
        {
            int[] rings = { 24, 16, 10, 6 }, sides = { 16, 12, 8, 6 };
            var v = new List<Vector3>(); var c = new List<Color>(); var t = new List<int>();
            int r = rings[lod], s = sides[lod];
            for (int j = 0; j < r; j++)
            {
                float u = j / (float)(r - 1), z = Mathf.Lerp(1, -0.92f, u);
                float width = 0.012f + 0.29f * Mathf.Pow(Mathf.Max(0, Mathf.Sin(u * Mathf.PI)), 0.85f) * (1 - u * 0.52f);
                for (int k = 0; k < s; k++)
                {
                    float a = k * 2 * Mathf.PI / s, y = Mathf.Sin(a);
                    v.Add(new Vector3(Mathf.Cos(a) * width, y * width * 1.55f, z));
                    Color color = Color.Lerp(new Color(0.62f, 0.68f, 0.59f), new Color(0.07f, 0.22f, 0.25f), Mathf.SmoothStep(0, 1, (y + 0.3f) / 1.3f));
                    c.Add(color);
                }
            }
            for (int j = 0; j < r - 1; j++) for (int k = 0; k < s; k++)
            {
                int a = j * s + k, b = j * s + (k + 1) % s;
                t.Add(a); t.Add(a + s); t.Add(b); t.Add(b); t.Add(a + s); t.Add(b + s);
            }
            for (int k = 1; k < s - 1; k++)
            {
                t.Add(0); t.Add(k); t.Add(k + 1);
                int e = (r - 1) * s; t.Add(e); t.Add(e + k + 1); t.Add(e + k);
            }
            var fin = new Color(0.28f, 0.38f, 0.27f, 0);
            Fin(v,c,t,new Vector3(0,0,-0.82f),new Vector3(0,0.51f,-1.3f),new Vector3(0,0.06f,-1.13f),fin);
            Fin(v,c,t,new Vector3(0,0,-0.82f),new Vector3(0,-0.06f,-1.13f),new Vector3(0,-0.51f,-1.3f),fin);
            Fin(v,c,t,new Vector3(0,0.30f,0.45f),new Vector3(0,0.63f,0.12f),new Vector3(0,0.20f,-0.65f),fin);
            Fin(v,c,t,new Vector3(0,-0.24f,-0.1f),new Vector3(0,-0.48f,-0.42f),new Vector3(0,-0.14f,-0.72f),fin);
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Fin(v,c,t,new Vector3(sign*0.23f,-0.05f,0.42f),new Vector3(sign*0.56f,-0.24f,-0.1f),new Vector3(sign*0.19f,-0.13f,-0.3f),fin);
                if (lod < 2) Eye(v,c,t,sign,lod == 0 ? 10 : 6);
            }
            var mesh = new Mesh { name = "Reef fusilier LOD" + lod };
            // UV0.x is a continuous flank stripe mask; fins/eyes remain unpainted.
            // Its hue is chosen per stable fish ID in the shader, with no per-fish materials.
            var markings = new List<Vector2>(v.Count);
            for (int j=0;j<v.Count;j++)
            {
                float side = Mathf.Sin((j % s) * 2 * Mathf.PI / s);
                markings.Add(new Vector2(j < r*s ? Mathf.Exp(-side*side*18) : 0, 0));
            }
            mesh.SetVertices(v); mesh.SetUVs(0,markings); mesh.SetColors(c); mesh.SetTriangles(t,0); mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 3.4f);
            return mesh;
        }

        private static void Fin(List<Vector3> v,List<Color> c,List<int> t,Vector3 a,Vector3 b,Vector3 d,Color color)
        {
            int start = v.Count;
            v.AddRange(new[] { a,b,d,a,d,b });
            for(int i=0;i<6;i++) { c.Add(color); t.Add(start+i); }
        }

        private static void Eye(List<Vector3> v,List<Color> c,List<int> t,int sign,int sides)
        {
            int start=v.Count;
            var center=new Vector3(sign*0.145f,0.09f,0.70f);
            v.Add(center+Vector3.right*sign*0.048f); c.Add(new Color(0.008f,0.015f,0.018f));
            for(int k=0;k<sides;k++)
            {
                float a=k*2*Mathf.PI/sides;
                v.Add(center+new Vector3(sign*0.022f,Mathf.Cos(a)*0.063f,Mathf.Sin(a)*0.063f));
                c.Add(new Color(0.50f,0.57f,0.40f));
            }
            for(int k=0;k<sides;k++)
            {
                t.Add(start); t.Add(start+1+(sign>0?k:(k+1)%sides)); t.Add(start+1+(sign>0?(k+1)%sides:k));
            }
        }
    }
}
