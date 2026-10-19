"""Deployment smoke tests. Run on the server; never prints API credentials."""
import base64
import concurrent.futures
import json
import pathlib
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
BASE = 'http://127.0.0.1:8765'

def http(path, body=None, key=None, headers=None):
    h = dict(headers or {})
    if key:
        h['Authorization'] = 'Bearer ' + key
    data = None if body is None else json.dumps(body).encode()
    if data is not None:
        h['Content-Type'] = 'application/json'
    req = urllib.request.Request(BASE + path, data=data, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=1000) as r:
            return r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)

def main():
    for path in ['/', '/usage', '/docs', '/health', '/v1/models']:
        code, data, _ = http(path)
        assert code == 200 and data, (path, code)
        print(path, code, flush=True)
    request = json.loads((ROOT / 'examples/request.json').read_text())
    assert http('/v1/systemone', request)[0] == 401
    assert http('/v1/systemone', request, 'invalid-deployment-test')[0] == 401
    key = json.loads(http('/local/dashboard-key')[1])['key']
    for mode in ['none', 'options', 'context']:
        started = time.monotonic()
        code, data, _ = http('/v1/systemone', request, key, {'X-AutoJev-Cache-Mode': mode})
        assert code == 200, (mode, code, data[:500])
        result = json.loads(data)
        assert 'answers' in result and result['usage']['input_tokens'] > 0, result
        print(mode, round(time.monotonic() - started, 2), result, flush=True)
    image_request = {'model': 'jev-latest', 'state': 'Inspect the attached image.',
        'images': ['data:image/png;base64,' + base64.b64encode((ROOT/'tests/vision-fixtures/red.png').read_bytes()).decode()],
        'questions': {'color': {'type': 'choice', 'instructions': 'What color is the rectangle?',
            'criteria': {'red': 'Red', 'blue': 'Blue'}}}}
    code, data, _ = http('/v1/systemone', image_request, key)
    assert code == 200, (code, data[:500])
    print('image', json.loads(data), flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(pool.map(lambda _: http('/v1/systemone', request, key)[0], range(3)))
    assert results == [200] * 3, results
    print('concurrent queue', results, flush=True)
    assert http('/local/usage')[0] == 200
    assert http('/local/dashboard-key', headers={'Origin': 'https://untrusted.invalid'})[0] == 403
    print('PASS: dashboard, auth, text, image, cache modes, queue, usage and origin protection', flush=True)

if __name__ == '__main__':
    main()
