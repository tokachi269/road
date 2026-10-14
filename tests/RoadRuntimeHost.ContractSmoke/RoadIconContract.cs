using System;
using System.Reflection;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class RoadIconContract
    {
        public static void Validate(Assembly assembly)
        {
            Type raster = assembly.GetType("RoadRuntimeHost.Runtime.RoadIconRaster", true);
            MethodInfo render = raster.GetMethod("Render"), label = raster.GetMethod("Label"), compose = raster.GetMethod("Composite");
            int[][] cases = { new int[] {0,1}, new int[] {0,2}, new int[] {0,3}, new int[] {0,4},
                new int[] {1,1}, new int[] {2,2}, new int[] {3,3}, new int[] {4,4},
                new int[] {1,2}, new int[] {1,3}, new int[] {1,4}, new int[] {2,3}, new int[] {2,4}, new int[] {3,4} };
            string[] labels = { "→1","→2","→3","→4","1+1","2+2","3+3","4+4","1+2","1+3","1+4","2+3","2+4","3+4" };
            for (int c = 0; c < cases.Length; ++c)
            {
                int b = cases[c][0], f = cases[c][1];
                if ((string)label.Invoke(null, new object[] { b, f }) != labels[c]) throw new InvalidOperationException("Wrong base topology footer");
                byte[] image = (byte[])render.Invoke(null, new object[] { b, f, null });
                if (image.Length != 128*128*4 || image[3] != 0) throw new InvalidOperationException("Icon dimensions or outer transparency changed");
                // Count independent horizontal paint runs: arrows at their
                // stems are one white run per lane; center is one yellow run
                // only for opposing traffic, not a painted median variant.
                if (Runs(image, 48, true) != b + f || Runs(image, 80, false) != (b == 0 ? 0 : 1))
                    throw new InvalidOperationException("Icon lane/traffic-boundary paint matrix failed");
                int total = b + f, width = Math.Min(112,24+12*total), left=(128-width)/2;
                double pitch=(width-8.0)/total;
                for (int i=0; i<total; ++i)
                {
                    int x=(int)Math.Round(left+4+(i+0.5)*pitch);
                    bool down=b>0 && i<b;
                    int headY=down?62:35, oppositeY=down?35:62;
                    if (!White(image,x+3,headY) || White(image,x+3,oppositeY))
                        throw new InvalidOperationException("Icon arrow points against its lane group");
                }
                byte[] empty = new byte[image.Length];
                byte[] transparent = (byte[])render.Invoke(null, new object[] { b, f, empty });
                for (int i=0; i<image.Length; ++i) if (image[i] != transparent[i]) throw new InvalidOperationException("Transparent shared overlay changed the icon");
            }
            byte[] target={20,40,60,255,0,0,0,0,100,110,120,255};
            byte[] overlay={220,140,60,128,8,16,24,64,1,2,3,255};
            compose.Invoke(null,new object[]{target,overlay});
            byte[] expected={120,90,60,255,8,16,24,64,1,2,3,255};
            for(int i=0;i<expected.Length;++i) if(target[i]!=expected[i]) throw new InvalidOperationException("Straight-alpha overlay composition is wrong");
            string[] states=(string[])raster.GetField("States").GetValue(null);
            if(string.Join(",",states)!=",Hovered,Pressed,Focused,Disabled") throw new InvalidOperationException("Native toolbar sprite states are missing");
            ValidateMetadata(assembly, raster);
            ValidateOverlayHeader(raster);
            // PNGs and Unity textures have opposite vertical origins. A
            // labelled upper-left pixel must stay upper-left after conversion.
            byte[] rgba=new byte[128*128*4]; rgba[0]=17; rgba[1]=31; rgba[2]=63; rgba[3]=128;
            MethodInfo convert=assembly.GetType("RoadRuntimeHost.Runtime.RoadToolbarIcons",true)
                .GetMethod("ToUnity",BindingFlags.Static|BindingFlags.NonPublic);
            UnityEngine.Color32[] unity=(UnityEngine.Color32[])convert.Invoke(null,new object[]{rgba});
            UnityEngine.Color32 top=unity[127*128];
            if(top.r!=17 || top.g!=31 || top.b!=63 || top.a!=128 || unity[0].a!=0)
                throw new InvalidOperationException("Shared overlay was vertically flipped in the native atlas");
            Console.WriteLine("ROAD_ICON_CONTRACT_OK topologies=14 arrows_labels_alpha_states");
        }
        private static void ValidateMetadata(Assembly assembly,Type raster)
        {
            Type lane=assembly.GetType("RoadRuntimeHost.Runtime.LaneBundle",true);
            Array lanes=Array.CreateInstance(lane,5);
            for(int i=0;i<5;++i) {
                object value=Activator.CreateInstance(lane);
                lane.GetField("LaneType").SetValue(value,i==0 || i==4?"Pedestrian":"Vehicle");
                lane.GetField("VehicleType").SetValue(value,i==0 || i==4?"None":"Car");
                lane.GetField("Direction").SetValue(value,i==0 || i==4?"Both":i==1?"Backward":"Forward");
                lanes.SetValue(value,i);
            }
            MethodInfo counts=raster.GetMethod("Counts");
            object[] input={lanes,0,0};
            if(!(bool)counts.Invoke(null,input) || (int)input[1]!=1 || (int)input[2]!=2)
                throw new InvalidOperationException("Icon counted sidewalks as traffic lanes");
            lane.GetField("Position").SetValue(lanes.GetValue(1),11f);
            lane.GetField("Width").SetValue(lanes.GetValue(1),4f);
            lane.GetField("VerticalOffset").SetValue(lanes.GetValue(1),-0.3f);
            input=new object[]{lanes,0,0};
            if(!(bool)counts.Invoke(null,input) || (int)input[1]!=1 || (int)input[2]!=2)
                throw new InvalidOperationException("Placement details changed the base icon");
            lane.GetField("VehicleType").SetValue(lanes.GetValue(1),"Bicycle");
            if((bool)counts.Invoke(null,new object[]{lanes,0,0})) throw new InvalidOperationException("Unknown traffic topology got a misleading car icon");
        }
        private static void ValidateOverlayHeader(Type raster)
        {
            byte[] png=new byte[33]; byte[] start={137,80,78,71,13,10,26,10};
            Array.Copy(start,png,8); png[11]=13; png[12]=73; png[13]=72; png[14]=68; png[15]=82; png[19]=128; png[23]=128;
            MethodInfo check=raster.GetMethod("RequireOverlayPng");
            check.Invoke(null,new object[]{png});
            png[16]=1; bool rejected=false;
            try { check.Invoke(null,new object[]{png}); }
            catch(TargetInvocationException error) { if(!(error.InnerException is System.IO.InvalidDataException)) throw; rejected=true; }
            if(!rejected) throw new InvalidOperationException("Oversized compressed PNG reached the native decoder");
        }
        private static bool White(byte[] image,int x,int y) { int p=(y*128+x)*4; return image[p]>230 && image[p+1]>230 && image[p+2]>230; }
        private static int Runs(byte[] image,int y,bool white)
        {
            int count=0; bool prior=false;
            for(int x=0;x<128;++x) {
                int p=(y*128+x)*4;
                bool paint=white?White(image,x,y):image[p]>230 && image[p+1]>150 && image[p+1]<220 && image[p+2]<100;
                if(paint && !prior) ++count; prior=paint;
            }
            return count;
        }
    }
}
