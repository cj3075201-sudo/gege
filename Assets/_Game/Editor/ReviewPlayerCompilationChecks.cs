using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class ReviewPlayerCompilationChecks
    {
        public static void Run()
        {
            foreach(bool development in new[]{false,true})
            {
                string output="Library/ReviewPlayerScripts/"+(development?"Development":"Release");Directory.CreateDirectory(output);
                var settings=new ScriptCompilationSettings{target=BuildTarget.StandaloneWindows64,group=BuildTargetGroup.Standalone,
                    subtarget=(int)StandaloneBuildSubtarget.Player,options=development?ScriptCompilationOptions.DevelopmentBuild:0};
                var result=PlayerBuildInterface.CompilePlayerScripts(settings,output);
                string assembly=Path.Combine(output,"Assembly-CSharp.dll");
                if(result.assemblies==null||result.assemblies.Count==0||!File.Exists(assembly))throw new InvalidOperationException("Player script compilation failed");
                string metadata=Encoding.UTF8.GetString(File.ReadAllBytes(assembly));
                if(metadata.Contains("LastRollMilliseconds")!=development)throw new InvalidOperationException("Diagnostic compilation guard mismatch");
                Debug.Log("REVIEW_PLAYER_COMPILE_PASS development="+development+" diagnostics="+metadata.Contains("LastRollMilliseconds"));
            }
        }
    }
}
