using System;using System.Linq;using System.IO;using UnityEngine;using UnityEngine.Rendering;using UnityEngine.Rendering.Universal;using UnityEngine.SceneManagement;using UnityEditor;using UnityEditor.SceneManagement;using DeliveryRider;using DeliveryRider.Editor;using Newtonsoft.Json;
public static class MyeongjiLightingSetup {
 const string Profile="Assets/_Game/Settings/MyeongjiDaySkySettings.asset";
 static Color C(float r,float g,float b)=>new Color(r,g,b,1);
 static Gradient Gradient(params (float time,Color color)[] keys){var g=new Gradient();g.SetKeys(keys.Select(k=>new GradientColorKey(k.color,k.time)).ToArray(),new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(1,1)});return g;}
 public static string Apply(){
 var scene=SceneManager.GetActiveScene();if(scene.path!="Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity"||EditorApplication.isPlaying||scene.isDirty)throw new Exception("Saved Myeongji edit scene required");
 if(AssetDatabase.LoadAssetAtPath<DaySkySettings>(Profile)==null&&!AssetDatabase.CopyAsset("Assets/_Game/Settings/DaySkySettings.asset",Profile))throw new Exception("Profile copy failed");
 var settings=AssetDatabase.LoadAssetAtPath<DaySkySettings>(Profile);var data=new SerializedObject(settings);
 void F(string n,float v)=>data.FindProperty(n).floatValue=v;void Color(string n,UnityEngine.Color c)=>data.FindProperty(n).colorValue=c;void G(string n,Gradient g)=>data.FindProperty(n).gradientValue=g;
 F("startElevation",65);F("endElevation",0);F("startAzimuth",105);F("endAzimuth",265);F("minimumLightElevation",12);
 data.FindProperty("height").animationCurveValue=new AnimationCurve(new Keyframe(0,.55f,0,1.4f),new Keyframe(.375f,1,0,0),new Keyframe(.75f,.44f,-1.8f,-1.8f),new Keyframe(1,0,-1.4f,-1.4f));
 G("sunColor",Gradient((0,C(1,.9f,.75f)),(.375f,C(1,.97f,.9f)),(.7f,C(1,.9f,.72f)),(.85f,C(1,.73f,.46f)),(1,C(1,.52f,.30f))));
 data.FindProperty("sunIntensity").animationCurveValue=new AnimationCurve(new Keyframe(0,1.25f),new Keyframe(.375f,1.6f),new Keyframe(.7f,1.4f),new Keyframe(.85f,1.12f),new Keyframe(1,.78f));
 G("ambientColor",Gradient((0,C(.49f,.54f,.63f)),(.4f,C(.52f,.59f,.69f)),(.75f,C(.5f,.52f,.61f)),(1,C(.48f,.43f,.52f))));
 G("zenithColor",Gradient((0,C(.43f,.65f,.86f)),(.4f,C(.32f,.58f,.84f)),(.75f,C(.45f,.54f,.76f)),(1,C(.35f,.29f,.51f))));
 G("fogColor",Gradient((0,C(.80f,.86f,.86f)),(.4f,C(.74f,.84f,.9f)),(.75f,C(.94f,.78f,.62f)),(1,C(.96f,.55f,.36f))));
 F("cloudShadowStrength",.22f);F("cloudCoverage",.35f);F("duskLightIntensity",.32f);F("duskShadowStrength",.3f);Color("duskAmbient",C(.39f,.42f,.54f));
 data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(settings);AssetDatabase.SaveAssetIfDirty(settings);
 DaySkyInstaller.Apply();
 var sky=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DaySky>(true)).Single();var sun=sky.Sun;if(sun==null)throw new Exception("Missing sun reference after installer");sun.enabled=true;sun.lightmapBakeType=LightmapBakeType.Realtime;sun.shadows=LightShadows.Soft;sun.shadowStrength=.9f;sun.shadowBias=.04f;sun.shadowNormalBias=.22f;sun.useColorTemperature=false;
 var rp=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;if(rp!=null){rp.shadowDistance=100;rp.mainLightShadowmapResolution=4096;rp.shadowCascadeCount=4;EditorUtility.SetDirty(rp);}
 RenderSettings.ambientMode=AmbientMode.Flat;
 SetOpening(sky,settings);sun.shadowStrength=.9f;
 var map=scene.GetRootGameObjects().Single(g=>g.name=="MyeongjiMap");int receives=0;foreach(var r in map.GetComponentsInChildren<Renderer>(true)){if(!r.receiveShadows){Undo.RecordObject(r,"Receive daylight shadows");r.receiveShadows=true;PrefabUtility.RecordPrefabInstancePropertyModifications(r);receives++;}}
 EditorUtility.SetDirty(sun);EditorSceneManager.MarkSceneDirty(scene);AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(scene);
 return "Morning/noon/evening profile installed; same session as HUD, sun/cloud/developer references restored; realtime soft shadows 100m/4096/4 cascades; receivers changed "+receives;
 }
 static void SetOpening(DaySky sky,DaySkySettings settings){
 sky.Apply(0,0);var mat=RenderSettings.skybox;float elevation=settings.Elevation(0)*Mathf.Deg2Rad,azimuth=settings.Azimuth(0)*Mathf.Deg2Rad;var dir=new Vector3(Mathf.Sin(azimuth)*Mathf.Cos(elevation),Mathf.Sin(elevation),Mathf.Cos(azimuth)*Mathf.Cos(elevation));
 mat.SetColor("_ZenithColor",settings.ZenithColor(0));mat.SetColor("_HorizonColor",settings.FogColor(0));mat.SetColor("_SunColor",settings.SunColor(0));mat.SetVector("_SunDirection",dir);mat.SetFloat("_Dusk",0);mat.SetFloat("_SunSize",settings.SunSize);mat.SetFloat("_CloudCoverage",settings.CloudCoverage);mat.SetFloat("_CloudScale",settings.CloudScale);mat.SetColor("_CloudLit",UnityEngine.Color.Lerp(UnityEngine.Color.white,settings.SunColor(0),.6f));mat.SetColor("_CloudShade",settings.ZenithColor(0)*.8f);EditorUtility.SetDirty(mat);
 }
 public static string Review(){var scene=SceneManager.GetActiveScene();var sky=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DaySky>(true)).Single();var data=new SerializedObject(sky);var session=data.FindProperty("session").objectReferenceValue;var hud=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DeliveryDayHud>(true)).Single();bool same=session!=null&&session==new SerializedObject(hud).FindProperty("session").objectReferenceValue;if(!same||sky.Sun==null||!sky.enabled)throw new Exception("Clock/light not connected");var s=sky.Settings;var samples=new[]{0f,.375f,.75f,1f}.Select(t=>new{day=t,elevation=s.Elevation(t),azimuth=s.Azimuth(t),intensity=s.SunIntensity(t),sun=s.SunColor(t).ToString(),ambient=s.AmbientColor(t).ToString()});var mats=scene.GetRootGameObjects().Single(g=>g.name=="MyeongjiMap").GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m!=null&&m.HasProperty("_ReceiveShadows")&&(m.GetFloat("_ReceiveShadows")<.5f||m.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"))).Select(m=>m.name).Distinct().ToArray();var result=new{scene=scene.path,sameClockAsHud=same,profile=AssetDatabase.GetAssetPath(s),samples,shadowDisabledMaterials=mats,playTest=false};var json=JsonConvert.SerializeObject(result,Formatting.Indented);File.WriteAllText("Docs/MapLayout/Myeongji/lighting_review.json",json);return json;}
}
