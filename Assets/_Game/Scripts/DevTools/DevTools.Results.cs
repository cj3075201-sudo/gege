using System.Globalization;
using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class DevTools
    {
        private string startDayText;

        private void DrawResults()
        {
            GUILayout.Label(stats != null ? stats.Describe() : "진행 정보 없음", resultText);
            GUILayout.Space(10);
            GUILayout.Label("지금 판의 현금·빚", title);
            var progress = session != null ? session.Progress : null;
            GUI.enabled = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label("현금", GUILayout.Width(50));
            GUILayout.TextField(progress != null ? progress.Cash.ToString(CultureInfo.InvariantCulture) : "0");
            GUILayout.Label("빚", GUILayout.Width(40));
            GUILayout.TextField(progress != null ? progress.Debt.ToString(CultureInfo.InvariantCulture) : "0");
            GUILayout.Button("적용", GUILayout.Width(ButtonWidth));
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.Label("진행 데이터에 직접 변경 기능이 없어 현재 읽기 전용입니다. 시험용 변경 API 연결이 필요합니다.", note);
            GUILayout.Space(10);
            GUILayout.Label("일차 시험", title);
            GUILayout.Label("다음 날로: 현재 수입으로 조기 정산하고 진행합니다. 진행 중 주문은 건너뜁니다.\n지정 일차로 새 판 시작: 현금·인라인·이번 판 기록을 초기화합니다. 시험 이동은 저장파일을 기록하지 않습니다.", note);
            bool canMove = progress != null && !progress.IsEnded && SceneController.Instance != null;
            GUI.enabled = canMove;
            if (GUILayout.Button("다음 날로 (시험)"))
            {
                stats?.Poll();
                progress.CloseDay(session.Cash, session.CompletedCount, session.Economy != null ? session.Economy.LivingCost : 0);
                progress.StartNextDay();
                ReloadForTest(progress);
            }
            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            GUILayout.Label("시작 일차", GUILayout.Width(80));
            if (startDayText == null) startDayText = progress != null ? progress.Day.ToString(CultureInfo.InvariantCulture) : "1";
            startDayText = GUILayout.TextField(startDayText, GUILayout.Width(FieldWidth));
            bool valid = int.TryParse(startDayText, out int wanted) && wanted >= 1;
            GUI.enabled = valid && SceneController.Instance != null;
            if (GUILayout.Button("이 일차로 새 판 시작 (시험)"))
            {
                stats?.Poll();
                ReloadForTest(new RunProgress(wanted, session != null && session.Economy != null ? session.Economy.StartingDebt : 0));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!valid) GUILayout.Label("시작 일차는 1 이상의 정수로 입력하세요.", note);
            if (SceneController.Instance == null) GUILayout.Label("일차 이동에는 씬의 SceneController가 필요합니다.", note);
        }

        private void ReloadForTest(RunProgress progress)
        {
            var controller = SceneController.Instance;
            controller.Progress = progress;
            SetOpen(false);
            Time.timeScale = 1;
            controller.NextScene(ESceneType.GameScene);
            GUIUtility.ExitGUI();
        }
    }
}
