"""Reproducible AutoJev option-count and concurrency probes. Python standard library only."""
import argparse, concurrent.futures as cf, ctypes as C, ctypes.wintypes as W, json, math, os, pathlib, random, statistics, subprocess, threading, time, urllib.request, urllib.error
ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'reports'/'capacity-2026-09-27'; OUT.mkdir(exist_ok=True)
BASE='http://localhost:8765'
def http(path, body=None, base=BASE, key=None):
    data=None if body is None else json.dumps(body).encode()
    headers={'Content-Type':'application/json'}
    if key is None and path == '/v1/systemone' and base.rstrip('/') in ('http://localhost:8765', 'http://127.0.0.1:8765'):
        with urllib.request.urlopen(base+'/local/dashboard-key') as response: key=json.load(response)['key']
    if key: headers['Authorization']='Bearer '+key
    t=time.perf_counter()
    try:
        with urllib.request.urlopen(urllib.request.Request(base+path,data=data,headers=headers),timeout=900) as r:
            return r.status,json.load(r),time.perf_counter()-t
    except urllib.error.HTTPError as e:
        return e.code,e.read().decode(),time.perf_counter()-t
    except Exception as e: return 0,str(e),time.perf_counter()-t
def save(name,data): (OUT/name).write_text(json.dumps(data,indent=2),encoding='utf-8')
def status(): return http('/local/status')[1]
# Each scenario has two plausible but wrong alternatives. More unrelated engineering remedies
# probe distractor scaling, not universal 255-way semantic discrimination.
CASES=[
('sql','User input is concatenated into a SQL WHERE clause. The driver supports bound parameters. Choose the change that fixes SQL injection.',
 'Use a parameterized SQL query and bind the input as a value.',
 ['Remove whitespace before concatenating user input into SQL.','Catch SQL syntax errors without changing query construction.']),
('race','Two threads increment a shared in-memory counter with a read-modify-write operation. Updates are lost. Choose the fix.',
 'Protect the entire read-modify-write operation with a lock or atomic increment.',
 ['Make the counter volatile but keep the unsynchronized read-modify-write.','Retry a failed network request with exponential backoff.']),
('auth','A logged-in user can fetch another customer invoice by changing customer_id. Choose the fix.',
 'Check authorization and invoice ownership for the authenticated user before returning it.',
 ['Check only that customer_id is an integer.','Require login without checking invoice ownership.']),
('xss','A page inserts untrusted comments with innerHTML, allowing script execution. Comments should be plain text. Choose the fix.',
 'Render the comment as text using textContent rather than interpreting it as HTML.',
 ['Encode the database password with Base64.','Remove whitespace from comments before using innerHTML.']),
('timeout','An HTTP client can hang indefinitely when an upstream server never responds. Choose the fix.',
 'Set bounded connection and request timeouts and handle timeout failures.',
 ['Increase the database connection pool size.','Retry forever without a deadline.']),
('secret','An application logs the Authorization header including bearer tokens. Choose the fix.',
 'Redact or omit credentials from logs before writing them.',
 ['Rotate log files daily while keeping full credentials.','Compress the logs before uploading them.']),
('leak','An error path skips closing an opened file handle. Repeated failures exhaust descriptors. Choose the fix.',
 'Use a context manager or finally block to close the file on all paths.',
 ['Increase the number of worker threads.','Force a garbage collection after each successful request.']),
('order','A numeric field is stored as strings and sorting produces 1, 10, 2. Choose the fix.',
 'Parse and compare the values numerically when sorting.',
 ['Reverse the existing lexicographic sort.','Lowercase the string values before sorting.'])
]
def question_case(case,count,seed):
    name,state,correct,near=case
    options=[correct]+near[:max(0,count-1)]
    distractors=[f'{verb} for the {component}.' for verb in
      ['Add a dashboard metric','Write deployment documentation','Change the UI label','Add a configuration example','Create a maintenance checklist','Record a version number','Add a unit conversion helper','Improve startup diagnostics','Document ownership','Configure a monitoring alert','Add a release note','Set a retention policy','Create an integration fixture','Add a status page','Schedule a dependency audit','Add an operator guide']
      for component in ['image thumbnail service','DNS resolver','metrics exporter','calendar widget','backup scheduler','email template','static asset cache','translation catalog','build pipeline','PDF renderer','search index','audio preview','development container','feature flag service','log viewer','package mirror']]
    options=(options+distractors)[:count]
    random.Random(seed).shuffle(options)
    criteria={f'option_{i:03d}':s for i,s in enumerate(options)}
    expected=next(k for k,v in criteria.items() if v==correct)
    return {'model':'jev-latest','state':state,'questions':{'answer':{'type':'choice','instructions':'Choose the single most appropriate fix for the problem in the state.','criteria':criteria}}},expected
class PMC(C.Structure):
    _fields_=[('cb',W.DWORD),('PageFaultCount',W.DWORD)]+[(n,C.c_size_t) for n in ['PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage','PrivateUsage']]
