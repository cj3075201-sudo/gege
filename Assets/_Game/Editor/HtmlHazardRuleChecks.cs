using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class HtmlHazardRuleChecks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static void Run()
        {
            try
            {
                var host = new GameObject("Inactive rules probe"); host.SetActive(false);
                var session = host.AddComponent<DeliverySession>();
                var planner = host.AddComponent<HazardPlanner>();
                typeof(HazardPlanner).GetField("session", Any).SetValue(planner, session);
                var slot = host.AddComponent<HazardSlot>();
                slot.Configure("falling", HazardRole.Trigger, 1, Array.Empty<int>());
                foreach (DeliveryStage stage in Enum.GetValues(typeof(DeliveryStage)))
                {
                    typeof(DeliverySession).GetProperty("Stage").SetValue(session, stage);
                    bool expected = stage == DeliveryStage.Carrying || stage == DeliveryStage.FoodLost;
                    bool actual = (bool)typeof(HazardPlanner).GetMethod("CanStrike", Any).Invoke(planner, new object[] { slot });
                    if (actual != expected) throw new InvalidOperationException($"{stage}: expected strike={expected}, got {actual}");
                    slot.Configure("manhole", HazardRole.Object, 1, Array.Empty<int>());
                    if (!(bool)typeof(HazardPlanner).GetMethod("CanStrike", Any).Invoke(planner, new object[] { slot })) throw new InvalidOperationException("Objects must remain armed");
                    slot.Configure("falling", HazardRole.Trigger, 1, Array.Empty<int>());
                }
                GateStates(session, planner);
                CallSelection(session, planner);
                VisibilityAndIgnite(session,planner);
                UnityEngine.Object.DestroyImmediate(host);
                Debug.Log("HtmlHazardRuleChecks PASS: delivery recovery gate and always armed objects");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogError("HtmlHazardRuleChecks FAIL: " + error); EditorApplication.Exit(1); }
        }
        private static void Set(object owner,string name,object value) => owner.GetType().GetField(name,Any).SetValue(owner,value);
        private static object Call(object owner,string name,params object[] args) => owner.GetType().GetMethod(name,Any).Invoke(owner,args);
        private static void GateStates(DeliverySession session,HazardPlanner planner)
        {
            var slots=(System.Collections.Generic.List<HazardSlot>)typeof(HazardPlanner).GetField("slots",Any).GetValue(planner);
            var host=new GameObject("Gate slots");host.SetActive(false);
            foreach(var type in new[]{"manhole","hydrant","falling","cat","dog","clone","duck","chicken","theft"})
            {
                var obj=new GameObject(type);obj.transform.SetParent(host.transform);var slot=obj.AddComponent<HazardSlot>();
                slot.Configure(type,type=="manhole"||type=="hydrant"?HazardRole.Object:HazardRole.Trigger,1,Array.Empty<int>());slots.Add(slot);
            }
            foreach(DeliveryStage stage in Enum.GetValues(typeof(DeliveryStage)))
            {
                typeof(DeliverySession).GetProperty("Stage").SetValue(session,stage);
                foreach(var slot in slots)
                {
                    bool actual=(bool)Call(planner,"CanStrike",slot), expected=slot.Role==HazardRole.Object||stage==DeliveryStage.Carrying||stage==DeliveryStage.FoodLost;
                    if(actual!=expected)throw new InvalidOperationException("Gate matrix "+slot.Type+" "+stage);
                }
            }
            slots.Clear();UnityEngine.Object.DestroyImmediate(host);
            Debug.Log("HtmlHazardRuleChecks PASS: 9 types x 5 stages; recovery, before pickup, after completion");
        }
        private static void CallSelection(DeliverySession session,HazardPlanner planner)
        {
            var slots=(System.Collections.Generic.List<HazardSlot>)typeof(HazardPlanner).GetField("slots",Any).GetValue(planner);
            var host=new GameObject("Call slots");host.SetActive(false);
            for(int n=0;n<16;n++)foreach(string type in new[]{"duck","chicken","muscleman","falling","cat","dog","tire","theft","clone"})
            {
                var obj=new GameObject(type);obj.transform.SetParent(host.transform);obj.transform.position=new Vector3(n*20f,0,0);
                var slot=obj.AddComponent<HazardSlot>();slot.Configure(type,HazardRole.Trigger,1,new[]{0});slots.Add(slot);
            }
            var offer=new DeliveryOrderOffer(1,host.transform,null,host.transform,"","","",0,0,10,10,"","C","C",0,0);
            var routes=ScriptableObject.CreateInstance<DeliveryRouteTable>();
            JsonUtility.FromJsonOverwrite("{\"shopNames\":[\"S\"],\"doorZones\":[\"C\"]}",routes);Set(session,"routeTable",routes);
            Set(planner,"rolledOrder",offer);Set(planner,"visibilityHoldRadius",0f);
            int duck=0;
            for(int day=1;day<=4;day++)for(int seed=0;seed<100;seed++)
            {
                foreach(var slot in slots)slot.Selected=false;
                Set(planner,"day",day);Set(planner,"callRandom",new System.Random(seed*1009+day));
                typeof(HazardPlanner).GetProperty("SelectedTriggers").SetValue(planner,0);typeof(HazardPlanner).GetProperty("NearSelectedTriggers").SetValue(planner,0);
                Call(planner,"RollCall");
                var selected=slots.FindAll(x=>x.Selected);int want=new[]{5,8,10,13}[day-1];
                if(selected.Count!=want)throw new InvalidOperationException($"ACT {day}/{seed} {selected.Count}/{want}");
                int birds=selected.FindAll(x=>x.Type=="duck").Count,steal=selected.FindAll(x=>x.Type=="theft"||x.Type=="clone").Count;
                if(birds>1||steal>1)throw new InvalidOperationException("Duck or stealing cap");duck+=birds;
            }
            if(duck<60||duck>140)throw new InvalidOperationException("25% duck distribution "+duck+"/400");
            slots.Clear();UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(routes);
            Debug.Log("HtmlHazardRuleChecks PASS: 400 seeded call rolls, exact ACT and exclusive duck/theft; duck="+duck+"/400");
        }
        private static void VisibilityAndIgnite(DeliverySession session,HazardPlanner planner)
        {
            var slots=(System.Collections.Generic.List<HazardSlot>)typeof(HazardPlanner).GetField("slots",Any).GetValue(planner);
            var host=new GameObject("Visibility and chain fixtures");host.SetActive(false);
            var riderObject=new GameObject("Rider position fixture");riderObject.transform.SetParent(host.transform);var rider=riderObject.AddComponent<RiderMotor>();Set(planner,"rider",rider);Set(planner,"visibilityHoldRadius",25f);
            var cameraHost=new GameObject("Probe visibility camera");var camera=cameraHost.AddComponent<Camera>();camera.transform.position=new Vector3(0f,2f,-5f);camera.transform.LookAt(new Vector3(0f,1f,10f));Set(planner,"visibilityCamera",camera);
            var node=new GameObject("Pending trigger");node.transform.SetParent(host.transform);node.transform.position=Vector3.forward*10f;
            var slot=node.AddComponent<HazardSlot>();slot.Configure("falling",HazardRole.Trigger,1,Array.Empty<int>());slot.Selected=true;slot.NeedsCallReset=true;slot.SetRuntimeActive(false);slots.Add(slot);
            typeof(DeliverySession).GetProperty("Stage").SetValue(session,DeliveryStage.Carrying);
            Call(planner,"ApplyPendingSlots");if(slot.RuntimeActive)throw new InvalidOperationException("visible nearby slot popped in");
            rider.transform.position=Vector3.back*30f;Call(planner,"ApplyPendingSlots");if(!slot.RuntimeActive||!slot.StrikeAllowed)throw new InvalidOperationException("deferred slot never activated");
            rider.transform.position=Vector3.zero;typeof(DeliverySession).GetProperty("Stage").SetValue(session,DeliveryStage.Complete);Call(planner,"ApplyPendingSlots");
            if(slot.RuntimeActive||slot.StrikeAllowed)throw new InvalidOperationException("completed call must hide its triggers and close the gate immediately");
            rider.transform.position=Vector3.back*30f;Call(planner,"ApplyPendingSlots");if(slot.RuntimeActive)throw new InvalidOperationException("deferred slot never hid");
            slots.Clear();UnityEngine.Object.DestroyImmediate(node);UnityEngine.Object.DestroyImmediate(cameraHost);
            HazardSlot Make(string type,HazardRole role,float distance)
            {
                var obj=new GameObject(type);obj.transform.SetParent(host.transform);obj.transform.position=Vector3.right*distance;var made=obj.AddComponent<HazardSlot>();made.Configure(type,role,1,Array.Empty<int>());slots.Add(made);return made;
            }
            var source=Make("manhole",HazardRole.Object,0f);var near=Make("falling",HazardRole.Trigger,12f);var far=Make("dog",HazardRole.Trigger,30f);
            Set(planner,"callRandom",new System.Random(1));Set(planner,"objectIgniteChance",1f);Set(planner,"igniteRadius",40f);
            typeof(DeliverySession).GetProperty("Stage").SetValue(session,DeliveryStage.Carrying);Call(planner,"Ignite",source);
            if(!near.Selected||far.Selected)throw new InvalidOperationException("chain did not pick the nearest eligible slot");
            near.Selected=false;typeof(DeliverySession).GetProperty("Stage").SetValue(session,DeliveryStage.Pickup);Call(planner,"Ignite",source);
            if(near.Selected||far.Selected)throw new InvalidOperationException("chain ignited before delivery");
            slots.Clear();UnityEngine.Object.DestroyImmediate(host);Set(planner,"rider",null);
            Debug.Log("HtmlHazardRuleChecks PASS: near-camera deferred visibility, immediate strike gate, nearest chain and before-pickup exclusion");
        }
    }
}
