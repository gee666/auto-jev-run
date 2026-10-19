"""Check 24 concurrent programming requests through the installed model queue."""
import concurrent.futures as cf
import json, statistics, threading, time, urllib.request
from capacity import ROOT, BASE, CASES, question_case

barrier=threading.Barrier(24)
def evaluate(i):
    body,expected=question_case(CASES[i%len(CASES)],16,42)
    body['state']=f'Request {i}. '+body['state']
    barrier.wait()
    start=time.perf_counter()
    request=urllib.request.Request(BASE+'/v1/systemone',data=json.dumps(body).encode(),headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(request,timeout=180) as response:
        data=json.load(response)
        return {'request':i,'status':response.status,'correct':data['answers']['answer']['choice']==expected,'seconds':time.perf_counter()-start,'queue_ms':float(response.headers['X-AutoJev-Queue-Milliseconds']),'result':data}

snapshots=[]
with cf.ThreadPoolExecutor(max_workers=24) as pool:
    futures=[pool.submit(evaluate,i) for i in range(24)]
    while not all(f.done() for f in futures):
        with urllib.request.urlopen(BASE+'/local/status',timeout=5) as response:snapshots.append(json.load(response))
        time.sleep(.25)
    results=[f.result() for f in futures]
summary={'requests':24,'successful':sum(r['status']==200 for r in results),'correct':sum(r['correct'] for r in results),'peak_active':max(s['activeRequests'] for s in snapshots),'peak_waiting':max(s['waitingRequests'] for s in snapshots),'median_seconds':statistics.median(r['seconds'] for r in results),'max_seconds':max(r['seconds'] for r in results),'backend_pids':sorted({s['backendPid'] for s in snapshots if s['backendPid'] is not None}),'results':results}
(ROOT/'reports/queue-live-validation.json').write_text(json.dumps(summary,indent=2))
assert summary['successful']==summary['correct']==24
assert summary['peak_active']==1 and summary['peak_waiting']>16 and len(summary['backend_pids'])==1
print(json.dumps({k:v for k,v in summary.items() if k!='results'},indent=2))
