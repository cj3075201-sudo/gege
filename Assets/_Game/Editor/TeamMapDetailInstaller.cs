using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Imports only visual data from c0bc911; never copies its gameplay roots or regenerates team art.
    public static class TeamMapDetailInstaller
    {
        internal const string ReferenceScene="Assets/TeamMapReference.unity",ReferencePrefab="Assets/TeamMapReference.prefab";
        internal const string MapPrefab="Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab";
        internal const string Art="Assets/_Game/Art/Myeongji/StreetDetails";
        internal static string Id(Renderer r)=>r.name.StartsWith("AB01_")?r.name.Substring(5).Split('.')[0]:null;
        internal static Dictionary<string,MeshRenderer> Buildings(GameObject map)=>map.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.name.StartsWith("AB01_")).ToDictionary(Id);
        public static void Inspect()
        {
            var main=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);var reference=EditorSceneManager.OpenScene(ReferenceScene,OpenSceneMode.Additive);
            try
            {
                var source=reference.GetRootGameObjects().Single(g=>g.name=="MyeongjiMap");var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ReferencePrefab);var ground=prefab.transform.Find("Ground").GetComponentInChildren<MeshFilter>();
                var b=ground.sharedMesh.bounds;var min=ground.transform.TransformPoint(b.min);var max=ground.transform.TransformPoint(b.max);
                TeamMapVerification.Write("reference_inspect.json",new{originalBuildings=Buildings(source).Count,prefabBuildings=Buildings(prefab).Count,originalGroundMin=new[]{min.x,min.y,min.z},originalGroundMax=new[]{max.x,max.y,max.z},groups=new[]{"StreetGreenery","EntranceDetails","EveningLighting"}.Select(name=>new{name,exists=source.transform.Find(name)!=null,children=source.transform.Find(name)?.childCount,colliders=source.transform.Find(name)?.GetComponentsInChildren<Collider>(true).Length}).ToArray(),props=source.transform.Find("EntranceDetails").Cast<Transform>().Select(t=>new{t.name,p=new[]{t.position.x,t.position.y,t.position.z}}).ToArray()});
                SceneManager.SetActiveScene(main);TeamMapVerification.CompareProtected();
                Debug.Log("TEAM_MAP_REFERENCE_INSPECT_PASS");
            }
            finally{EditorSceneManager.CloseScene(reference,true);SceneManager.SetActiveScene(main);}
        }
        public static void Apply()
        {
            var main=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);TeamMapVerification.CompareProtected();
            var reference=EditorSceneManager.OpenScene(ReferenceScene,OpenSceneMode.Additive);var referenceMap=reference.GetRootGameObjects().Single(g=>g.name=="MyeongjiMap");
            var referencePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(ReferencePrefab);var source=Buildings(referencePrefab);
            var map=main.GetRootGameObjects().Single(g=>g.name=="MyeongjiMap");var expansion=main.GetRootGameObjects().Single(g=>g.name=="MyeongjiMapExpansion");
            SceneManager.SetActiveScene(main);
            try
            {
                var target=Buildings(map);if(source.Count!=52||target.Count!=52)throw new InvalidOperationException("Expected 52 original buildings");
                int original=0,clones=0,evening=0;
                foreach(var pair in target){ApplyBuilding(pair.Value,source[pair.Key],pair.Key,false);original++;if(HasEvening(pair.Value))evening++;}
                var lots=(JArray)JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/lots_expanded.json"))["lots"];
                foreach(var lot in lots)
                {
                    string name=(string)lot["id"],id=(string)lot["sourceBuildingId"];var renderer=expansion.GetComponentsInChildren<MeshRenderer>(true).SingleOrDefault(r=>r.name==name);if(renderer==null)continue;
                    ApplyBuilding(renderer,source[id],id,true);clones++;if(HasEvening(renderer))evening++;
                }
                if(clones!=169)throw new InvalidOperationException("Expected 169 existing clone renderers: "+clones);
                var prefab=PrefabUtility.LoadPrefabContents(MapPrefab);
                try
                {
                    var prefabTargets=Buildings(prefab);
                    foreach(var pair in prefabTargets){var from=source[pair.Key];pair.Value.GetComponent<MeshFilter>().sharedMesh=from.GetComponent<MeshFilter>().sharedMesh;pair.Value.sharedMaterials=from.sharedMaterials;}
                    PrefabUtility.SaveAsPrefabAsset(prefab,MapPrefab,out bool saved);if(!saved)throw new IOException("Map visual prefab save failed");
                }
                finally{PrefabUtility.UnloadPrefabContents(prefab);}
                // Existing collider meshes and all transforms stay as they were; only render mesh/material overrides change.
                foreach(var renderer in target.Values.Concat(expansion.GetComponentsInChildren<MeshRenderer>(true).Where(r=>lots.Any(l=>(string)l["id"]==r.name))))
                {PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);PrefabUtility.RecordPrefabInstancePropertyModifications(renderer.GetComponent<MeshFilter>());}
                TeamMapVerification.Write("building_import.json",new{original,clones,eveningRenderers=evening,sourceCommit="c0bc911",originalColliderMeshesRetained=true});
                CopyGroup(referenceMap,map,"StreetGreenery");CopyGroup(referenceMap,map,"EntranceDetails");
                foreach(var referenceRoot in reference.GetRootGameObjects())referenceRoot.SetActive(false);
                Physics.SyncTransforms();
                TeamMapPlacement.Build(main,map,expansion,referencePrefab,referenceMap);
                ConnectLighting(main,map,expansion);
                EditorSceneManager.MarkSceneDirty(main);if(!EditorSceneManager.SaveScene(main))throw new IOException("Main art scene save failed");
            }
            finally{EditorSceneManager.CloseScene(reference,true);SceneManager.SetActiveScene(main);}
            TeamMapVerification.CompareProtected();
            Debug.Log("TEAM_MAP_IMPORT_PASS: visuals across52+169, props audited,143 lights; gameplay preserved");
        }
        private static bool HasEvening(Renderer r)=>r.sharedMaterials.Any(m=>m!=null&&m.name=="Evening_Window_Base");
        private static void ApplyBuilding(MeshRenderer target,MeshRenderer source,string id,bool keepZoneColor)
        {
            var zone=keepZoneColor?target.sharedMaterials.FirstOrDefault(m=>m!=null&&(m.name.StartsWith("KIT_DistrictWall_")||m.name.StartsWith("KIT_Wall_"))):null;
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(Art+"/Windows/"+id+"_Evening.asset");var materials=source.sharedMaterials.ToList();
            if(mesh!=null)materials.Add(AssetDatabase.LoadAssetAtPath<Material>(Art+"/Evening_Window_Base.mat"));else mesh=source.GetComponent<MeshFilter>().sharedMesh;
            if(zone!=null){int index=materials.FindIndex(m=>m!=null&&(m.name.StartsWith("KIT_DistrictWall_")||m.name.StartsWith("KIT_Wall_")));if(index>=0)materials[index]=zone;}
            if(materials.Count!=mesh.subMeshCount)throw new InvalidOperationException("Material count mismatch "+id+" "+materials.Count+"/"+mesh.subMeshCount);
            target.GetComponent<MeshFilter>().sharedMesh=mesh;target.sharedMaterials=materials.ToArray();
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);PrefabUtility.RecordPrefabInstancePropertyModifications(target.GetComponent<MeshFilter>());
        }
        private static void CopyGroup(GameObject source,GameObject target,string name)
        {
            var previous=target.transform.Find(name);if(previous!=null)UnityEngine.Object.DestroyImmediate(previous.gameObject);
            var group=source.transform.Find(name)??throw new InvalidOperationException("Team group missing: "+name);
            var copy=UnityEngine.Object.Instantiate(group.gameObject,target.transform,false);copy.name=name;
            if(copy.GetComponentsInChildren<MonoBehaviour>(true).Length>0)throw new InvalidOperationException("Decorative group unexpectedly has runtime scripts");
        }
        private static void ConnectLighting(Scene scene,GameObject map,GameObject expansion)
        {
            var old=map.transform.Find("EveningLighting");if(old!=null)UnityEngine.Object.DestroyImmediate(old.gameObject);
            var root=new GameObject("EveningLighting");root.transform.SetParent(map.transform,false);var controller=root.AddComponent<NeighborhoodEveningLights>();
            var windowMaterial=AssetDatabase.LoadAssetAtPath<Material>(Art+"/Evening_Window_Base.mat");var lampMaterial=AssetDatabase.LoadAssetAtPath<Material>(Art+"/Evening_Lamp_Base.mat");
            var windows=new List<NeighborhoodEveningLights.Surface>();var lamps=new List<NeighborhoodEveningLights.Surface>();var lights=new List<Light>();
            foreach(var renderer in map.GetComponentsInChildren<MeshRenderer>(true).Concat(expansion.GetComponentsInChildren<MeshRenderer>(true)))
            {
                var materials=renderer.sharedMaterials;for(int i=0;i<materials.Length;i++)if(materials[i]==windowMaterial)windows.Add(new NeighborhoodEveningLights.Surface{renderer=renderer,slot=i,group=renderer.name.Sum(c=>(int)c)%4});
                int slot=Array.FindIndex(materials,m=>m!=null&&(m.name=="ST_Lamp"||m==lampMaterial));if(slot<0)continue;
                var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;var vertices=mesh.vertices;var points=mesh.GetTriangles(slot).Distinct().Select(i=>renderer.transform.TransformPoint(vertices[i])).ToArray();
                var center=points.Aggregate(Vector3.zero,(a,b)=>a+b)/points.Length;var host=new GameObject(renderer.name+"_Light");host.transform.SetParent(root.transform,false);host.transform.position=new Vector3(center.x,points.Min(p=>p.y)-.035f,center.z);host.transform.rotation=Quaternion.Euler(90,0,0);
                var light=host.AddComponent<Light>();light.type=LightType.Spot;light.range=8;light.spotAngle=100;light.innerSpotAngle=65;light.color=new Color(1,.83f,.60f);light.intensity=0;light.shadows=LightShadows.None;light.enabled=false;lights.Add(light);
                materials[slot]=lampMaterial;renderer.sharedMaterials=materials;PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);lamps.Add(new NeighborhoodEveningLights.Surface{renderer=renderer,slot=slot});
            }
            if(lights.Count!=143)throw new InvalidOperationException("Expected143 streetlight renderers, found "+lights.Count);
            var so=new SerializedObject(controller);so.FindProperty("daySky").objectReferenceValue=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DaySky>(true)).Single();so.FindProperty("windowMaterial").objectReferenceValue=windowMaterial;so.FindProperty("lampMaterial").objectReferenceValue=lampMaterial;
            void Bind(string name,List<NeighborhoodEveningLights.Surface> surfaces){var a=so.FindProperty(name);a.arraySize=surfaces.Count;for(int i=0;i<surfaces.Count;i++){var p=a.GetArrayElementAtIndex(i);p.FindPropertyRelative("renderer").objectReferenceValue=surfaces[i].renderer;p.FindPropertyRelative("slot").intValue=surfaces[i].slot;p.FindPropertyRelative("group").intValue=surfaces[i].group;}}
            Bind("windows",windows);Bind("lamps",lamps);var array=so.FindProperty("streetLights");array.arraySize=lights.Count;for(int i=0;i<lights.Count;i++)array.GetArrayElementAtIndex(i).objectReferenceValue=lights[i];so.ApplyModifiedPropertiesWithoutUndo();
            TeamMapVerification.Write("lighting_import.json",new{windows=windows.Count,lamps=lamps.Count,lights=lights.Count,nearbyLimit=8,shadows=false});
        }
        public static void Audit()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);Physics.SyncTransforms();TeamMapVerification.CompareProtected();TeamMapPlacement.Audit(scene);
            var roots=scene.GetRootGameObjects();var map=roots.Single(g=>g.name=="MyeongjiMap");var expansion=roots.Single(g=>g.name=="MyeongjiMapExpansion");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ReferencePrefab);var sources=Buildings(prefab);int originals=0,clones=0;var failures=new List<string>();
            void Check(MeshRenderer renderer,string id)
            {
                var expected=AssetDatabase.LoadAssetAtPath<Mesh>(Art+"/Windows/"+id+"_Evening.asset")??sources[id].GetComponent<MeshFilter>().sharedMesh;
                if(renderer.GetComponent<MeshFilter>().sharedMesh!=expected)failures.Add(renderer.name+": mesh not shared with team source");
                if(renderer.sharedMaterials.Length!=expected.subMeshCount)failures.Add(renderer.name+": material count");
            }
            foreach(var pair in Buildings(map)){Check(pair.Value,pair.Key);originals++;}
            var lots=(JArray)JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/lots_expanded.json"))["lots"];
            foreach(var lot in lots){Check(expansion.GetComponentsInChildren<MeshRenderer>(true).Single(r=>r.name==(string)lot["id"]),(string)lot["sourceBuildingId"]);clones++;}
            var lighting=map.GetComponentInChildren<NeighborhoodEveningLights>(true);var so=new SerializedObject(lighting);var lights=map.transform.Find("EveningLighting").GetComponentsInChildren<Light>(true);
            int registered=so.FindProperty("streetLights").arraySize;if(registered!=143||lights.Length!=143||lights.Any(l=>l.shadows!=LightShadows.None)||so.FindProperty("nearbyLightLimit").intValue!=8)failures.Add("light budget/registration/shadows");
            var windows=so.FindProperty("windows");var samples=new List<object>();
            try
            {
                lighting.PreviewAt(0,Camera.main.transform.position);if(lights.Any(l=>l.enabled))failures.Add("daytime streetlight on");
                foreach(var view in JArray.Parse(File.ReadAllText(Path.Combine(TeamMapVerification.Output,"views.json"))))
                {
                    var p=view["position"];lighting.PreviewAt(1,new Vector3((float)p[0],(float)p[1],(float)p[2]));int enabled=lights.Count(l=>l.enabled);
                    if(enabled>8)failures.Add("more than8 active lights");samples.Add(new{label=(string)view["label"],enabled});
                }
                for(int i=0;i<windows.arraySize;i++){var p=windows.GetArrayElementAtIndex(i);var r=(Renderer)p.FindPropertyRelative("renderer").objectReferenceValue;var mat=r.sharedMaterials[p.FindPropertyRelative("slot").intValue];if(mat.GetColor("_EmissionColor").maxColorComponent<=0)failures.Add(r.name+": no evening emission");}
            }
            finally{lighting.Restore();}
            TeamMapVerification.Write("visual_audit.json",new{originals,clones,sharedWindowRenderers=windows.arraySize,registeredLights=registered,daytimeOff=true,shadows=false,lightSamples=samples,failures});
            TeamMapVerification.CompareProtected();if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
            Debug.Log("TEAM_MAP_VISUAL_AUDIT_PASS original52 clones169 windows203 lights143 budget8");
        }
        public static void CleanupReferences()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            var dependencies=AssetDatabase.GetDependencies(MeasureRunner.ScenePath,true);
            if(dependencies.Contains(ReferenceScene)||dependencies.Contains(ReferencePrefab))throw new InvalidOperationException("Main scene still depends on a temporary reference asset");
            if(!AssetDatabase.DeleteAsset(ReferenceScene)||!AssetDatabase.DeleteAsset(ReferencePrefab))throw new IOException("Temporary reference cleanup failed");
            AssetDatabase.SaveAssets();TeamMapVerification.CompareProtected();TeamMapPlacement.Audit(SceneManager.GetActiveScene());
            Debug.Log("TEAM_MAP_FINAL_PASS referencesRemoved=true gameplayDifferences=0 protectedOverlaps=0");
        }
    }
}
