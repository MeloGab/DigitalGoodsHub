"""Kills the actual API after a durable external effect, then verifies recovery.
Local mode only. Requires a Release build. Starts both services on isolated ports.
"""
import argparse, json, os, pathlib, subprocess, tempfile, time, urllib.request, uuid
p=argparse.ArgumentParser();p.add_argument('--dotnet',default='dotnet');a=p.parse_args()
root=pathlib.Path(__file__).resolve().parent.parent
api_url='http://127.0.0.1:5180';provider_url='http://127.0.0.1:5181'
def req(base,path,body=None,key=None):
    headers={'Content-Type':'application/json'}
    if key:headers['Idempotency-Key']=key
    r=urllib.request.Request(base+path,data=json.dumps(body).encode() if body else None,headers=headers)
    with urllib.request.urlopen(r,timeout=3) as response:return json.load(response)
def health(base):
    deadline=time.time()+20
    while time.time()<deadline:
        try:req(base,'/health');return
        except Exception:time.sleep(.2)
    raise RuntimeError('Health timeout: '+base)
with tempfile.TemporaryDirectory(prefix='digitalgoods-restart-') as tmp:
    log=open(pathlib.Path(tmp)/'services.log','w');processes=[]
    def start(project,port,db):
        env={**os.environ,'ConnectionStrings__Sqlite':f'Data Source={db};Default Timeout=30','ProviderUrl':provider_url+'/'}
        child=subprocess.Popen([a.dotnet,str(root/'src'/project/'bin/Release/net10.0'/f'{project}.dll'),'--urls',f'http://127.0.0.1:{port}','--contentRoot',str(root/'src'/project)],env=env,stdout=log,stderr=log,cwd=root)
        processes.append(child);return child
    try:
        provider=start('DigitalGoodsHub.ProviderSimulator',5181,pathlib.Path(tmp)/'provider.db');health(provider_url)
        api=start('DigitalGoodsHub.Api',5180,pathlib.Path(tmp)/'api.db');health(api_url)
        payment=req(api_url,'/api/payments',{'productId':'recarga-vivo-30','phoneNumber':'11999999999','scenario':'TimeoutAfterEffect'},str(uuid.uuid4()))
        deadline=time.time()+10
        while not any(x['id']==payment['id'] for x in req(provider_url,'/provider/operations')):
            assert time.time()<deadline,'No external effect observed';time.sleep(.02)
        api.kill();api.wait(timeout=10)
        print('PASS: API killed after durable external effect and before result commit',flush=True)
        api=start('DigitalGoodsHub.Api',5180,pathlib.Path(tmp)/'api.db');health(api_url)
        deadline=time.time()+75
        while True:
            state=req(api_url,'/api/payments/'+payment['id'])
            if state['status']=='Completed':break
            assert time.time()<deadline,state;time.sleep(.3)
        ops=req(provider_url,'/provider/operations')
        assert sum(x['id']==payment['id'] for x in ops)==1,ops
        assert state['providerTransactionId']==next(x['transactionId'] for x in ops if x['id']==payment['id'])
        print('PASS: restart recovered Completed; exactly one external operation; Inbox committed',flush=True)
        assert req(api_url,'/api/diagnostics')['inbox']==1
    finally:
        for child in reversed(processes):
            if child.poll() is None:
                child.terminate()
                try:child.wait(timeout=5)
                except subprocess.TimeoutExpired:child.kill();child.wait()
        log.close()