kernel=C.WinDLL('kernel32',use_last_error=True); psapi=C.WinDLL('psapi',use_last_error=True)
kernel.OpenProcess.argtypes=[W.DWORD,W.BOOL,W.DWORD];kernel.OpenProcess.restype=W.HANDLE
kernel.CloseHandle.argtypes=[W.HANDLE]
psapi.GetProcessMemoryInfo.argtypes=[W.HANDLE,C.POINTER(PMC),W.DWORD]
kernel.GetProcessTimes.argtypes=[W.HANDLE,C.POINTER(W.FILETIME),C.POINTER(W.FILETIME),C.POINTER(W.FILETIME),C.POINTER(W.FILETIME)]
def proc(pid):
    h=kernel.OpenProcess(0x410,False,pid)
    if not h:return {}
    try:
        m=PMC();m.cb=C.sizeof(m);psapi.GetProcessMemoryInfo(h,C.byref(m),m.cb)
        a,b,k,u=W.FILETIME(),W.FILETIME(),W.FILETIME(),W.FILETIME()
        kernel.GetProcessTimes(h,C.byref(a),C.byref(b),C.byref(k),C.byref(u))
        ticks=lambda t:(t.dwHighDateTime<<32)+t.dwLowDateTime
        return {'rss_mib':m.WorkingSetSize/2**20,'private_mib':m.PrivateUsage/2**20,'cpu_seconds':(ticks(k)+ticks(u))/1e7}
    finally:kernel.CloseHandle(h)
class Sample:
    def __init__(self,pid):self.pid=pid;self.rows=[];self.end=threading.Event()
    def __enter__(self):
        self.start=proc(self.pid);self.t=time.perf_counter()
        def loop():
            while not self.end.is_set():
                row=proc(self.pid);row['elapsed']=time.perf_counter()-self.t
                try:
                    s=subprocess.check_output(['nvidia-smi','--query-gpu=memory.used,utilization.gpu,power.draw','--format=csv,noheader,nounits'],creationflags=0x08000000,text=True,timeout=5).strip().split(',')
                    row.update(gpu_total_mib=float(s[0]),gpu_util_percent=float(s[1]),gpu_power_w=float(s[2]))
                except Exception:pass
                self.rows.append(row);self.end.wait(.5)
        self.thread=threading.Thread(target=loop,daemon=True);self.thread.start();return self
    def __exit__(self,*_):
        self.end.set();self.thread.join(timeout=8);last=proc(self.pid)
        self.summary={'samples':len(self.rows),'peak_backend_rss_mib':max([x.get('rss_mib',0) for x in self.rows] or [0]),'peak_backend_private_mib':max([x.get('private_mib',0) for x in self.rows] or [0]),'peak_total_gpu_mib':max([x.get('gpu_total_mib',0) for x in self.rows] or [0]),'peak_gpu_util_percent':max([x.get('gpu_util_percent',0) for x in self.rows] or [0]),'mean_gpu_util_percent':statistics.mean([x.get('gpu_util_percent',0) for x in self.rows] or [0]),'mean_cpu_cores':(last.get('cpu_seconds',0)-self.start.get('cpu_seconds',0))/(time.perf_counter()-self.t)}
def evaluate(body,expected,base=BASE,key=None):
    code,r,seconds=http('/v1/systemone',body,base,key)
    return {'status':code,'seconds':seconds,'correct':code==200 and r['answers']['answer'].get('choice')==expected,'expected':expected,'answer':r['answers']['answer'] if code==200 else r,'input_tokens':r.get('usage',{}).get('input_tokens') if isinstance(r,dict) else None}
def padded(i,words,option_count=16):
    b,e=question_case(CASES[i%len(CASES)],option_count,42)
    # Different first text tokens prevent accidental identical-prefix wins.
    b['state']=f'Request {i:06d}. '+('audit entry verified timestamp normal service operation. '*words)+ '\nProblem: '+b['state']
    return b,e
