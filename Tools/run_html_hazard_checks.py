"""Run explicitly requested Unity regressions sequentially in THIS worktree only."""
import argparse,json,subprocess
from pathlib import Path

UNITY=r'C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe'
PROJECT=r'C:\UnityWork\ge\codex_ground'
ROOT=Path('/mnt/c/UnityWork/ge/공유/결과')
CHECKS={
 'allocation':('DeliveryRider.Editor.HtmlAllocationPlayChecks.Run',False),
 'cat_defaults':('DeliveryRider.Editor.CatBoxerChecks.Run',False),
 'finalize':('DeliveryRider.Editor.HtmlHazardSettingsInstaller.FinalizeArtifacts',True),
 'rules':('DeliveryRider.Editor.HtmlHazardRuleChecks.Run',False),
 'stats':('DeliveryRider.Editor.DevToolsStatsChecks.Run',True),
 'zone':('DeliveryRider.Editor.ZoneRulesVerification.Run',True),
 'hazard_zone':('DeliveryRider.Editor.HazardZoneChecks.Run',False),
 'barrier':('DeliveryRider.Editor.HazardZoneChecks.RunBarrierAudit',False),
 'walker':('DeliveryRider.Editor.HazardZoneChecks.RunWalkerReturn',False),
 'gate':('DeliveryRider.Editor.HazardGateChecks.Run',False),
 'recovery':('DeliveryRider.Editor.MyeongjiExpansionInstaller.CheckRecovery',True),
 'falling_play':('DeliveryRider.Editor.FallingObjectPlayChecks.Run',False),
 'cat_play':('DeliveryRider.Editor.CatBoxerPlayChecks.Run',False),
 'dog_clone_play':('DeliveryRider.Editor.DogClonePlayChecks.Run',False),
 'duck':('DeliveryRider.Editor.DuckWallChecks.Run',True),
 'duck_play':('DeliveryRider.Editor.DuckWallPlayChecks.Run',False),
 'flat_flight':('DeliveryRider.Editor.HtmlFlightPlayChecks.Run',False),
 'map_play':('DeliveryRider.Editor.HtmlHazardPlayChecks.Run',False),
}

def main():
 p=argparse.ArgumentParser();p.add_argument('checks',nargs='*',choices=list(CHECKS));p.add_argument('--prefix',default='codex_html_final');a=p.parse_args()
 results=[]
 for key in a.checks or list(CHECKS):
  method,quit_flag=CHECKS[key];log=ROOT/(a.prefix+'_'+key+'.log')
  cmd=[str(Path.home()/'.local/bin/winquiet.exe'),UNITY,'-batchmode','-projectPath',PROJECT,'-executeMethod',method,'-logFile',str(log).replace('/mnt/c/','C:\\').replace('/','\\')]
  if quit_flag:cmd.append('-quit')
  result=subprocess.run(cmd,timeout=480)
  text=log.read_text(encoding='utf-8-sig',errors='replace') if log.exists() else ''
  row=dict(check=key,exit=result.returncode,compilerErrors=sum('error CS' in line for line in text.splitlines()),log=str(log),passLines=[line for line in text.splitlines() if 'PASS' in line and not line.startswith('  ')])
  results.append(row);print(json.dumps(row,ensure_ascii=False),flush=True)
  (ROOT/(a.prefix+'_results.json')).write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
  if result.returncode or row['compilerErrors']:raise SystemExit(1)
if __name__=='__main__':main()
