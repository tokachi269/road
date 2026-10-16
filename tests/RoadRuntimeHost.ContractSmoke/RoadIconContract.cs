using System;
using System.IO;
using System.Reflection;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class RoadIconContract
    {
        public static void Validate(Assembly assembly, string previewPath)
        {
            if (assembly.GetType("RoadRuntimeHost.Runtime.RoadIconRaster", false) != null)
                throw new InvalidOperationException("Runtime still owns icon drawing");
            Type assets = assembly.GetType("RoadRuntimeHost.Runtime.RoadIconAssets", true);
            MethodInfo read = assets.GetMethod("Read");
            int[][] cases = {new int[]{0,1},new int[]{0,2},new int[]{0,3},new int[]{0,4},
                new int[]{1,1},new int[]{1,2},new int[]{1,3},new int[]{1,4},new int[]{2,2},
                new int[]{2,3},new int[]{2,4},new int[]{3,3},new int[]{3,4},new int[]{4,4}};
            foreach (int[] pair in cases)
            {
                byte[] actual = (byte[])read.Invoke(null, new object[]{previewPath,pair[0],pair[1]});
                byte[] source = File.ReadAllBytes(Path.Combine(Path.Combine(previewPath,"icons"),pair[0]+"-"+pair[1]+".png"));
                if (actual.Length != source.Length) throw new InvalidOperationException("Runtime transformed the offline PNG");
                for(int i=0;i<source.Length;++i) if(actual[i]!=source[i])
                    throw new InvalidOperationException("Runtime transformed the offline PNG");
            }
            string[] states = (string[])assets.GetField("States").GetValue(null);
            if (string.Join(",",states) != ",Hovered,Pressed,Focused,Disabled")
                throw new InvalidOperationException("Native toolbar states are missing");
            ValidateMetadata(assembly, assets);
            string temp = Path.Combine(Path.GetTempPath(),"RoadIconContract."+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp,"icons"));
            try {
                // Missing assets fail; they are not regenerated inside the game.
                RequireFailure(read,temp,typeof(FileNotFoundException));
                string file = Path.Combine(Path.Combine(temp,"icons"),"1-1.png");
                byte[] valid = (byte[])read.Invoke(null,new object[]{previewPath,1,1});
                byte[] oversized = (byte[])valid.Clone(); oversized[16]=1;
                File.WriteAllBytes(file,oversized); RequireFailure(read,temp,typeof(InvalidDataException));
                File.WriteAllBytes(file,new byte[3]); RequireFailure(read,temp,typeof(InvalidDataException));
                byte[] invalid = (byte[])valid.Clone(); invalid[0]=0;
                File.WriteAllBytes(file,invalid); RequireFailure(read,temp,typeof(InvalidDataException));
                File.WriteAllBytes(file,new byte[1024*1024+1]); RequireFailure(read,temp,typeof(InvalidDataException));
            } finally { Directory.Delete(temp,true); }
            Console.WriteLine("ROAD_ICON_CONTRACT_OK topologies=14 offline_png_consumer states missing_invalid_rejected");
        }

        private static void RequireFailure(MethodInfo read,string root,Type expected)
        {
            try { read.Invoke(null,new object[]{root,1,1}); }
            catch(TargetInvocationException error) { if(expected.IsInstanceOfType(error.InnerException)) return; throw; }
            throw new InvalidOperationException("Invalid icon input was accepted");
        }

        private static void ValidateMetadata(Assembly assembly,Type assets)
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
            MethodInfo counts=assets.GetMethod("Counts");
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
            if((bool)counts.Invoke(null,new object[]{lanes,0,0}))
                throw new InvalidOperationException("Unknown traffic topology got a misleading car icon");
        }
    }
}
