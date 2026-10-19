"""Temporarily stop the tray app, benchmark isolated backends, then restore the app."""
import os, pathlib, secrets, subprocess, time
import capacity as c

def run():
    root=c.ROOT
    runtime=root/'runtime/windows-cuda'
    keyfile=root/'state/capacity-test.token'
    key=secrets.token_hex(32)
    keyfile.write_text(key)
    app=root/'app/AutoJev.exe'
    subprocess.run([str(app),'stop','--root',str(root)],creationflags=0x08000000,timeout=30)
    time.sleep(3)
    try:
        for slots in [1,2,4]:
            logpath=c.OUT/f'backend-slots-{slots}.log'
            env=os.environ.copy()
            env['PATH']=str(runtime)+';'+os.environ['SystemRoot']+'\\System32;'+os.environ['SystemRoot']
            env['LLAMA_CACHE']=str(root/'state/cache')
            env['TEMP']=env['TMP']=str(root/'state')
            args=[str(runtime/'llama-server.exe'),'-m',str(root/'models/autojev-27b/autojev-Q4_K_M.gguf'),'--system-one','--host','127.0.0.1','--port','18766','--api-key-file',str(keyfile),'-c',str(12288*slots),'-np',str(slots),'-t','8','-tb','8','-b','2048','-ub','512','--fit','on','--fit-target','1536']
            with logpath.open('w',encoding='utf-8') as log:
                started=time.perf_counter()
                p=subprocess.Popen(args,cwd=runtime,env=env,stdout=log,stderr=log,creationflags=0x08000000)
                try:
                    while time.perf_counter()-started<180:
                        if p.poll() is not None:break
                        if c.http('/health',base='http://127.0.0.1:18766',key=key)[0]==200:break
                        time.sleep(.5)
                    ready=c.http('/health',base='http://127.0.0.1:18766',key=key)[0]==200
                    c.save(f'launch-slots-{slots}.json',{'slots':slots,'context_total':slots*12288,'context_per_slot':12288,'ready':ready,'startup_seconds':time.perf_counter()-started,'exit_code':p.poll()})
                    print(f'BACKEND slots={slots} ready={ready} startup={time.perf_counter()-started:.2f}s',flush=True)
                    if ready:c.concurrency('http://127.0.0.1:18766',key,p.pid,slots,False)
                finally:
                    if p.poll() is None:p.terminate()
                    p.wait(timeout=30)
    finally:
        # Only our temporary token is removed. Logs and measurements are retained.
        keyfile.unlink(missing_ok=True)
        subprocess.run([str(app),'start','--root',str(root)],creationflags=0x08000000,timeout=30)
        print('RESTORED normal AutoJev app',flush=True)

if __name__=='__main__':run()
