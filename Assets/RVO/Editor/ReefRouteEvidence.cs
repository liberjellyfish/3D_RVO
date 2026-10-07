using System.IO;
using System.Text;
using Unity.Mathematics;

namespace Rvo.Editor
{
    // Records actual swept crossings, not an inference from static connectivity or counts.
    internal sealed class ReefRouteEvidence
    {
        private readonly ReefRouteLayout.Portal[] portals=ReefRouteLayout.Portals();
        private readonly uint[] visited;
        private readonly int[] crossings=new int[24], unique=new int[24];
        private readonly StringBuilder spawns=new StringBuilder("agent,start_x,start_y,start_z,goal_x,goal_y,goal_z,direct_clear\n");
        private int exterior, overflight;
        public ReefRouteEvidence(in AgentReadView agents,NavigationVolume map)
        {
            visited=new uint[agents.Count];
            for(int i=0;i<agents.Count;i++)
            {
                var p=agents.Positions[i]; var g=agents.Goals[i];
                spawns.AppendLine(System.FormattableString.Invariant($"{i},{p.x},{p.y},{p.z},{g.x},{g.y},{g.z},{map.SegmentClear(p,g)}"));
            }
        }
        public void Observe(SimulationWorld world)
        {
            var before=world.DebugSnapshot.Inputs; var after=world.Snapshot;
            for(int i=0;i<after.Count;i++) for(int axis=0;axis<2;axis++) for(int wall=0;wall<2;wall++)
            {
                int dimension=axis==0 ? 0 : 2, along=axis==0 ? 2 : 0;
                float plane=wall==0 ? -16 : 16;
                float a=before.Positions[i][dimension],b=after.Positions[i][dimension];
                if((a<plane)==(b<plane) || math.abs(b-a)<1e-7f) continue;
                var p=math.lerp(before.Positions[i],after.Positions[i],(plane-a)/(b-a));
                if(math.abs(p[along])>40) { exterior++; continue; }
                if(p.y>36) { overflight++; continue; }
                for(int lane=0;lane<3;lane++)
                {
                    int portal=(axis*2+wall)*3+lane;
                    if(math.abs(p[along]-portals[portal].Along)>5) continue;
                    int index=portal*2+(p.y<portals[portal].BaffleY ? 0 : 1);
                    crossings[index]++;
                    uint bit=1u<<index;
                    if((visited[i]&bit)==0) { unique[index]++; visited[i]|=bit; }
                    break;
                }
            }
        }
        public void Write(string folder,int run)
        {
            var csv=new StringBuilder("axis,wall,lane,level,plane,along,crossings,unique_agents\n");
            for(int p=0;p<portals.Length;p++) for(int level=0;level<2;level++)
            {
                var portal=portals[p]; int index=p*2+level;
                csv.AppendLine($"{(portal.Axis==0 ? "X" : "Z")},{portal.Wall},{portal.Lane},{(level==0 ? "lower" : "upper")},{portal.Coordinate},{portal.Along},{crossings[index]},{unique[index]}");
            }
            File.WriteAllText($"{folder}/portals-{run}.csv",csv.ToString());
            File.WriteAllText($"{folder}/spawns-{run}.csv",spawns.ToString());
            File.WriteAllText($"{folder}/bypasses-{run}.txt",$"Exterior ring crossing events={exterior}\nOver-wall crossing events={overflight}\nCounts are events, not unique agents; portal CSV separates unique agents per aperture.\n");
        }
    }
}
