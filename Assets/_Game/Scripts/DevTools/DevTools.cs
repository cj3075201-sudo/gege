using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeliveryRider
{
    // In-game developer tools (F1; editor and development builds only). Player, hazard, traffic, day/economy and
    // camera values change while the game keeps running; the rider's controls pause and the cursor is free while the
    // panel is open. Most values apply at once; counts and spawn spacing apply with the respawn buttons.
    // Unsaved changes last for this Play session, including daily scene loads. In the editor the save button writes them
    // to the settings assets and prefabs at once, and to the scene when Play Mode ends (DevToolsSaver).
    [DefaultExecutionOrder(-150)]
    public sealed partial class DevTools : MonoBehaviour
    {
        // Set by the editor; null in builds, which hides the save button. Returns the status line to show.
        public static System.Func<IReadOnlyList<DevChange>, string> Saver;

        private const float Margin = 12f, PanelWidth = 560f, LabelWidth = 210f, SliderWidth = 150f, FieldWidth = 72f, ButtonWidth = 52f;
        private static readonly string[] TabNames = { "결과", "플레이어", "사건", "교통", "하루·경제", "카메라", "하늘" };
        private static readonly string[] TabNotes =
        {
            "HTML 수치 패널과 같은 식입니다. 기록은 이번 판 동안 유지됩니다. 정산·일시정지는 경과시간에서 제외합니다.",
            "값은 바로 반영됩니다. 이동 속도는 인라인을 적용하기 전 기본값입니다.",
            "개수·간격·등장 일차는 '사건 다시 배치'를 눌러야 반영됩니다. 사건 내부 값은 지금 있는 사건과 다시 배치될 사건에 모두 적용되며, 트리거 크기 배율은 즉시 적용됩니다. 다른 크기·배치에 쓰이는 일부 값은 다시 배치한 뒤 반영됩니다.",
            "차량 수·보행자 수·배치 간격은 다시 배치할 때, 주행·보행 값은 바로 반영됩니다.",
            "보상·제한시간은 다음 콜부터, 하루 길이와 인라인 배율은 다음 날부터 반영됩니다.",
            "값은 바로 반영됩니다.",
            "미리보기는 하늘과 빛만 바꾸고 게임 시계는 그대로 둡니다. 해 그림자를 끄고 켜며 제목 줄의 프레임 시간을 비교할 수 있습니다. 색 그라디언트와 곡선은 설정 자산 Inspector에서 조정합니다.",
        };

        [SerializeField] private DeliverySession session;
        [SerializeField] private RiderMotor rider;
        [SerializeField] private RiderReactionVisual riderVisual;
        [SerializeField] private RiderSafeRecovery safeRecovery;
        [SerializeField] private RiderDust riderDust;
        [SerializeField] private ThirdPersonCamera followCamera;
        [SerializeField] private CurvedWorld curvedWorld;
        [SerializeField] private HazardPlanner hazards;
        [SerializeField] private HazardSpawnSettings hazardSettings;
        [SerializeField] private TrafficNetwork traffic;
        [SerializeField] private PedestrianNetwork pedestrians;
        [SerializeField] private DeliveryDaySettings daySettings;
        [SerializeField] private DaySky daySky;
        [Tooltip("Font with Hangul for the panel; the built-in IMGUI font has none.")]
        [SerializeField] private Font font;

        // Where a tuned hazard component sits inside its prefab, to find the same part in every spawned copy.
        private sealed class HazardPart
        {
            public string Type, Path;
            public System.Type Component;
            public int Index;
            public DevTarget Target;
        }

        private List<DevTarget>[] tabs;
        private readonly List<HazardPart> hazardParts = new List<HazardPart>();
        private readonly List<DevTarget> vehicleTargets = new List<DevTarget>();
        private DevTarget walkerTarget;
        private DevTarget seedTarget;
        private RunStats stats;
        private readonly Dictionary<DevValue, string> controlNames = new Dictionary<DevValue, string>();
        private readonly Dictionary<DevValue, string> typing = new Dictionary<DevValue, string>();
        private readonly Dictionary<string, int> placed = new Dictionary<string, int>();
        private readonly StringBuilder summary = new StringBuilder();
        private readonly Vector2[] scroll = new Vector2[TabNames.Length];
        private int tab;
        private bool open;
        private bool initialCopiesApplied;
        private float frameMilliseconds = 16.7f;
        private string status = "";
        private GUISkin skin;
        private GUIStyle panel, title, foldout, changedLabel, note, resultText;
        private Texture2D background;

        // Release builds leave the tools out.
        private void Awake()
        {
            if (!Debug.isDebugBuild) { enabled = false; return; }
            // Restore before DeliverySession.Awake captures the day length, economy and inline multipliers.
            Build();
            stats = gameObject.AddComponent<RunStats>();
            stats.Initialize(session, rider, daySettings);
        }

        private void OnEnable() { if (hazards != null) hazards.HazardSpawned += OnHazardSpawned; }

        private void OnDisable()
        {
            if (hazards != null) hazards.HazardSpawned -= OnHazardSpawned;
            if (open) SetOpen(false);
        }

        private void OnDestroy()
        {
            // Tuning persists through scene destruction; DevToolsSaver restores unsaved assets when Play ends.
            if (skin != null) Destroy(skin);
            if (background != null) Destroy(background);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) SetOpen(!open);
            // Smoothed frame time for the panel title.
            frameMilliseconds = Mathf.Lerp(frameMilliseconds, Time.unscaledDeltaTime * 1000f, 0.05f);
        }

        // The session locks the cursor again on every state change; keep it free while the panel is open.
        private void LateUpdate()
        {
            // Traffic and pedestrians spawn in Start, after this tool restores the authored scene fields in Awake.
            if (!initialCopiesApplied && tabs != null)
            {
                foreach (var list in tabs)
                    foreach (var target in list) { target.Refresh(); target.ReapplyChanges(); }
                initialCopiesApplied = true;
            }
            if (!open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SetOpen(bool value)
        {
            open = value;
            typing.Clear();
            if (open)
            {
                if (tabs == null) Build();
                else foreach (var list in tabs) foreach (var target in list) target.Refresh();
            }
            if (rider != null) rider.SetInputBlocked(open);
            bool look = !open && rider != null && rider.CanLook;
            Cursor.lockState = look ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !look;
        }

        private void Build()
        {
            tabs = new List<DevTarget>[TabNames.Length];
            for (int i = 0; i < tabs.Length; i++) tabs[i] = new List<DevTarget>();

            Single(tabs[1], "라이더 이동·점프·사고", rider, ShowJumpHeight);
            Single(tabs[1], "사고 연출", riderVisual);
            Single(tabs[1], "안전 위치 복귀", safeRecovery);
            Single(tabs[1], "걷기 먼지", riderDust);

            if (hazardSettings != null) tabs[2].Add(HazardCounts());
            if (hazards != null)
            {
                seedTarget = new DevTarget("배치 시드", hazards, () => new UnityEngine.Object[] { hazards });
                seedTarget.Add(new[] { DevValue.Find(hazards, "randomEachRun", "무작위 배치 (끄면 고정 시드)"),
                    DevValue.Find(hazards, "seed", "고정 시드") });
                tabs[2].Insert(0, seedTarget);
            }
            Single(tabs[2], "사건 발동 규칙", hazards,
                values => values.RemoveAll(value => value.PropertyPath == "randomEachRun" || value.PropertyPath == "seed"));
            if (hazardSettings != null && hazards != null) AddHazardParts(tabs[2]);

            Single(tabs[3], "차량 배치", traffic);
            if (traffic != null) AddVehicles(tabs[3]);
            Single(tabs[3], "보행자 배치", pedestrians);
            if (pedestrians != null && pedestrians.Prefab != null)
            {
                walkerTarget = new DevTarget("보행자", pedestrians.Prefab, () => pedestrians.Pedestrians);
                walkerTarget.Add(DevValue.Discover(pedestrians.Prefab));
                tabs[3].Add(walkerTarget);
            }

            Single(tabs[4], "배달 진행", session);
            Single(tabs[4], "하루 길이", daySettings);
            Single(tabs[4], "경제·보상", session != null ? session.Economy : null);

            Single(tabs[5], "3인칭 카메라", followCamera);
            Single(tabs[5], "커브드월드", curvedWorld);

            Single(tabs[6], "해 경로·빛", daySky != null ? daySky.Settings : null);
            // The pipeline asset is shared by every scene; a save writes the distance project-wide.
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset pipeline)
            {
                var shadows = new DevTarget("그림자 (렌더 파이프라인)", pipeline, () => new UnityEngine.Object[] { pipeline });
                shadows.Add(new[] { DevValue.Find(pipeline, "m_ShadowDistance", "그림자 거리(m)") });
                tabs[6].Add(shadows);
            }

            int id = 0;
            foreach (var list in tabs)
            {
                if (list.Count > 0) list[0].Expanded = true;
                foreach (var target in list) foreach (var value in target.Values) controlNames[value] = "dev" + id++;
            }
        }

        private static void Single(List<DevTarget> list, string label, UnityEngine.Object owner, System.Action<List<DevValue>> adjust = null)
        {
            if (owner == null) return;
            var values = DevValue.Discover(owner);
            adjust?.Invoke(values);
            if (values.Count == 0) return;
            var target = new DevTarget(label, owner, () => new[] { owner });
            target.Add(values);
            list.Add(target);
        }

        // Jump speed is tuned as the height it reaches.
        private static void ShowJumpHeight(List<DevValue> values)
        {
            int index = values.FindIndex(v => v.PropertyPath == "jumpSpeed");
            if (index < 0) return;
            float gravity = Mathf.Max(0.01f, -Physics.gravity.y);
            values[index] = values[index].Converted("점프 높이 (m)", speed => speed * speed / (2f * gravity),
                height => Mathf.Sqrt(2f * gravity * height), 0.1f, 6f);
        }

        // Objects place their daily count; triggers place a pool and a few of them light up per call.
        private DevTarget HazardCounts()
        {
            DevTuningSession.EnsureFourDays(hazardSettings);
            var values = new List<DevValue>();
            for (int i = 0; i < hazardSettings.Kinds.Count; i++)
            {
                var kind = hazardSettings.Kinds[i];
                string name = DevLabels.HazardType(kind.type), at = $"kinds.Array.data[{i}].";
                if (kind.role == HazardRole.Trigger) values.Add(DevValue.Find(hazardSettings, at + "poolSize", name + " 배치 수 (트리거)"));
                else if (kind.countPerDay.Length > 0)
                    for (int day = 1; day <= 4; day++)
                    {
                        values.Add(DevValue.Find(hazardSettings, $"{at}countPerDay.Array.data[{day - 1}]", $"{name} 개수 ({day}일차)"));
                    }
                values.Add(DevValue.Find(hazardSettings, at + "unlockDay", name + " 등장 일차"));
            }
            values.AddRange(DevValue.Discover(hazardSettings));
            var target = new DevTarget("사건 개수·간격 (다시 배치 시 적용)", hazardSettings, () => new UnityEngine.Object[] { hazardSettings });
            target.Add(values);
            return target;
        }

        // Each hazard prefab's own components. Changes reach every live copy and every copy spawned later.
        private void AddHazardParts(List<DevTarget> list)
        {
            foreach (var kind in hazardSettings.Kinds)
            {
                if (kind.prefab == null) continue;
                foreach (var part in kind.prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (part == null) continue;
                    var type = part.GetType();
                    if (type.Namespace != nameof(DeliveryRider)) continue;
                    var values = DevValue.Discover(part);
                    if (values.Count == 0) continue;
                    var entry = new HazardPart
                    {
                        Type = kind.type, Path = RelativePath(kind.prefab.transform, part.transform), Component = type,
                        Index = System.Array.IndexOf(part.GetComponents(type), part),
                    };
                    entry.Target = new DevTarget($"{DevLabels.HazardType(kind.type)} · {type.Name}", part, () => LiveParts(entry));
                    entry.Target.Add(values);
                    hazardParts.Add(entry);
                    list.Add(entry.Target);
                }
            }
        }

        private IEnumerable<UnityEngine.Object> LiveParts(HazardPart entry)
        {
            foreach (var slot in hazards.Slots)
            {
                if (slot == null || slot.Type != entry.Type) continue;
                var part = FindPart(slot.transform, entry);
                if (part != null) yield return part;
            }
        }

        // A new hazard takes the tuned values before it is placed and woken.
        private void OnHazardSpawned(GameObject hazard, string type)
        {
            foreach (var entry in hazardParts)
            {
                if (entry.Type != type) continue;
                var part = FindPart(hazard.transform, entry);
                if (part != null) entry.Target.ApplyTo(part);
            }
        }

        private static Component FindPart(Transform root, HazardPart entry)
        {
            var holder = entry.Path.Length == 0 ? root : root.Find(entry.Path);
            if (holder == null) return null;
            var parts = holder.GetComponents(entry.Component);
            return entry.Index < parts.Length ? parts[entry.Index] : null;
        }

        private static string RelativePath(Transform root, Transform part)
        {
            var names = new List<string>();
            for (var t = part; t != root; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        // Cars are grouped by kind; TrafficNetwork names each car after its prefab.
        private void AddVehicles(List<DevTarget> list)
        {
            foreach (var kind in traffic.Kinds)
            {
                if (kind.prefab == null) continue;
                string prefix = kind.prefab.name + "_";
                var target = new DevTarget("차량 · " + kind.prefab.name, kind.prefab, () => Cars(prefix));
                target.Add(DevValue.Discover(kind.prefab));
                vehicleTargets.Add(target);
                list.Add(target);
            }
        }

        private IEnumerable<UnityEngine.Object> Cars(string prefix)
        {
            foreach (var car in traffic.Cars)
                if (car != null && car.name.StartsWith(prefix, System.StringComparison.Ordinal)) yield return car;
        }

        private void RespawnHazards()
        {
            foreach (var entry in hazardParts) entry.Target.ClearLive();
            hazards.Respawn();
            int requested = 0;
            foreach (int count in hazards.RequestedCounts.Values) requested += count;
            status = $"사건 배치 {hazards.Slots.Count} / 요청 {requested}개 (후보·간격 제한 적용).";
        }

        private void RespawnTraffic()
        {
            traffic.Respawn();
            foreach (var target in vehicleTargets) { target.Refresh(); target.ReapplyChanges(); }
            status = $"차량 {traffic.Cars.Count}대를 다시 배치했습니다.";
        }

        private void RespawnWalkers()
        {
            pedestrians.Respawn();
            if (walkerTarget != null) { walkerTarget.Refresh(); walkerTarget.ReapplyChanges(); }
            status = $"보행자 {pedestrians.PedestrianCount}명을 다시 배치했습니다.";
        }

        private void RevertAll()
        {
            foreach (var list in tabs) foreach (var target in list) target.RevertAll();
            typing.Clear();
            status = "모든 변경을 되돌렸습니다.";
        }

        private void Save()
        {
            var changes = new List<DevChange>();
            foreach (var list in tabs) foreach (var target in list) target.CollectChanges(changes);
            if (changes.Count == 0) { status = "저장할 변경이 없습니다."; return; }
            try { status = Saver(changes); }
            catch (System.Exception error)
            {
                status = "저장하지 못했습니다: " + error.Message;
                Debug.LogException(error, this);
                return;
            }
            foreach (var list in tabs) foreach (var target in list) target.MarkSaved();
        }

        private string DescribeChanges()
        {
            var text = new StringBuilder();
            foreach (var list in tabs)
                foreach (var target in list)
                    foreach (var (value, before, after) in target.Edits)
                        text.Append(target.Title).Append(" / ").Append(value.Label).Append(" (").Append(value.PropertyPath).Append("): ")
                            .Append(value.Format(value.ToDisplay(before))).Append(" → ").Append(value.Format(value.ToDisplay(after))).AppendLine();
            if (text.Length == 0) text.AppendLine("변경 없음");
            text.AppendLine().AppendLine("결과 요약").Append(stats != null ? stats.Describe() : "진행 정보 없음");
            if (seedTarget != null)
                foreach (var value in seedTarget.Values)
                    text.AppendLine().Append(value.Label).Append(": ").Append(value.Format(seedTarget.Get(value)));
            return text.ToString();
        }

        private string PlacedSummary()
        {
            placed.Clear();
            foreach (var slot in hazards.Slots)
                if (slot != null) placed[slot.Type] = (placed.TryGetValue(slot.Type, out int count) ? count : 0) + 1;
            summary.Clear().Append("현재 배치/요청:");
            foreach (var kind in hazardSettings.Kinds)
            {
                placed.TryGetValue(kind.type, out int count);
                hazards.RequestedCounts.TryGetValue(kind.type, out int requested);
                summary.Append("  ").Append(DevLabels.HazardType(kind.type)).Append(' ').Append(count).Append('/').Append(requested);
            }
            return summary.ToString();
        }

        // IMGUI needs the same controls in its layout and event passes: anything that adds or removes rows (tabs,
        // folding) ends the pass with ExitGUI, and the per-row revert button is always drawn, only disabled.
        private void OnGUI()
        {
            if (!open || tabs == null) return;
            EnsureStyles();
            GUI.skin = skin;
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUILayout.BeginArea(new Rect(Margin, Margin, PanelWidth, Screen.height / scale - Margin * 2f), panel);
            GUILayout.Label($"개발자 도구 (F1로 닫기)   {frameMilliseconds:0.0}ms · {1000f / Mathf.Max(frameMilliseconds, 0.01f):0}fps", title);
            int selected = GUILayout.Toolbar(tab, TabNames);
            if (selected != tab)
            {
                tab = selected;
                typing.Clear();
                GUIUtility.ExitGUI();
            }
            GUILayout.Label(TabNotes[tab], note);
            DrawTabActions();
            scroll[tab] = GUILayout.BeginScrollView(scroll[tab]);
            if (tab == 0) DrawResults();
            else foreach (var target in tabs[tab]) if (target != seedTarget) DrawTarget(target);
            GUILayout.EndScrollView();
            DrawFooter();
            GUILayout.EndArea();
        }

        private void DrawTabActions()
        {
            if (tab == 2 && hazards != null && hazardSettings != null)
            {
                GUILayout.Label("배치 시드 (아래 값을 바꾼 뒤 다시 배치)", note);
                if (seedTarget != null) foreach (var value in seedTarget.Values) DrawValue(seedTarget, value);
                if (GUILayout.Button("사건 다시 배치")) RespawnHazards();
                GUILayout.Label(PlacedSummary(), note);
            }
            else if (tab == 3)
            {
                GUILayout.BeginHorizontal();
                if (traffic != null && GUILayout.Button("차량 다시 배치")) RespawnTraffic();
                if (pedestrians != null && GUILayout.Button("보행자 다시 배치")) RespawnWalkers();
                GUILayout.EndHorizontal();
            }
            else if (tab == 6 && daySky != null) DrawSkyPreview();
        }

        // Sky preview: another time for the sky and light only, from departure through closing to the end of the dusk;
        // dragging the slider starts the preview.
        private void DrawSkyPreview()
        {
            bool previewing = daySky.Preview.HasValue;
            bool wanted = GUILayout.Toggle(previewing, previewing ? "하늘 시각 미리보기 중 (끄면 실제 시각으로 돌아감)" : "하늘 시각 미리보기");
            float shown = daySky.ShownTimeline;
            if (wanted != previewing) daySky.Preview = wanted ? shown : (float?)null;
            GUILayout.BeginHorizontal();
            string at = shown <= 1f ? $"하루 {Mathf.RoundToInt(shown * 100f)}%" : $"마감 후 어스름 {Mathf.RoundToInt((shown - 1f) * 100f)}%";
            GUILayout.Label(at + " (출발 → 마감 → 어스름)", GUILayout.Width(LabelWidth));
            float slid = GUILayout.HorizontalSlider(shown, 0f, 2f, GUILayout.Width(SliderWidth + FieldWidth + 4f));
            if (!Mathf.Approximately(slid, shown)) daySky.Preview = slid;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("자동 진행 (하루/초)", GUILayout.Width(LabelWidth));
            daySky.PreviewSpeed = GUILayout.HorizontalSlider(daySky.PreviewSpeed, 0f, 0.2f, GUILayout.Width(SliderWidth));
            GUILayout.Label(daySky.PreviewSpeed.ToString("0.00", CultureInfo.InvariantCulture), GUILayout.Width(FieldWidth));
            GUILayout.EndHorizontal();
            // Compare the frame time (panel title) with the sun's shadows on and off; ends with the scene.
            if (daySky.Sun == null) return;
            bool shadows = daySky.Sun.shadows != LightShadows.None;
            if (GUILayout.Toggle(shadows, "해 그림자 (끄고 켜며 프레임 시간 비교)") != shadows)
                daySky.Sun.shadows = shadows ? LightShadows.None : LightShadows.Soft;
        }

        private void DrawTarget(DevTarget target)
        {
            int changes = target.ChangeCount;
            string label = (target.Expanded ? "▼ " : "▶ ") + target.Title + (changes > 0 ? $"  (변경 {changes})" : "");
            if (GUILayout.Button(label, foldout))
            {
                target.Expanded = !target.Expanded;
                GUIUtility.ExitGUI();
            }
            if (!target.Expanded) return;
            foreach (var value in target.Values)
            {
                if (value.Header != null) GUILayout.Label(value.Header, note);
                DrawValue(target, value);
            }
            GUILayout.Space(6f);
        }

        private void DrawValue(DevTarget target, DevValue value)
        {
            float shown = value.ToDisplay(target.Get(value));
            bool changed = target.IsChanged(value);
            float next = shown;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(value.Label, value.Tooltip), changed ? changedLabel : skin.label, GUILayout.Width(LabelWidth));
            if (value.IsBool)
            {
                bool on = shown >= 0.5f;
                next = GUILayout.Toggle(on, on ? "켜짐" : "꺼짐", GUILayout.Width(SliderWidth + FieldWidth + 4f)) ? 1f : 0f;
            }
            else
            {
                // The slider only replaces the value when it is dragged, so typed values beyond its range survive.
                float clamped = Mathf.Clamp(shown, value.Min, value.Max);
                float slid = GUILayout.HorizontalSlider(clamped, value.Min, value.Max, GUILayout.Width(SliderWidth));
                if (!Mathf.Approximately(slid, clamped))
                {
                    next = value.IsInt ? Mathf.Round(slid) : slid;
                    typing.Remove(value);
                }
                next = NumberField(value, next);
            }
            GUI.enabled = changed;
            bool revert = GUILayout.Button("복원", GUILayout.Width(ButtonWidth));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (revert)
            {
                target.Revert(value);
                typing.Remove(value);
            }
            else if (!Mathf.Approximately(next, shown)) target.Set(value, value.FromDisplay(next));
        }

        // While the field has focus the typed text stays as typed, so partial numbers such as "1." can be entered.
        private float NumberField(DevValue value, float shown)
        {
            string name = controlNames[value];
            bool focused = GUI.GetNameOfFocusedControl() == name;
            string text = focused && typing.TryGetValue(value, out var held) ? held : value.Format(shown);
            GUI.SetNextControlName(name);
            string typed = GUILayout.TextField(text, GUILayout.Width(FieldWidth));
            if (typed == text)
            {
                if (!focused) typing.Remove(value);
                return shown;
            }
            typing[value] = typed;
            return float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : shown;
        }

        private void DrawFooter()
        {
            int pending = 0;
            foreach (var list in tabs) foreach (var target in list) pending += target.ChangeCount;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("모두 되돌리기")) RevertAll();
            if (GUILayout.Button("변경 내역 복사"))
            {
                GUIUtility.systemCopyBuffer = DescribeChanges();
                status = $"변경 {pending}개를 클립보드에 복사했습니다.";
            }
            if (Saver != null && GUILayout.Button($"저장 ({pending})")) Save();
            GUILayout.EndHorizontal();
            GUILayout.Label(Saver != null ? "변경은 다음 날에도 유지됩니다. 저장하지 않은 값은 Play 종료 때 복원합니다."
                : "변경은 다음 날에도 유지되며 게임 실행 종료 때 초기화됩니다.", note);
            GUILayout.Label(status, note);
            GUILayout.Label(GUI.tooltip, note);
        }

        private void EnsureStyles()
        {
            if (skin != null) return;
            skin = Instantiate(GUI.skin);
            if (font != null) skin.font = font;
            background = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            background.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.1f, 0.94f));
            background.Apply();
            panel = new GUIStyle(skin.box) { padding = new RectOffset(10, 10, 8, 8) };
            panel.normal.background = background;
            title = new GUIStyle(skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            foldout = new GUIStyle(skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            changedLabel = new GUIStyle(skin.label);
            changedLabel.normal.textColor = new Color(1f, 0.78f, 0.3f);
            note = new GUIStyle(skin.label) { wordWrap = true, fontSize = 12 };
            note.normal.textColor = new Color(0.72f, 0.77f, 0.83f);
            resultText = new GUIStyle(skin.label) { wordWrap = true };
        }
    }
}