def concurrency(base=BASE,key=None,pid=None,slots=1,public=True):
    data={'base':base,'slots':slots,'public_bridge':public,'note':'GPU samples are total device usage, including desktop; backend memory excludes other processes. Small sample empirical p95, not a production estimate.','groups':[]}
    b,e=padded(999,24);evaluate(b,e,base,key)
    if pid is None:pid=status()['backendPid']
    levels=[1,2,4,8,16] if public else [1,2,4,8]
    for words in ([24,110] if public else [110]):
        for level in levels:
            total=max(8,level)
            with Sample(pid) as monitor:
                start=time.perf_counter()
                with cf.ThreadPoolExecutor(max_workers=level) as pool:
                    futures=[pool.submit(evaluate,*padded(i+level*1000+words*100,words),base,key) for i in range(total)]
                    results=[f.result() for f in futures]
                wall=time.perf_counter()-start
            times=sorted(x['seconds'] for x in results)
            row={'padding_repeats':words,'concurrency':level,'requests':total,'wall_seconds':wall,'throughput_per_second':sum(x['status']==200 for x in results)/wall,'p50_seconds':statistics.median(times),'p95_seconds':times[math.ceil(len(times)*.95)-1],'max_seconds':max(times),'ok':sum(x['status']==200 for x in results),'correct':sum(x['correct'] for x in results),'resources':monitor.summary,'results':results}
            data['groups'].append(row);save(f'concurrency-slots-{slots}-{"public" if public else "direct"}.json',data)
            print(f'CONC slots={slots} words={words} c={level} ok={row["ok"]}/{total} p50={row["p50_seconds"]:.2f}s p95={row["p95_seconds"]:.2f}s rps={row["throughput_per_second"]:.3f}',flush=True)
    if public:
        barrier=threading.Barrier(24)
        def burst(i):barrier.wait();return evaluate(*padded(i+90000,110))
        with cf.ThreadPoolExecutor(max_workers=24) as pool:results=list(pool.map(burst,range(24)))
        data['overload_24']=results;save('concurrency-slots-1-public.json',data)
        print('OVERLOAD', {code:sum(r['status']==code for r in results) for code in {r['status'] for r in results}},flush=True)
    return data
def options():
    data={'note':'Eight hand-authored code-remediation scenarios, near-miss distractors plus unrelated remedies. One deterministic shuffled order per case/count. No universal quality bound.','rows':[]}
    for count in [2,4,8,16,32,64,128,255]:
        rows=[]
        for i,case in enumerate(CASES):
            b,e=question_case(case,count,count*100+i)
            result=evaluate(b,e);result.update(case=case[0],option_count=count);rows.append(result);data['rows'].append(result)
            save('option-counts.json',data)
        print(f'OPTIONS {count}: {sum(x["correct"] for x in rows)}/8 median={statistics.median(x["seconds"] for x in rows):.2f}s tokens={rows[0]["input_tokens"]}',flush=True)
    b,e=question_case(CASES[0],255,0);b['questions']['answer']['criteria']['option_255']='Do nothing.'
    data['boundary_256']=evaluate(b,e);save('option-counts.json',data)

def cache():
    data={'note':'Warm single-slot public API. Fixed options in all requests. Different-state cases change the first state tokens, leaving the problem and options identical. Six identical trials followed by six changed-state trials at each option count.','groups':[]}
    for count in [16,128]:
        b,e=question_case(CASES[0],count,42)
        b['state']='Request 000000. '+('audit entry verified timestamp normal service operation. '*110)+'\nProblem: '+b['state']
        evaluate(b,e)
        same=[];different=[]
        for i in range(6):
            same.append(evaluate(b,e))
        for i in range(6):
            changed=dict(b);changed['state']=b['state'].replace('Request 000000.',f'Request {i+1:06d}.')
            different.append(evaluate(changed,e))
        data['groups'].append({'option_count':count,'identical_request':same,'changed_state_same_options':different})
        save('cache.json',data)
        print(f'CACHE options={count} same={statistics.median(x["seconds"] for x in same):.2f}s changed={statistics.median(x["seconds"] for x in different):.2f}s',flush=True)

def long_context():
    evaluate(*padded(999,24))
    pid=status()['backendPid']
    data={'note':'Installed API, 16 choices and about 8000 input tokens. Four requests per point. Warm inference, latency includes queueing.','groups':[]}
    for level in [1,4]:
        with Sample(pid) as monitor:
            start=time.perf_counter()
            with cf.ThreadPoolExecutor(max_workers=level) as pool:
                futures=[pool.submit(evaluate,*padded(i+700000+level*100,950)) for i in range(4)]
                results=[f.result() for f in futures]
            wall=time.perf_counter()-start
        row={'concurrency':level,'requests':4,'wall_seconds':wall,'throughput_per_second':sum(r['status']==200 for r in results)/wall,'p50_seconds':statistics.median(r['seconds'] for r in results),'max_seconds':max(r['seconds'] for r in results),'correct':sum(r['correct'] for r in results),'resources':monitor.summary,'results':results}
        data['groups'].append(row);save('long-context.json',data)
        print(f'LONG c={level} median={row["p50_seconds"]:.2f}s max={row["max_seconds"]:.2f}s correct={row["correct"]}/4',flush=True)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('mode',choices=['public','options','direct','cache','long']);p.add_argument('--slots',type=int,default=1);p.add_argument('--pid',type=int);p.add_argument('--key');p.add_argument('--base',default='http://127.0.0.1:18766');a=p.parse_args()
    if a.mode=='options':options()
    elif a.mode=='cache':cache()
    elif a.mode=='long':long_context()
    elif a.mode=='public':concurrency()
    else:concurrency(a.base,a.key,a.pid,a.slots,False)

