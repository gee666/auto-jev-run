"""Usage: py examples/image_request.py screenshot.png"""
import os, base64, json, pathlib, sys, urllib.request
path = pathlib.Path(sys.argv[1])
mime = {'.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp'}[path.suffix.lower()]
body = {
    'model': 'jev-latest',
    'state': 'Inspect the attached screenshot.',
    'images': [f'data:{mime};base64,' + base64.b64encode(path.read_bytes()).decode()],
    'questions': {'failed': {'type': 'noul', 'instructions': 'Does this screenshot show a failed build?'}},
}
request = urllib.request.Request('http://localhost:8765/v1/systemone', data=json.dumps(body).encode(), headers={'Authorization': 'Bearer ' + os.environ['TYPESAFE_API_KEY'], 'Content-Type': 'application/json'})
with urllib.request.urlopen(request, timeout=1200) as response:
    print(json.dumps(json.load(response), indent=2))
