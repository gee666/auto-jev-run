"""Real HTTP queue tests without loading model weights. Uses deterministic score requests."""
import concurrent.futures as cf
import contextlib, http.client, json, pathlib, subprocess, time, urllib.request, urllib.error

ROOT=pathlib.Path(__file__).resolve().parents[1]
PORT=18865
BASE=f'http://127.0.0.1:{PORT}'
BODY=json.dumps({'model':'jev-latest','state':'Queue transport test','questions':{'ok':{'type':'score','criteria':['Only level']}}}).encode()
checks=[]
def check(name,ok,detail=None):
    assert ok,(name,detail)
    checks.append({'check':name,'passed':True,'detail':detail})
    print('PASS',name,flush=True)

def call(path='/v1/systemone',body=BODY,headers=None):
    h={'Content-Type':'application/json',**(headers or {})}
    start=time.perf_counter()
    try:
        with urllib.request.urlopen(urllib.request.Request(BASE+path,data=body,headers=h),timeout=20) as r:
            return r.status,json.load(r),dict(r.headers),time.perf_counter()-start
    except urllib.error.HTTPError as e:return e.code,json.load(e),dict(e.headers),time.perf_counter()-start

def status():return call('/local/status',None)[1]
def until(predicate,seconds=5):
    start=time.perf_counter()
    while time.perf_counter()-start<seconds:
        try:
            value=predicate()
            if value:return value
        except OSError:pass
        time.sleep(.02)
    raise AssertionError('Wait expired')

@contextlib.contextmanager
def server(name,**settings):
    root=ROOT/'.build'/('queue-http-'+name)
    for sub in ['config','state','logs']:(root/sub).mkdir(parents=True,exist_ok=True)
    cfg=json.loads((ROOT/'config/server.json').read_text())
    cfg.update(port=PORT,backendPort=PORT+1,maxQueuedRequests=64,queueTimeoutSeconds=0,requestTimeoutSeconds=10)
    cfg.update(settings)
    (root/'config/server.json').write_text(json.dumps(cfg))
    p=subprocess.Popen([str(ROOT/'app/AutoJev.exe'),'serve','--root',str(root)],creationflags=0x08000000)
    try:
        until(lambda:status().get('root')==str(root))
        yield root
    finally:
        try:call('/local/stop',b'',{'X-AutoJev-Control':(root/'state/control.token').read_text()})
        except Exception:pass
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()

def stalled():
    c=http.client.HTTPConnection('127.0.0.1',PORT,timeout=20)
    c.putrequest('POST','/v1/systemone')
    c.putheader('Content-Type','application/json');c.putheader('Content-Length',str(len(BODY)))
    c.endheaders()
    return c

with server('burst') as root:
    with contextlib.closing(stalled()) as held:
        until(lambda:status()['activeRequests']==1)
        with cf.ThreadPoolExecutor(max_workers=24) as pool:
            futures=[pool.submit(call) for _ in range(24)]
            snapshot=until(lambda:(s if (s:=status())['waitingRequests']==24 else None))
            check('24 waiting requests admitted',snapshot['activeRequests']==1,snapshot)
            check('Cannot unload with queued requests',call('/local/unload',b'',{'X-AutoJev-Control':(root/'state/control.token').read_text()})[0]==409)
            canceled=stalled()
            until(lambda:status()['waitingRequests']==25)
            canceled.close()
            until(lambda:status()['waitingRequests']==24)
            check('Disconnected waiter removed immediately',True)
            held.send(BODY)
            response=held.getresponse();response.read()
            results=[f.result() for f in futures]
            check('All 24 waiting requests return their results',response.status==200 and all(r[0]==200 and r[1]['answers']['ok']['score']==0 for r in results))
            check('Queue time returned in response header',all('X-AutoJev-Queue-Milliseconds' in r[2] for r in results))
    check('Drained queue does not load model',status()['queuedRequests']==0 and status()['backendPid'] is None)

with server('processing-timeout',requestTimeoutSeconds=1):
    with contextlib.closing(stalled()) as first,contextlib.closing(stalled()) as second:
        until(lambda:status()['queuedRequests']==2)
        result=call()
        check('Queue wait does not consume processing timeout',result[0]==200 and result[3]>1.5,{'seconds':result[3],'status':result[0]})
        check('Timed-out active request advances queue',first.getresponse().status==504 and second.getresponse().status==504)

with server('overflow',maxQueuedRequests=1,queueTimeoutSeconds=1):
    with contextlib.closing(stalled()) as held:
        until(lambda:status()['activeRequests']==1)
        with cf.ThreadPoolExecutor(max_workers=1) as pool:
            waiting=pool.submit(call)
            until(lambda:status()['waitingRequests']==1)
            full=call()
            check('Bounded queue rejects overflow with retry delay',full[0]==429 and full[2].get('Retry-After')=='2')
            expired=waiting.result()
            check('Optional queue deadline is distinct',expired[0]==504 and 'waiting' in expired[1]['error']['message'])
            check('Expired waiter frees capacity',status()['waitingRequests']==0)
        held.send(BODY);held.getresponse().read()
        check('Requests work after overflow and timeout',call()[0]==200)

out=ROOT/'reports/queue-http-validation.json'
out.write_text(json.dumps(checks,indent=2))
print('All HTTP checks passed; isolated servers stopped.',flush=True)
