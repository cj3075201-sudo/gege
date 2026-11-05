using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace DeliveryRider.Editor
{
    // GC.Alloc's sample value is duration; byte sizes are sample metadata[0], as documented by Unity.
    [InitializeOnLoad]
    public static class HtmlAllocationPlayChecks
    {
        private const string Pending="HtmlAllocation.Pending";
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static int tick,firstFrame,waited;
        private static DeliverySession session;
        static HtmlAllocationPlayChecks()=>EditorApplication.playModeStateChanged+=Mode;
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity",OpenSceneMode.Single);
            SessionState.SetBool(Pending,true);SessionState.SetInt(Pending+".exit",1);EditorApplication.EnterPlaymode();
        }
        private static void Mode(PlayModeStateChange state)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(state==PlayModeStateChange.EnteredPlayMode){tick=waited=0;EditorApplication.update+=Step;}
            if(state==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        private static void Step()
        {
            try
            {
                if(++tick==1)
                {
                    session=UnityEngine.Object.FindFirstObjectByType<DeliverySession>();var planner=UnityEngine.Object.FindFirstObjectByType<HazardPlanner>();
                    Property(session,"Progress",new RunProgress(4,100000));Set(session,"openZones",session.Economy.ZonesForDay(4));Set(planner,"day",4);Set(planner,"randomEachRun",false);Set(planner,"seed",17);
                    Call(session,"Notify");planner.Respawn();
                    return;
                }
                if(tick==3)
                {
                    SessionState.SetBool(Pending+".oldDriver",ProfilerDriver.enabled);SessionState.SetBool(Pending+".oldEnabled",Profiler.enabled);SessionState.SetBool(Pending+".oldEditor",ProfilerDriver.profileEditor);
                    ProfilerDriver.profileEditor=true;ProfilerDriver.enabled=true;Profiler.enabled=true;return;
                }
                if(tick==6)
                {
                    if(!session.OpenCall())throw new InvalidOperationException("call does not open");
                    firstFrame=Mathf.Max(0,ProfilerDriver.lastFrameIndex-1);
                    Profiler.BeginSample("Html.AcceptAllocation");
                    bool accepted;
                    try{accepted=session.TryAcceptOrder(session.Candidates[0]);}
                    finally{Profiler.EndSample();}
                    if(!accepted)throw new InvalidOperationException("call not accepted");
                    Time.timeScale=0f;return;
                }
                if(tick<=8)return;
                long acceptBytes=0,rollBytes=0;int acceptAllocs=0,rollAllocs=0,validFrames=0,maxSamples=0;bool found=false;
                for(int frame=firstFrame;frame<=ProfilerDriver.lastFrameIndex;frame++)
                {
                    using(var data=ProfilerDriver.GetRawFrameDataView(frame,0))
                    {
                        if(!data.valid)continue;validFrames++;maxSamples=Mathf.Max(maxSamples,data.sampleCount);
                        int gc=data.GetMarkerId("GC.Alloc");
                        for(int i=0;i<data.sampleCount;i++)
                        {
                            string name=data.GetSampleName(i);if(name!="Html.AcceptAllocation"&&name!="HazardPlanner.RollCall")continue;
                            long start=(long)data.GetSampleStartTimeNs(i),end=start+(long)data.GetSampleTimeNs(i);long bytes=0;int count=0;
                            for(int j=i+1;j<data.sampleCount;j++)
                            {
                                long time=(long)data.GetSampleStartTimeNs(j);if(time>=end)break;
                                if(time<start||data.GetSampleMarkerId(j)!=gc||data.GetSampleMetadataCount(j)==0)continue;
                                bytes+=data.GetSampleMetadataAsLong(j,0);count++;
                            }
                            if(name=="Html.AcceptAllocation"){acceptBytes+=bytes;acceptAllocs+=count;found=true;}else{rollBytes+=bytes;rollAllocs+=count;}
                        }
                    }
                }
                if(!found){if(++waited<30)return;throw new InvalidOperationException($"Profiler marker frame unavailable first={firstFrame} last={ProfilerDriver.lastFrameIndex} validFrames={validFrames} maxSamples={maxSamples} driver={ProfilerDriver.enabled} engine={Profiler.enabled} editor={ProfilerDriver.profileEditor}");}
                if(acceptBytes<=0||rollBytes<=0||acceptBytes<rollBytes)throw new InvalidOperationException($"GC metadata unavailable: accept={acceptBytes}, roll={rollBytes}");
                File.WriteAllText("Library/html_allocation.json",JsonConvert.SerializeObject(new{day=4,acceptBytes,acceptAllocs,rollBytes,rollAllocs,method="RawFrameDataView GC.Alloc metadata[0] on main thread in named acceptance/roll scopes"},Formatting.Indented));
                Debug.Log($"HTML_ALLOCATION_PASS acceptBytes={acceptBytes} samples={acceptAllocs} rollBytes={rollBytes} samples={rollAllocs}");Finish(null);
            }
            catch(Exception error){Finish(error);}
        }
        private static void Finish(Exception error)
        {
            EditorApplication.update-=Step;ProfilerDriver.enabled=SessionState.GetBool(Pending+".oldDriver",false);Profiler.enabled=SessionState.GetBool(Pending+".oldEnabled",false);ProfilerDriver.profileEditor=SessionState.GetBool(Pending+".oldEditor",false);Time.timeScale=1f;
            if(error!=null)Debug.LogError("HTML_ALLOCATION_FAIL "+error);SessionState.SetInt(Pending+".exit",error==null?0:1);EditorApplication.ExitPlaymode();
        }
        private static void Set(object owner,string field,object value)=>owner.GetType().GetField(field,Any).SetValue(owner,value);
        private static void Property(object owner,string name,object value)=>owner.GetType().GetProperty(name,Any).SetValue(owner,value);
        private static object Call(object owner,string name)=>owner.GetType().GetMethod(name,Any).Invoke(owner,null);
    }
}
