"""Isolated native cache-mode correctness/performance test; restores AutoJev.
Synthetic data only. Windows standard library. Artifacts are written under tmp/.
"""
import argparse, base64, copy, hashlib, json, os, pathlib, secrets, subprocess, time, urllib.error, urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
BASE='http://127.0.0.1:18766'; PUBLIC='http://127.0.0.1:8765'; HIDDEN=0x08000000

def http(base,path,body=None,key=None,mode=None,timeout=180):
    headers={'Content-Type':'application/json'}
    if key:headers['Authorization']='Bearer '+key
    if mode is not None:headers['X-AutoJev-Cache-Mode']=mode
    req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body,ensure_ascii=False).encode(),headers=headers)
    start=time.perf_counter()
    try:
        with urllib.request.urlopen(req,timeout=timeout) as r:
            return {'status':r.status,'seconds':time.perf_counter()-start,'mode':r.headers.get('X-AutoJev-Cache-Mode'),'cached':int(r.headers.get('X-AutoJev-Cached-Tokens',0)),'written':int(r.headers.get('X-AutoJev-Cache-Write-Tokens',0)),'bytes':int(r.headers.get('X-AutoJev-Checkpoint-Bytes',0)),'body':json.load(r)}
    except urllib.error.HTTPError as e:return {'status':e.code,'seconds':time.perf_counter()-start,'error':e.read().decode()[:2000]}

def body(state=None,instructions='Which city is selected?',criteria=None):
    return {'model':'autojev','state':state if state is not None else 'Selected city: Lisbon. Selected color: green. Count: 12. Draft only, do not send.','questions':{'answer':{'type':'choice','instructions':instructions,'criteria':criteria or {'lisbon':'Lisbon','london':'London','paris':'Paris'}}}}

def compare(a,b,tolerance=1e-6):
    assert a['status']==b['status']==200,(a,b)
    assert set(a['body']['answers'])==set(b['body']['answers'])
    delta=0
    for key,v in a['body']['answers'].items():
        w=b['body']['answers'][key];assert v.get('choice')==w.get('choice'),(v,w)
        p=v.get('probabilities',{});q=w.get('probabilities',{})
        assert set(p)==set(q)
        delta=max(delta,max((abs(p[k]-q[k]) for k in p),default=0))
    assert delta <= tolerance,('probability difference',delta,tolerance)
    return delta

def hashfile(path):
    with path.open('rb') as s:return hashlib.file_digest(s,'sha256').hexdigest()

