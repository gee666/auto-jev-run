"""Real-model metering checks; all inference is attributed to Dashboard & testing."""
import json, pathlib, time, urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
BASE='http://127.0.0.1:8765'
def get(path):
    with urllib.request.urlopen(BASE+path) as r:return json.load(r)
key=get('/local/dashboard-key')['key']
rows=[]
for i,cache in enumerate([True,True,False]):
    body={'state':['A new Python compiler release improves execution speed.','A software company released a new database engine.','Heavy rain is forecast tomorrow.'][i], 'questions':{'topic':{'type':'choice','instructions':'Classify this news article by its primary subject. Usage metering validation 2026-09-27.','criteria':{'technology':'Software, computers and computing research.','weather':'Weather forecasts and storms.','other':'Any other subject.'}}}}
    req=urllib.request.Request(BASE+'/v1/systemone',data=json.dumps(body).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+key,'X-AutoJev-Prefix-Cache':str(cache).lower()})
    started=time.perf_counter()
    with urllib.request.urlopen(req,timeout=1200) as r:
        result=json.load(r);headers=dict(r.headers)
    row={'cache':cache,'seconds':time.perf_counter()-started,'id':headers['X-TypeSafe-Request-Id'],'usage':result['usage'],'cacheRead':int(headers['X-AutoJev-Cached-Tokens']),'cacheWrite':int(headers['X-AutoJev-Cache-Write-Tokens'])}
    rows.append(row);print(json.dumps(row),flush=True)
time.sleep(.2)
history=get('/local/usage?keyId=dashboard')['requests']
for row in rows:
    stored=next(r for r in history if r['id']==row['id'])
    assert stored['keyId']=='dashboard' and stored['status']==200
    assert stored['inputTokens']==row['usage']['input_tokens'] and stored['cacheReadTokens']==row['cacheRead'] and stored['cacheWriteTokens']==row['cacheWrite']
    row['stored']=stored
assert rows[0]['cacheWrite']>0 and rows[0]['cacheRead']==0
assert rows[1]['cacheRead']>0 and rows[1]['cacheWrite']==0
assert rows[2]['cacheRead']==rows[2]['cacheWrite']==0
(ROOT/'reports/usage-live-validation.json').write_text(json.dumps(rows,indent=2))
print('Live token and cache metering passed.')

