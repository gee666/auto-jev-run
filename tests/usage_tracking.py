"""Isolated SQLite/auth integration checks; never writes test app keys into live history."""
import concurrent.futures, json, pathlib, sqlite3, subprocess, time, urllib.request, urllib.error

ROOT = pathlib.Path(__file__).resolve().parents[1]
TEMP = ROOT / '.build' / ('usage-checks-' + str(time.time_ns()))
BASE = 'http://127.0.0.1:18875'
BODY = {'state': 'private test content', 'questions': {'x': {'type': 'score', 'criteria': ['Only level']}}}
checks = []

def call(path, body=None, headers=None, method=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, headers={'Content-Type': 'application/json', **(headers or {})}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r: return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        try: return e.code, json.loads(text)
        except ValueError: return e.code, text

def check(name, condition):
    assert condition, name
    checks.append(name)
    print('PASS', name, flush=True)

def start():
    p = subprocess.Popen([str(ROOT/'app/AutoJev.exe'), 'serve', '--root', str(TEMP)], creationflags=0x08000000, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(200):
        try:
            if call('/local/status')[1]['root'] == str(TEMP): return p
        except OSError: pass
        time.sleep(.05)
    raise RuntimeError('Isolated server did not start')

def stop(p):
    call('/local/stop', {}, {'X-AutoJev-Control': (TEMP/'state/control.token').read_text()})
    p.wait(timeout=15)

(TEMP/'config').mkdir(parents=True)
cfg = json.loads((ROOT/'config/server.json').read_text())
cfg.update(port=18875, backendPort=18876)
(TEMP/'config/server.json').write_text(json.dumps(cfg))
(TEMP/'app').mkdir()
for name in ['index.html','usage.html']:
    (TEMP/'app'/name).write_bytes((ROOT/'app'/name).read_bytes())
p = start()
try:
    secret = call('/local/dashboard-key')[1]['key']
    key = call('/local/keys', {'name': 'News app'})[1]
    auth = {'Authorization': 'Bearer '+key['key']}
    check('Missing key rejected', call('/v1/systemone', BODY)[0] == 401)
    check('Missing-key group absent',not any(k['id']=='without-a-key' for k in call('/local/usage')[1]['keys']))
    check('Missing-key request not tracked',call('/local/usage')[1]['total']==0)
    check('Dashboard key works', call('/v1/systemone', BODY, {'Authorization': 'Bearer '+secret})[0] == 200)
    check('Named key works', call('/v1/systemone', BODY, auth)[0] == 200)
    check('Alternative key header works', call('/v1/systemone', BODY, {'X-API-Key':key['key']})[0] == 200)
    check('Unknown key rejected', call('/v1/systemone', BODY, {'Authorization':'Bearer bad'})[0] == 401)
    check('Route variants enforce keys', call('/V1/SystemOne/', BODY, {'Authorization':'Bearer bad'})[0] == 401)
    check('Conflicting headers rejected', call('/v1/systemone', BODY, {**auth,'X-API-Key':'bad'})[0] == 401)
    check('Case-insensitive duplicate name rejected', call('/local/keys', {'name':'NEWS APP'})[0] == 409)
    check('Reserved name rejected', call('/local/keys', {'name':'Dashboard & testing'})[0] == 422)
    check('Cross-origin key creation rejected', call('/local/keys', {'name':'Bad'}, {'Origin':'https://example.com'})[0] == 403)
    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
        result = list(pool.map(lambda _:call('/v1/systemone', BODY, auth)[0],range(60)))
    check('60 concurrent calls tracked', all(s == 200 for s in result))
    time.sleep(.1)
    usage = call('/local/usage?keyId='+key['id'])[1]
    row = next(k for k in usage['keys'] if k['id'] == key['id'])
    check('Exact app totals and pagination', row['requests']==62 and usage['total']==62 and len(usage['requests'])==50 and len(call('/local/usage?keyId='+key['id']+'&offset=50')[1]['requests'])==12)
    check('Deterministic metrics zero; timing recorded', all(r['inputTokens']==r['outputTokens']==r['cacheReadTokens']==r['cacheWriteTokens']==0 and r['totalMs']>=r['queueMs']>=0 for r in usage['requests']))
    check('Rename preserves key', call('/local/keys/'+key['id'], {'name':'Renamed app'}, method='PATCH')[0]==200 and call('/v1/systemone',BODY,auth)[0]==200)
    stop(p); p=None
    with sqlite3.connect(TEMP/'data/usage.sqlite') as db:
        dump='\n'.join(db.iterdump())
        check('No raw secrets or request content persisted', all(s not in dump for s in [key['key'],secret,BODY['state']]))
        db.execute("INSERT INTO api_keys(id,name,created_utc,system) VALUES('without-a-key','without-a-key','2026-09-27',1)")
        db.execute("INSERT INTO requests(id,key_id,started_utc) VALUES('legacy-anonymous','without-a-key','2026-09-27')")
        db.execute("INSERT INTO requests(id,key_id,started_utc) VALUES('crash-test','dashboard','2026-09-27T00:00:00+00:00')")
    p=start()
    check('Legacy anonymous history removed',call('/local/usage?keyId=without-a-key')[1]['total']==0 and not any(k['id']=='without-a-key' for k in call('/local/usage')[1]['keys']))
    new_secret=call('/local/dashboard-key')[1]['key']
    check('Restart preserves app credential',call('/v1/systemone',BODY,auth)[0]==200)
    check('Dashboard secret rotates',new_secret!=secret and call('/v1/systemone',BODY,{'Authorization':'Bearer '+new_secret})[0]==200 and call('/v1/systemone',BODY,{'Authorization':'Bearer '+secret})[0]==401)
    check('Revoke rejects key',call('/local/keys/'+key['id']+'/revoke',{})[0]==200 and call('/v1/systemone',BODY,auth)[0]==401)
    time.sleep(.1)
    usage=call('/local/usage')[1]
    row=next(k for k in usage['keys'] if k['id']==key['id'])
    check('Revoked request attributed to original app',row['requests']==65 and row['failures']==1)
    check('Unfinished request marked interrupted',any(r['id']=='crash-test' and r['outcome']=='interrupted' for r in call('/local/usage?keyId=dashboard')[1]['requests']))
    check('Tracking never loads model',call('/local/status')[1]['status']=='unloaded')
    check('Built-in key cannot be deleted',call('/local/keys/dashboard',{},method='DELETE')[0]==404)
    check('Delete revoked key',call('/local/keys/'+key['id'],{},method='DELETE')[0]==200)
    remaining=call('/local/usage?keyId='+key['id'])[1]
    check('Deleted key and history removed',remaining['total']==0 and not any(k['id']==key['id'] for k in remaining['keys']))
    check('Deleted credential rejected',call('/v1/systemone',BODY,auth)[0]==401)
    fresh=call('/local/keys',{'name':'Renamed app'})[1]
    check('Deleted name reusable', 'key' in fresh)
    import http.client
    held=http.client.HTTPConnection('127.0.0.1',18875,timeout=10)
    held.putrequest('POST','/v1/systemone');held.putheader('Content-Type','application/json')
    held.putheader('Authorization','Bearer '+fresh['key']);held.putheader('Content-Length','100');held.endheaders()
    try:
        for _ in range(100):
            if call('/local/status')[1]['activeRequests']:break
            time.sleep(.02)
        check('In-flight key deletion blocked',call('/local/keys/'+fresh['id'],{},method='DELETE')[0]==409)
    finally:held.close()
    for _ in range(100):
        if not call('/local/status')[1]['activeRequests']:break
        time.sleep(.02)
    check('Delete active key after request ends',call('/local/keys/'+fresh['id'],{},method='DELETE')[0]==200)
    check('Deleted active credential rejected',call('/v1/systemone',BODY,{'X-API-Key':fresh['key']})[0]==401)
    stop(p);p=None;p=start()
    check('Deletion survives restart',not any(k['id'] in [key['id'],fresh['id']] for k in call('/local/usage')[1]['keys']))
finally:
    if p: stop(p)
(ROOT/'reports/sidebar-validation.json').write_text(json.dumps({'passed':checks,'count':len(checks)},indent=2))
print('All checks passed.')