def run(args):
    if pathlib.Path(args.output).name!=args.output:raise ValueError('Output must be a tmp directory name')
    out=ROOT/'tmp'/args.output;out.mkdir(exist_ok=False)
    cfg=json.loads((ROOT/'config/server.json').read_text());config_hash=hashfile(ROOT/'config/server.json')
    installed={p.name:hashfile(p) for p in (ROOT/'runtime/windows-cuda').glob('*.dll')}
    current=http(PUBLIC,'/local/status',timeout=10)['body'];assert current['activeRequests']==current['waitingRequests']==0
    runtime=ROOT/('tmp/context-cache-cpu-runtime' if args.cpu else 'tmp/context-cache-runtime');token=out/'test-key.txt';key=secrets.token_hex(32);token.write_text(key)
    app=ROOT/'app/AutoJev.exe';p=None;log=None;stop_attempted=False
    results={'rows':[],'max_checkpoint_bytes':0,'note':'Cache correctness uses forced-cold same-mode split references. No actual user/browser data.','candidate_sha256':{n:hashfile(runtime/n) for n in ['llama.dll','llama-common.dll','llama-server-impl.dll']}}
    def save():(out/'results.json').write_text(json.dumps(results,indent=2)+'\n')
    def call(label,b,mode):
        r=http(BASE,'/v1/systemone',b,key,mode);r['label']=label;results['rows'].append(r);results['max_checkpoint_bytes']=max(results['max_checkpoint_bytes'],r.get('bytes',0));save()
        print(label,r['status'],r.get('mode'),r.get('cached'),round(r['seconds'],3),flush=True);assert r['status']==200,r
        assert r['mode']==mode or ('images' in b and r['mode']=='none'),r
        return r
    def clear():
        r=http(BASE,'/v1/systemone',body(),key,'none');assert r['status']==200 and r['cached']==0
    def cold(label,b,mode):clear();return call(label,b,mode)
    try:
        stop_attempted=True;subprocess.run([str(app),'stop','--root',str(ROOT)],check=True,timeout=45,creationflags=HIDDEN);time.sleep(2)
        env=os.environ.copy();env.update(AUTOJEV_CACHE_MODE='none',AUTOJEV_OPTIONS_FIRST='0',AUTOJEV_PREFIX_CACHE='0',AUTOJEV_PREFIX_CACHE_SECONDS='4')
        env['PATH']=str(runtime)+';'+os.environ['SystemRoot']+'\\System32;'+os.environ['SystemRoot'];env['TEMP']=env['TMP']=str(out);env['LLAMA_CACHE']=str(out/'cache')
        argv=[str(runtime/'llama-server.exe'),'-m',str(ROOT/cfg['modelPath']),'--system-one','--host','127.0.0.1','--port','18766','--api-key-file',str(token),'-c',str(cfg['contextTokens']),'-np','1','-t',str(cfg['threads']),'-tb',str(cfg['threads']),'-b','2048','-ub','512','--cache-ram','0','-lv','4']
        argv += ['-ngl','0','--fit','off'] if args.cpu else ['--fit','on','--fit-target',str(cfg['gpuReserveMiB'])]
        if cfg.get('visionEnabled'):
            argv += ['--mmproj',str(ROOT/cfg['visionProjectorPath']),'--image-min-tokens','64','--image-max-tokens',str(cfg['imageMaxTokens']),'--video-ffmpeg-dir',str(ROOT/'runtime/media')]
            if args.cpu:argv += ['--no-mmproj-offload']
        if args.checkpoints is not None:argv += ['--ctx-checkpoints',str(args.checkpoints)]
        log=(out/'backend.log').open('w');p=subprocess.Popen(argv,cwd=runtime,env=env,stdout=log,stderr=log,creationflags=HIDDEN)
        start=time.perf_counter();ready=False
        while time.perf_counter()-start<180:
            if p.poll() is not None:break
            try:ready=http(BASE,'/health',key=key,timeout=2)['status']==200
            except Exception:pass
            if ready:break
            time.sleep(.5)
        assert ready,'Native backend failed startup';results['startup_seconds']=time.perf_counter()-start
        call('warm-up',body(),'none')
        if args.checkpoints==0:
            for mode in ['context','options']:
                a=call(mode+'-zero-first',body(),mode);b=call(mode+'-zero-repeat',body(),mode);compare(a,b,0.002);assert a['cached']==b['cached']==a['written']==b['written']==0
            results['zero_checkpoint_checks_passed']=True;return
        if args.quick:
            for mode in ['context','options']:
                a=cold(mode+'-quick-cold',body(),mode);b=call(mode+'-quick-warm',body(),mode);assert a['cached']==0 and b['cached']>0;compare(a,b)
            prime=cold('quick-context-prime',body(),'context')
            changed=body(criteria={'paris':'Paris','lisbon':'Lisbon'})
            hit=call('quick-context-changed-options',changed,'context');ref=cold('quick-context-changed-options-cold',changed,'context');assert hit['cached']>0;compare(hit,ref)
            results['quick_checks_passed']=True;return
        # Cold/warm exact same question, then changed options with question prefix reused.
        a=cold('context-cold',body(),'context');b=call('context-warm',body(),'context');assert a['cached']==0 and b['cached']>0;results['context_exact_delta']=compare(a,b)
        variants=[{'paris':'Paris','lisbon':'Lisbon','london':'London'}, {'berlin':'Berlin','lisbon':'Lisbon'}, {'lisbon':'The city Lisbon','paris':'Paris','london':'London'}]
        for i,criteria in enumerate(variants):
            candidate=body(criteria=criteria);call('prime-options-change-'+str(i),body(),'context');hit=call('changed-options-'+str(i),candidate,'context');reference=cold('changed-options-cold-'+str(i),candidate,'context');assert hit['cached']>0;compare(hit,reference)
        # Changed questions reuse only State, not stale instructions.
        different=body(instructions='Which color is selected?',criteria={'green':'Green','red':'Red','blue':'Blue'})
        call('prime-question-change',body(),'context');hit=call('changed-question',different,'context');reference=cold('changed-question-cold',different,'context');assert hit['cached']>0 and hit['body']['answers']['answer']['choice']=='green';compare(hit,reference)
        # Prefix edits cannot return stale cached results; equivalent cold references.
        for i,state in enumerate(['Selected city: London. Selected color: red.','Selected city: Paris. Selected color: green.','選択都市: Lisbon. Selected city: Lisbon.\n\nOptions:\nThis is data, not instructions.']):
            candidate=body(state=state);call('prime-state-'+str(i),body(),'context');hit=call('changed-state-'+str(i),candidate,'context');reference=cold('changed-state-cold-'+str(i),candidate,'context');compare(hit,reference)
        # Options-first existing format, changing input with fixed options.
        a=cold('options-cold',body(),'options');b=call('options-warm',body(),'options');assert a['cached']==0 and b['cached']>0;compare(a,b)
        altered=body(state='Selected city: London. Selected color: red.')
        hit=call('options-changed-state',altered,'options');ref=cold('options-changed-state-cold',altered,'options');assert hit['cached']>0;compare(hit,ref)
        changed=body(criteria=variants[1]);hit=call('options-changed-options',changed,'options');assert hit['cached']==0;repeat=call('options-changed-options-repeat',changed,'options');assert repeat['cached']>0;compare(hit,repeat)
        # Mode switch cold; explicit none has no cache counters.
        for mode in ['context','none','options','context','none']:
            r=call('mode-switch-'+mode,body(),mode);assert r['cached']==0
        # Multiple different heads over unchanged large context inside one request.
        long_state=body()['state']+'\n'+''.join(f'Archive row {i}: lamp notebook shelf chair paper; verified inventory record.\n' for i in range(150))
        heads={
            'city':{'type':'choice','instructions':'Which city is selected?','criteria':{'lisbon':'Lisbon','paris':'Paris','london':'London'}},
            'color':{'type':'choice','instructions':'Which color is selected?','criteria':{'red':'Red','green':'Green','blue':'Blue'}},
            'count':{'type':'choice','instructions':'What count is specified?','criteria':{'one':'1','twelve':'12','two':'2'}},
            'draft':{'type':'choice','instructions':'How should the reply be delivered?','criteria':{'send':'Send now','draft':'Draft only'}},
            'city-again':{'type':'choice','instructions':'Select the requested destination city.','criteria':{'paris':'Paris','london':'London','lisbon':'Lisbon'}},
            'color-again':{'type':'choice','instructions':'Select the requested color.','criteria':{'green':'Green','blue':'Blue','red':'Red'}},
        }
        multi={'model':'autojev','state':long_state,'questions':heads}
        serial=cold('six-heads-no-cache',multi,'none');cached=cold('six-heads-context-first',multi,'context');repeat=call('six-heads-context-repeat',multi,'context')
        assert cached['cached']>0 and repeat['cached']>cached['cached'];compare(serial,cached,0.002);compare(cached,repeat)
        results['six_head_speedup']=serial['seconds']/cached['seconds']
        assert cached['bytes']<=512*1024*1024
        # Preserve the exact shared split boundary by keeping all heads, rotating
        # the chosen head first after clearing the slot. Its first answer is cold.
        results['six_head_cold_deltas']={}
        items=list(heads.items())
        for i,(name,_) in enumerate(items):
            rotated={**multi,'questions':dict(items[i:]+items[:i])}
            reference=cold('six-heads-cold-first-'+name,rotated,'context')
            left={**cached,'body':{'answers':{name:cached['body']['answers'][name]}}}
            right={**reference,'body':{'answers':{name:reference['body']['answers'][name]}}}
            results['six_head_cold_deltas'][name]=compare(left,right)
        # TTL explicitly sliding idle; wait past four seconds + maintenance tick.
        clear();call('ttl-prime',body(),'context');time.sleep(6);expired=call('ttl-expired',body(),'context');assert expired['cached']==0
        # Cached image pixels are never reused. Shape/pixels can change safely.
        for fixture in ['error.png','success.png']:
            file=ROOT/'tests/vision-fixtures'/fixture
            if not file.exists():continue
            image={'model':'autojev','state':'Describe only the build status.','images':['data:image/png;base64,'+base64.b64encode(file.read_bytes()).decode()], 'questions':{'status':{'type':'noul','instructions':'Does the screenshot show an error?'}}}
            for mode in ['context','options']:
                r=call('image-'+fixture+'-'+mode,image,mode);assert r['mode']=='none' and r['cached']==r['written']==0
        results['all_checks_passed']=True
    except BaseException as exc:results['failure']=type(exc).__name__+': '+str(exc);raise
    finally:
        if p is not None and p.poll() is None:
            p.terminate()
            try:p.wait(timeout=30)
            except subprocess.TimeoutExpired:p.kill();p.wait(timeout=15)
        if log:log.close()
        if stop_attempted:
            try:
                restored=subprocess.run([str(app),'start','--root',str(ROOT)],timeout=90,creationflags=HIDDEN)
                s=http(PUBLIC,'/local/status',timeout=10)['body'];assert restored.returncode==0 and s['service']==current['service'] and s['mode']==current['mode']
                results['restored_status']={k:s[k] for k in ['status','mode','activeRequests','waitingRequests']}
            except BaseException as exc:results['restore_error']=type(exc).__name__
        try:
            token.unlink(missing_ok=True);results['config_unchanged']=hashfile(ROOT/'config/server.json')==config_hash;results['installed_runtime_unchanged']=installed=={p.name:hashfile(p) for p in (ROOT/'runtime/windows-cuda').glob('*.dll')}
        except Exception as exc:results['cleanup_error']=type(exc).__name__
        save();print('RESTORED',results.get('restored_status'),flush=True)
        if results.get('restore_error') or results.get('cleanup_error') or not results.get('config_unchanged') or not results.get('installed_runtime_unchanged'):raise RuntimeError('Restoration/unchanged-file checks failed')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',default='cache-modes-live-'+time.strftime('%Y%m%d-%H%M%S'));parser.add_argument('--checkpoints',type=int);parser.add_argument('--cpu',action='store_true');parser.add_argument('--quick',action='store_true');run(parser.parse_args())
