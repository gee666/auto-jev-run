"""Run against an already-started local server. Requires typesafe-sdk==0.7.1."""
import asyncio
import json
import math
import pathlib
import time
import urllib.error
import urllib.request
from typesafe_sdk import TypeSafeClient, AsyncTypeSafeClient, Noul, Choice, Score, RetryPolicy

BASE = 'http://localhost:8765'
ROOT = pathlib.Path(__file__).resolve().parents[1]
checks = []

def raw(path, body=None, headers=None):
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    headers = dict(headers or {'Content-Type': 'application/json'})
    if path == '/v1/systemone': headers['Authorization'] = 'Bearer ' + dashboard_key
    req = urllib.request.Request(BASE + path, data=data, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=1200) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode()

def record(name, **details):
    checks.append({'check': name, 'passed': True, **details})
    print('PASS', name, flush=True)

status, details = raw('/health')
assert status == 200 and details['status'] in ('ready', 'unloaded', 'loading')
record('health', mode=details['mode'])
with urllib.request.urlopen(BASE+'/local/dashboard-key') as response:
    dashboard_key=json.load(response)['key']

with TypeSafeClient(base_url=BASE, api_key=dashboard_key, timeout=1200, retry=RetryPolicy(max_retries=0)) as client:
    models = client.models.list()
    assert 'jev-latest' in str(models) and 'autojev' in str(models)
    record('official SDK model discovery')
    start = time.perf_counter()
    result = client.system_one(
        state={'message': 'My invoice was charged twice. Sign-in works normally.'},
        questions={
            'billing': Noul(instructions='Is the complaint about billing?'),
            'team': Choice(instructions='Which team should handle this?', criteria={'billing': 'Charges, invoices, refunds', 'technical': 'Login and software bugs'}),
            'urgency': Score(instructions='How urgent is this?', criteria=['Can wait', 'Needs attention soon', 'Needs immediate attention']),
        },
    )
    assert result.model == 'autojev-27b-q4_k_m'
    assert 0 <= result.nouls['billing'].noul <= 1
    assert result.choices['team'].choice in {'billing', 'technical'}
    assert abs(sum(result.choices['team'].probabilities.values()) - 1) < 1e-4
    assert 0 <= result.scores['urgency'].score <= 2
    record('official SDK noul, choice, score and structured state', seconds=time.perf_counter()-start, result=result.model_dump(mode='json'))
    result = client.system_one(state=['An invoice was charged twice.'], questions={
        'one': Score(instructions='Rate against the only level.', criteria=[{'label': 'Only level'}]),
        'yes': Noul(instructions={'task': 'Is this about a duplicate charge?'}, criteria={'true': 'A duplicate charge is mentioned', 'false': 'No duplicate charge is mentioned'})}, model='jev-9.99.0')
    assert result.scores['one'].score == 0
    assert result.scores['one'].probabilities[0] == 1
    record('arbitrary Jev version alias, list state, structured instructions, one-level score')
    result = client.system_one(state='Context', questions={'one': Score(criteria=['Only level'])})
    assert result.usage.input_tokens == 0 and result.scores['one'].score == 0
    record('all-deterministic score request')

async def concurrency():
    async with AsyncTypeSafeClient(base_url=BASE, api_key=dashboard_key, timeout=1200, retry=RetryPolicy(max_retries=0)) as client:
        answers = await asyncio.gather(*(client.system_one(state=f'Invoice {i} was charged twice.', questions={'billing': Noul(instructions='Is this about billing?')}) for i in range(2)))
        assert all(0 <= a.nouls['billing'].noul <= 1 for a in answers)
asyncio.run(concurrency())
record('official async SDK with concurrent requests')
for name, body, expected in [
    ('malformed JSON', b'{broken', 400),
    ('empty questions', {'state':'x','questions':{}}, 422),
    ('invalid state', {'state':12,'questions':{'x':{'type':'noul'}}}, 422),
    ('unknown model', {'model':'not-a-model','state':'x','questions':{'x':{'type':'noul'}}}, 404),
    ('invalid image URI rejected', {'state':'x','questions':{'x':{'type':'noul'}},'images':['not-an-image']}, 422),
]:
    code, response = raw('/v1/systemone', body)
    assert code == expected, (name, code, response)
    record(name, status=code)
code, _ = raw('/local/stop', {})
assert code == 403
record('stop endpoint requires local control token')
code, _ = raw('/v1/systemone', {'state':'x','questions':{'x':{'type':'noul'}}}, {'Content-Type':'application/json','Origin':'https://unrelated.example'})
assert code == 403
record('unrelated browser origin rejected')
(ROOT/'reports'/'sdk-compatibility.json').write_text(json.dumps({'base_url':BASE,'sdk':'typesafe-sdk 0.7.1','checks':checks},indent=2),encoding='utf-8')
print('COMPLETE', len(checks), 'checks passed', flush=True)
