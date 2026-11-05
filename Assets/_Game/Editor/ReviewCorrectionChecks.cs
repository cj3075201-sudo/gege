using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class ReviewCorrectionChecks
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
        public static void Run()
        {
            var failures=new List<string>();
            foreach(var item in new[]{("M2",(Action)Schedules),("M3",(Action)FoodGravity)})
            {
                if(!MeasureRunner.Arg("-reviewCases","M2,M3").Split(',').Contains(item.Item1))continue;
                try{item.Item2();Debug.Log("REVIEW_CHECK_PASS "+item.Item1);}
                catch(Exception error){string detail=item.Item1+": "+(error is TargetInvocationException&&error.InnerException!=null?error.InnerException:error);failures.Add(detail);Debug.LogError("REVIEW_CHECK_FAIL "+detail);}
            }
            DevTuningSession.EndSession();
            if(failures.Count>0)throw new InvalidOperationException(string.Join("\n",failures));
            Debug.Log("REVIEW_CHECK_ALL_PASS");
        }
        private static void Schedules()
        {
            var settings=ScriptableObject.CreateInstance<HazardSpawnSettings>();var host=new GameObject("ReviewF1Fixture");host.SetActive(false);
            try
            {
                var kinds=Enumerable.Range(0,15).Select(i=>new HazardSpawnSettings.Kind{type="probe"+i,role=HazardRole.Trigger,poolSize=13,mainPoolSize=10,alleyPoolSize=3}).ToArray();
                settings.Configure(kinds);
                var method=typeof(DevTuningSession).GetMethod("EnsureFiveDays",Any);Check(method!=null,"F1 requires five-day schedule migration");method.Invoke(null,new object[]{settings});
                var main=(int[])Get(kinds[0],"mainCountPerDay");var alley=(int[])Get(kinds[0],"alleyCountPerDay");
                Check(main.Length==5&&alley.Length==5,"independent five main/alley controls");
                Check(main.All(n=>n==10)&&alley.All(n=>n==3),"migration preserves whole-map split");
                var tool=host.AddComponent<DevTools>();Set(tool,"hazardSettings",settings);
                var target=(DevTarget)typeof(DevTools).GetMethod("HazardCounts",Any).Invoke(tool,null);
                foreach(int index in new[]{0,14})foreach(string field in new[]{"mainCountPerDay","alleyCountPerDay"})for(int day=0;day<5;day++)
                    Check(target.Values.Any(v=>v.PropertyPath==$"kinds.Array.data[{index}].{field}.Array.data[{day}]"),"missing F1 control for kind/day "+index+"/"+day);
                var value=target.Values.Single(v=>v.PropertyPath=="kinds.Array.data[0].mainCountPerDay.Array.data[4]");target.Set(value,17);
                Check(kinds[0].Count(5)==20&&kinds[0].Count(6)==20&&kinds[0].Count(99)==20,"day5 F1 count reaches gameplay and caps later days");
                Check(kinds[0].Count(4)==13,"fifth-day edit leaves earlier day alone");target.RevertAll();Check(kinds[0].Count(5)==13,"F1 revert updates actual counts");
                var planner=host.AddComponent<HazardPlanner>();var fields=DevValue.Discover(planner);
                foreach(string field in new[]{"activePerDay.Array.data[4]","routeShare","combosEnabled","showSelectedBeforePickup"})Check(fields.Any(v=>v.PropertyPath==field),"F1 missing rule "+field);
                Check((int)Get(planner,"activePerDay").GetType().GetProperty("Length").GetValue(Get(planner,"activePerDay"))==5,"ACT five days");
                Check(!(bool)Get(planner,"showSelectedBeforePickup"),"before-pickup display defaults off");
            }
            finally{DevTuningSession.EndSession();UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(settings);}
        }
        private static void FoodGravity()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            var session=UnityEngine.Object.FindFirstObjectByType<DeliverySession>();var motor=session.Player.GetComponent<RiderMotor>();
            var host=new GameObject("ReviewFoodFixture");
            try
            {
                var food=host.AddComponent<DroppedFood>();float up=22f,gravity=-Physics.gravity.y*motor.AccidentGravityScale;
                food.Launch(session,session.Player,Vector3.zero,Vector3.right*20,up,1.8f,null);
                Equal(2f*up/gravity,(float)Get(food,"flightSeconds"),"food uses accident gravity");
                Equal(up*up/(2f*gravity),(float)Get(food,"peak"),"food peak uses accident gravity");
                var planner=UnityEngine.Object.FindFirstObjectByType<HazardPlanner>();Equal(5f,(float)Get(planner,"foodExtraUp"),"HTML food extra up");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
        }
        private static object Get(object target,string name){var field=target.GetType().GetField(name,Any);Check(field!=null,"missing field "+name);return field.GetValue(target);}
        private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Any).SetValue(target,value);
        private static void Equal(float expected,float actual,string message)=>Check(Mathf.Abs(expected-actual)<.0001f,$"{message}: expected {expected}, actual {actual}");
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
