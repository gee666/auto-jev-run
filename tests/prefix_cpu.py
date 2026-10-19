"""CPU-only public API prefix caching and accelerated expiry check."""
import json, pathlib, subprocess, time, urllib.request
import capacity as c
import prefix_cache as bench
import prefix_integration as api
ROOT=c.ROOT
root=ROOT/'.build/vision-cpu'
for directory in ['config','state','logs']:(root/directory).mkdir(parents=True,exist_ok=True)
if not (root/'runtime').exists():
    subprocess.run(['powershell','-NoProfile','-Command', "New-Item -ItemType Junction -Path '"+str(root/'runtime').replace("'","''")+"' -Target '"+str(ROOT/'runtime').replace("'","''")+"' | Out-Null"],check=True)
cfg=json.loads((ROOT/'config/server.json').read_text())
cfg.update(port=18875,backendPort=18876,mode='cpu',prefixCacheEnabled=False,prefixCacheSeconds=2,modelPath=str(ROOT/cfg['modelPath']),visionProjectorPath=str(ROOT/cfg['visionProjectorPath']))
(root/'config/server.json').write_text(json.dumps(cfg))
req=urllib.request.Request(c.BASE+'/local/unload',data=b'',headers={'X-AutoJev-Control':(ROOT/'state/control.token').read_text()})
with urllib.request.urlopen(req):pass
p=subprocess.Popen([str(ROOT/'app/AutoJev.exe'),'serve','--root',str(root)],creationflags=0x08000000)
api.BASE='http://127.0.0.1:18875'
result={}
try:
    for _ in range(60):
        if c.http('/local/status',base=api.BASE)[0]==200:break
        time.sleep(.25)
    cases=bench.cases()
    result['first_original']=api.call(cases[0][1],'false')
    pid=c.http('/local/status',base=api.BASE)[1]['backendPid']
    result['before_cache_memory']=c.proc(pid)
    result['warm_original']=api.call(cases[1][1],'false')
    result['cache_build']=api.call(cases[0][1])
    result['cache_hit']=api.call(cases[1][1])
    result['cached_memory']=c.proc(pid)
    time.sleep(3.5)
    result['expired_memory']=c.proc(pid)
    result['after_expiry']=api.call(cases[2][1])
    result['rewarm']=api.call(cases[3][1])
    assert result['cache_hit']['cached']>0 and result['after_expiry']['cached']==0 and result['rewarm']['cached']>0
    assert all(r['status']==200 for r in result.values() if 'status' in r)
    for name, expected in [('first_original','politics'),('warm_original','business'),('cache_build','politics'),('cache_hit','business'),('after_expiry','technology'),('rewarm','science')]:
        assert result[name]['body']['answers']['answer']['choice']==expected
    (ROOT/'reports/prefix-cache/cpu.json').write_text(json.dumps(result,indent=2))
    print({k:(r['seconds'],r['cached']) for k,r in result.items() if 'seconds' in r},flush=True)
    print('CPU checks passed',flush=True)
finally:
    req=urllib.request.Request(api.BASE+'/local/stop',data=b'',headers={'X-AutoJev-Control':(root/'state/control.token').read_text()})
    try:
        urllib.request.urlopen(req,timeout=5).close();p.wait(timeout=15)
    except Exception:p.kill();p.wait()
