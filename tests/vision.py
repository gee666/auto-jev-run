"""Image-conditioned decision checks, including CPU and CUDA, without extra packages."""
import base64, concurrent.futures as cf, json, pathlib, subprocess, time, sys
import capacity as c
ROOT=c.ROOT; FIXTURES=ROOT/'tests/vision-fixtures'
OUT=ROOT/'reports/vision-validation.json'
data={'note':'Synthetic color, shape and large-text checks; not a natural-image or OCR accuracy benchmark.','cuda':[],'cpu':[],'invalid':[]}
def save():OUT.write_text(json.dumps(data,indent=2))
def image(name):
    mime='image/jpeg' if name.endswith('.jpg') else 'image/webp' if name.endswith('.webp') else 'image/png'
    return f'data:{mime};base64,'+base64.b64encode((FIXTURES/name).read_bytes()).decode()
def body(names,kind='color'):
    instructions,criteria={
      'color':('What color is the large rectangle?',{'red':'Red','blue':'Blue','green':'Green'}),
      'shape':('What shape is shown?',{'circle':'A circle','square':'A square','triangle':'A triangle'}),
      'build':('What result does the displayed build status show?',{'success':'The build succeeded and all tests passed.','error':'The build failed and tests failed.'}),
      'second':('What color is the rectangle in the SECOND image?',{'red':'Red','blue':'Blue'}),
      'fourth':('What color is the rectangle in the FOURTH image?',{'red':'Red','blue':'Blue'})}[kind]
    return {'model':'jev-latest','state':'Inspect the attached images.','images':[image(n) for n in names],'questions':{'answer':{'type':'choice','instructions':instructions,'criteria':criteria}}}
def evaluate(names,kind,expected,base=c.BASE):
    result=c.evaluate(body(names,kind),expected,base)
    result.update(images=names,kind=kind)
    print(kind,names,result['status'],result['correct'],f"{result['seconds']:.2f}s",flush=True)
    return result

tests=[([f'{x}.png'],kind,x) for kind,xs in [('color',['red','blue']),('shape',['circle','square']),('build',['success','error'])] for x in xs]
tests += [(['red.jpg'],'color','red'),(['red.webp'],'color','red'),(['red.png','blue.png'],'second','blue'),(['blue.png','red.png'],'second','red'),(['red.png','blue.png','red.png','blue.png'],'fourth','blue')]
if '--cpu-only' in sys.argv:
    data=json.loads(OUT.read_text());data['cpu']=[]
else:
    data['first_cuda_request']=evaluate(*tests[0]);save()
    pid=c.status()['backendPid']
    with c.Sample(pid) as monitor:
        for test in tests:
            data['cuda'].append(evaluate(*test));save()
    data['cuda_resources']=monitor.summary
    data['text_regression']=[]
    for i in range(3):data['text_regression'].append(c.evaluate(*c.question_case(c.CASES[i],16,42)))
    with cf.ThreadPoolExecutor(max_workers=4) as pool:
        data['concurrent_images']=list(pool.map(lambda t:evaluate(*t),tests[:4]))
    for label,images,expected in [('too_many',[image('red.png')]*5,422),('url',['https://example.com/image.png'],422),('invalid_base64',['data:image/png;base64,!!!'],422),('invalid_image',['data:image/png;base64,'+base64.b64encode(b'not an image').decode()],400)]:
        request=body(['red.png']);request['images']=images
        code,response,seconds=c.http('/v1/systemone',request)
        data['invalid'].append({'case':label,'status':code,'expected':expected,'passed':code==expected,'response':response})
        print('INVALID',label,code,flush=True)
    save()

# Unload the main model before a separate CPU-only check. The normal API stays up.
import urllib.request
token=(ROOT/'state/control.token').read_text()
req=urllib.request.Request(c.BASE+'/local/unload',data=b'',headers={'X-AutoJev-Control':token})
with urllib.request.urlopen(req) as response:assert response.status==200
root=ROOT/'.build/vision-cpu'
for sub in ['config','state','logs']:(root/sub).mkdir(parents=True,exist_ok=True)
if not (root/'runtime').exists():
    subprocess.run(['powershell','-NoProfile','-Command', "New-Item -ItemType Junction -Path '"+str(root/'runtime').replace("'","''")+"' -Target '"+str(ROOT/'runtime').replace("'","''")+"' | Out-Null"],check=True)
cfg=json.loads((ROOT/'config/server.json').read_text())
cfg.update(port=18875,backendPort=18876,mode='cpu',modelPath=str(ROOT/cfg['modelPath']),visionProjectorPath=str(ROOT/cfg['visionProjectorPath']))
(root/'config/server.json').write_text(json.dumps(cfg))
p=subprocess.Popen([str(ROOT/'app/AutoJev.exe'),'serve','--root',str(root)],creationflags=0x08000000)
base='http://127.0.0.1:18875'
try:
    for _ in range(60):
        if c.http('/local/status',base=base)[0]==200:break
        time.sleep(.25)
    data['cpu'].append(evaluate(*tests[0],base=base));save()
    cpu_pid=c.http('/local/status',base=base)[1]['backendPid']
    with c.Sample(cpu_pid) as monitor:data['cpu'].append(evaluate(*tests[1],base=base))
    data['cpu_resources']=monitor.summary;save()
finally:
    try:
        req=urllib.request.Request(base+'/local/stop',data=b'',headers={'X-AutoJev-Control':(root/'state/control.token').read_text()})
        urllib.request.urlopen(req,timeout=5).close();p.wait(timeout=15)
    except Exception:
        p.kill();p.wait()
assert all(r['status']==200 for r in data['cuda']+data['cpu']+data['concurrent_images'])
assert all(r['passed'] for r in data['invalid'])
print('Vision checks completed; CPU test server stopped. Main API remains available.',flush=True)
