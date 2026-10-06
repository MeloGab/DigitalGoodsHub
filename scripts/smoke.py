"""End-to-end test: two real HTTP services; no external Python dependencies."""
import argparse, json, time, uuid, urllib.request, urllib.error, concurrent.futures
p=argparse.ArgumentParser();p.add_argument('--wait',type=int,default=30);p.add_argument('--api',default='http://localhost:5080');p.add_argument('--provider',default='http://localhost:5081');a=p.parse_args()
def req(path,body=None,key=None,base=None):
    headers={'Content-Type':'application/json'}
    if key: headers['Idempotency-Key']=key
    r=urllib.request.Request((base or a.api)+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
    try:
        with urllib.request.urlopen(r,timeout=10) as response:return response.status,json.load(response)
    except urllib.error.HTTPError as e:return e.code,json.load(e)
for base in [a.api,a.provider]:
    deadline=time.time()+a.wait
    while True:
        try:
            if req('/health',base=base)[0]==200:break
        except Exception:pass
        if time.time()>deadline:raise RuntimeError('Health timeout: '+base)
        time.sleep(1)
body={'productId':'recarga-vivo-30','phoneNumber':'11999999999','scenario':'Success'};key=str(uuid.uuid4())
with concurrent.futures.ThreadPoolExecutor(max_workers=15) as ex:
    result=list(ex.map(lambda _:req('/api/payments',body,key),range(15)))
assert sum(s==201 for s,_ in result)==1,result
assert all(s in (200,201) for s,_ in result),result
assert len({r['id'] for _,r in result})==1,result
assert req('/api/payments',{**body,'phoneNumber':'11888888888'},key)[0]==409
print('PASS: 15 concurrent requests = one payment; changed payload = 409')
for scenario,expected in [('Success','Completed'),('CrashAfterEffect','Completed'),('TimeoutAfterEffect','Completed'),('TransientFailure','Completed'),('PermanentFailure','Failed'),('Unavailable','PendingReconciliation')]:
    code,payment=req('/api/payments',{**body,'scenario':scenario},str(uuid.uuid4()));assert code==201
    deadline=time.time()+60
    while True:
        code,payment=req('/api/payments/'+payment['id'])
        if payment['status'] in ('Completed','Failed','PendingReconciliation'):break
        assert time.time()<deadline,payment
        time.sleep(.3)
    assert payment['status']==expected,payment
    if expected=='Completed':
        code,ops=req('/provider/operations',base=a.provider)
        assert code==200 and sum(x['id']==payment['id'] for x in ops)==1,ops
        assert payment['providerTransactionId']==next(x['transactionId'] for x in ops if x['id']==payment['id'])
    print('PASS:',scenario,'->',expected,'attempts=',payment['attempts'])
with concurrent.futures.ThreadPoolExecutor(max_workers=20) as ex:
    responses=list(ex.map(lambda _:req('/api/products/recarga-vivo-30'),range(50)))
assert all(code==200 for code,_ in responses)
print('PASS: 50 concurrent catalog reads')
print(json.dumps(req('/api/diagnostics')[1],indent=2))
