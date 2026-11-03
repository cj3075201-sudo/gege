# 1층 외관 작업 재개 위치 — 2026-11-03

- 현재 저장 씬 FourDistrictPrototype_Myeongji.unity, 맵 프리팹MyeongjiMap.prefab. 바닥Stage15, 보행202경로 유지.
- 완료: B02~B18 총17채. B01은 기존 로드뷰 상가 외관을 그대로 유지. 이번B구역 작업은 저장까지 완료.
- 첫5채: B02/B03/B04/B05/B06. 이후12채: B07~B18. B11은 아치형 공용현관, B07/B17은 단차 유지, B14는 꺾인 전면 중 실제 도로 쪽 면만 보완.
- 결과 메시 Assets/_Game/Art/Myeongji/Geometry/StreetFronts/Bxx_GroundFloor.asset. 새메시+.meta와 씬/프리팹 함께 관리. 원본FBX는 그대로이며재수출 시보완본을 덮어쓰지 않도록 주의.
- 다음: A구역 일반건물A03~A07 검토 후 보완. A01/A02의 기존 상가·아치형 원본은 이미 특징이 있으므로 필요 부분만 작업. 이후E/F/G/H. 전체맵 외관 확장은 아직 완료되지 않음.
- 기존6색/무광재질재사용, 글씨·간판없음. 원본상층부와단차유지. 출입문은 배달위치에맞추되 위치를옮기지않음. 보행공간2m와게임이벤트유지.
- 검토: 202경로8408표본 통과, 위치45/차량1270표본/근육맨50후보 접근조회통과. Play진입하지않음. 화면검토는필요한각도만, 기존카메라변경금지.
- Tools/MyeongjiGroundFloorB.cs는준비된B구역좌표검사자료를사용함. 다른구역을하려면먼저현재메시의정면/단차/창위치를다시읽어야함. 완료Apply재실행금지. FinishArch는B11마감가로대중복방지처리됨.
- 백업 C:/Users/PC/Documents/Codex/Myeongji_District/Facade_Batch02_Backup. Git커밋/푸시하지않음.
