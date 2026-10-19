"""Public API cache override, invalidation, numerical equivalence, image fallback and concurrency."""
import base64, concurrent.futures, json, pathlib, statistics, subprocess, time, urllib.request, urllib.error
import capacity as c
import prefix_cache as bench
ROOT=c.ROOT;BASE='http://localhost:8765';OUT=ROOT/'reports/prefix-cache/integration.json'
def call(body, cache='true'):
    headers={'Content-Type':'application/json'}
    if cache is not None:headers['X-AutoJev-Prefix-Cache']=cache
    request=urllib.request.Request(BASE+'/v1/systemone',data=json.dumps(body).encode(),headers=headers)
    t=time.perf_counter()
    try:
        with urllib.request.urlopen(request,timeout=1200) as r:return {'status':r.status,'seconds':time.perf_counter()-t,'body':json.load(r),'cached':int(r.headers.get('X-AutoJev-Cached-Tokens',0)),'bytes':int(r.headers.get('X-AutoJev-Checkpoint-Bytes',0))}
    except urllib.error.HTTPError as e:return {'status':e.code,'body':e.read().decode()}
def probs(r):return r['body']['answers']['answer']['probabilities']
def run():
    cases=bench.cases();result={'equivalence':[]}
    def save():OUT.write_text(json.dumps(result,indent=2))
    result['default']=call(cases[0][1],None);assert result['default']['cached']==0
    pid=c.status()['backendPid']
    with c.Sample(pid) as monitor:
        for index in [0,1,18,24,26,46,52]:
            _,body,expected=cases[index]
            clear=json.loads(json.dumps(body));clear['questions']['answer']['instructions']='Separate temporary taxonomy: select the best option.'
            call(clear)
            cold=call(body);warm=call(body)
            assert cold['status']==warm['status']==200 and cold['cached']==0 and warm['cached']>0
            difference=max(abs(probs(cold)[k]-v) for k,v in probs(warm).items())
            assert difference < 1e-6, 'Cached state differs from an equivalent cold prefix'
            row={'id':cases[index][0],'cold':cold,'warm':warm,'max_probability_difference':difference}
            result['equivalence'].append(row);save()
            print('equivalence',row['id'],difference,flush=True)
    result['resources']=monitor.summary
    body=cases[0][1];call(body)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:result['concurrent']=list(pool.map(lambda i:call(cases[i][1]),range(4)))
    assert all(r['status']==200 and r['cached']>0 for r in result['concurrent'])
    result['invalid_header']=call(body,'banana');assert result['invalid_header']['status']==422
    result['forced_original']=call(body,'false');assert result['forced_original']['cached']==0
    result['after_original']=call(body);assert result['after_original']['cached']==0
    altered=json.loads(json.dumps(body));altered['questions']['answer']['criteria']['politics']='Reports about elections and parliamentary vote results.'
    result['changed_description']=call(altered);assert result['changed_description']['cached']==0
    result['changed_description_repeat']=call(altered);assert result['changed_description_repeat']['cached']>0
    structured=json.loads(json.dumps(body));structured['state']={'article':body['state'],'source':'synthetic'}
    result['structured']=call(structured);assert result['structured']['body']['answers']['answer']['choice']=='politics'
    image={'model':'jev-latest','state':'','images':['data:image/png;base64,'+base64.b64encode((ROOT/'tests/vision-fixtures/error.png').read_bytes()).decode()],'questions':{'failed':{'type':'noul','instructions':'Does this image show a failed build?'}}}
    result['image_fallback']=call(image);assert result['image_fallback']['cached']==0 and result['image_fallback']['body']['answers']['failed']['noul']>.5
    save();print('Public integration checks passed',flush=True)
if __name__=='__main__':run()
