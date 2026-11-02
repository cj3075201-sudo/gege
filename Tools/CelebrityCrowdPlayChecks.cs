using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeliveryRider;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class CelebrityCrowdPlayChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private).GetValue(owner);
    static CelebrityCrowdHazard[] Crowds() => UnityEngine.Object.FindObjectsByType<CelebrityCrowdHazard>(FindObjectsSortMode.None).OrderBy(c => c.name).ToArray();
    static string Json(object value) => JsonConvert.SerializeObject(value);

    public static string Prepare()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop before preparing.");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        if (Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty))
            throw new InvalidOperationException("Unsaved scene changes; preserve them before switching.");
        SessionState.SetString("CrowdChecks.SceneSetup", Json(setup));
        SessionState.SetBool("CrowdChecks.Background", Application.runInBackground);
        Application.runInBackground = true;
        EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity", OpenSceneMode.Single);
        return Json(new { scene = SceneManager.GetActiveScene().path, background = Application.runInBackground });
    }

    public static string Approach(int index = 0)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play Mode required.");
        var crowd = Crowds()[index];
        var session = Field<DeliverySession>(crowd, "session");
        var ground = Field<RoadNetwork>(crowd, "ground");
        var player = session.Player.GetComponent<Rigidbody>();
        var position = ground.OnGround(crowd.transform.position + crowd.transform.forward * 6f, 0.05f);
        player.transform.position = position;
        player.position = position;
        player.linearVelocity = Vector3.zero;
        Physics.SyncTransforms();
        return Snapshot();
    }

    public static string Snapshot()
    {
        var crowds = Crowds();
        return Json(new { frame = Time.frameCount, time = Time.time, scale = Time.timeScale,
            crowds = crowds.Select(c => new { name = c.name, position = c.transform.position.ToString("F2"), appeared = Field<bool>(c, "appeared"),
                actors = c.GetComponentsInChildren<TrafficPedestrian>().Select(a => {
                    var body = a.GetComponent<Rigidbody>();
                    var center = Field<Transform>(a, "crowdCenter");
                    var away = Vector3.ProjectOnPlane(body.position - center.position, Vector3.up);
                    var direction = -away.normalized;
                    var blockers = Physics.CapsuleCastAll(body.position + Vector3.up * 0.65f, body.position + Vector3.up * 1.45f,
                        0.28f, direction, 0.15f, ~0, QueryTriggerInteraction.Ignore).Where(h => h.rigidbody != body && h.normal.y < 0.7f).Select(h => h.collider.name).ToArray();
                    return new { name = a.name, position = a.transform.position.ToString("F2"), body = body.position.ToString("F2"),
                        home = Field<Vector3>(a, "crowdHome").ToString("F2"), radius = away.magnitude,
                        flying = Field<bool>(a, "flying"), blockers };
                }).ToArray() }).ToArray() });
    }

    public static async Task<string> Observe(float seconds = 5f)
    {
        int frame = Time.frameCount;
        float start = Time.time;
        double deadline = EditorApplication.timeSinceStartup + seconds + 15f;
        while (Time.time - start < seconds && EditorApplication.timeSinceStartup < deadline) await Task.Delay(50);
        return Json(new { framesAdvanced = Time.frameCount - frame, simulationSeconds = Time.time - start, snapshot = JsonConvert.DeserializeObject(Snapshot()) });
    }

    public static string Metrics(int index = 0)
    {
        var crowd = Crowds()[index];
        var actors = crowd.GetComponentsInChildren<TrafficPedestrian>();
        var fans = actors.Where(a => a.name != "Celebrity").ToArray();
        float minGap = float.PositiveInfinity;
        int penetrations = 0;
        for (int i = 0; i < fans.Length; i++) for (int j = i + 1; j < fans.Length; j++)
        {
            minGap = Mathf.Min(minGap, Vector3.ProjectOnPlane(fans[i].transform.position - fans[j].transform.position, Vector3.up).magnitude);
            var a = fans[i].GetComponent<CapsuleCollider>(); var b = fans[j].GetComponent<CapsuleCollider>();
            if (Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out _, out float depth) && depth > 0.015f) penetrations++;
        }
        var radii = fans.Select(a => Vector3.ProjectOnPlane(a.transform.position - Field<Transform>(a, "crowdCenter").position, Vector3.up).magnitude).ToArray();
        return Json(new { frame = Time.frameCount, time = Time.time, crowd = crowd.name, appeared = Field<bool>(crowd, "appeared"),
            fans = fans.Length, minFanGap = minGap, overlappingPairs = penetrations,
            minRadius = radii.DefaultIfEmpty().Min(), maxRadius = radii.DefaultIfEmpty().Max(), within3m = radii.Count(r => r <= 3f),
            meanRadius = radii.DefaultIfEmpty().Average(),
            farFans = fans.Where((a,i) => radii[i] > 3f).Select(a => new { a.name, at = a.transform.position.ToString("F2"),
                radius = Vector3.ProjectOnPlane(a.transform.position - Field<Transform>(a, "crowdCenter").position, Vector3.up).magnitude }).ToArray() });
    }

    public static async Task<string> Exercise(int index = 0, float seconds = 10f)
    {
        var before = JsonConvert.DeserializeObject(Metrics(index));
        Approach(index);
        float start = Time.time;
        double deadline = EditorApplication.timeSinceStartup + 30f;
        while (!Field<bool>(Crowds()[index], "appeared") && EditorApplication.timeSinceStartup < deadline) await Task.Delay(10);
        var spawned = JsonConvert.DeserializeObject(Metrics(index));
        int frame = Time.frameCount;
        while (Time.time - start < seconds && EditorApplication.timeSinceStartup < deadline + seconds) await Task.Delay(50);
        return Json(new { before, spawned, after = JsonConvert.DeserializeObject(Metrics(index)), framesAdvanced = Time.frameCount - frame,
            snapshot = JsonConvert.DeserializeObject(Snapshot()) });
    }

    public static string FrameCrowd(int index = 0)
    {
        foreach (var follow in UnityEngine.Object.FindObjectsByType<ThirdPersonCamera>(FindObjectsSortMode.None)) follow.enabled = false;
        var center = Crowds()[index].transform.position;
        var camera = Camera.main;
        camera.transform.position = center + new Vector3(0f, 16f, -11f);
        camera.transform.rotation = Quaternion.LookRotation(center + Vector3.up * 0.8f - camera.transform.position);
        return "Camera framed on the live crowd.";
    }

    public static async Task<string> Retreat(int index = 0)
    {
        var crowd = Crowds()[index];
        var fan = crowd.GetComponentsInChildren<TrafficPedestrian>().First(a => a.name == "WaitingFan_00");
        var center = Field<Transform>(fan, "crowdCenter");
        var body = fan.GetComponent<Rigidbody>();
        var outward = Vector3.ProjectOnPlane(fan.transform.position - center.position, Vector3.up).normalized;
        var position = center.position + outward * 1.1f;
        var facing = Quaternion.LookRotation(-outward);
        body.interpolation = RigidbodyInterpolation.None;
        fan.transform.SetPositionAndRotation(position, facing);
        body.position = position; body.rotation = facing;
        Physics.SyncTransforms();
        body.interpolation = RigidbodyInterpolation.Interpolate;
        float start = Time.time;
        double deadline = EditorApplication.timeSinceStartup + 10f;
        while (Time.time - start < 1.5f && EditorApplication.timeSinceStartup < deadline) await Task.Delay(30);
        var toward = Vector3.ProjectOnPlane(center.position - body.position, Vector3.up);
        return Json(new { frames = Time.frameCount, startedAt = 1.1f, finalRadius = toward.magnitude,
            facingDot = Vector3.Dot(fan.transform.forward, toward.normalized),
            flying = Field<bool>(fan, "flying"), simulationSeconds = Time.time - start });
    }

    public static string Restore()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop before restoring.");
        Application.runInBackground = SessionState.GetBool("CrowdChecks.Background", false);
        string saved = SessionState.GetString("CrowdChecks.SceneSetup", "");
        if (!string.IsNullOrEmpty(saved))
        {
            var setup = JsonConvert.DeserializeObject<SceneSetup[]>(saved);
            if (setup.All(s => !string.IsNullOrEmpty(s.path))) EditorSceneManager.RestoreSceneManagerSetup(setup);
        }
        return "Stopped; restored background setting and saved scene setup.";
    }

    public static string Cleanup()
    {
        string restored = Restore();
        const string folder = "Assets/Library/CrowdPlayChecks";
        AssetDatabase.DeleteAsset(folder + "/first-fix.png");
        if (System.IO.Directory.Exists(folder) && System.IO.Directory.GetFileSystemEntries(folder).Length == 0)
            AssetDatabase.DeleteAsset(folder);
        if (System.IO.Directory.Exists("Assets/Library") && System.IO.Directory.GetFileSystemEntries("Assets/Library").Length == 0)
            AssetDatabase.DeleteAsset("Assets/Library");
        return restored + " Removed the temporary capture.";
    }
}
