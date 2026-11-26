# Faux serveurs GLPI (API v1) + Tactical RMM pour les tests d'AMI·OPS — by alex.b
import json, ssl, re, sys, io
from email.parser import BytesParser
from email import policy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
LOG=[]
COMPUTERS=[{"id":1,"name":"CLI09160699-01","entities_id":5,"serial":"SN1","last_inventory_update":"2026-11-26 09:00:00"},
           {"id":2,"name":"CLI09160699-03","entities_id":5,"serial":"SN3","last_inventory_update":"2026-10-01 09:00:00"},
           {"id":3,"name":"AUTRE-PC","entities_id":7,"serial":"SN9","last_inventory_update":"2026-11-26 09:00:00"}]
AGENTS=[{"itemtype":"Computer","items_id":1,"last_contact":"2026-11-26 10:00:00","tag":"CLI-09-16-0699"}]
SUPERV={}   # cid -> ligne d'état (marqueur « État PC Santé AB ») reçue par /front/inventory.php
TRMM=[{"agent_id":"abc-1","hostname":"CLI09160699-01","client_name":"CLI-09-16-0699 - Garage Test","site_name":"Siège","status":"online","last_seen":"2026-11-26T09:30:00Z","needs_reboot":True,"has_patches_pending":False,"checks":{"failing":2},"operating_system":"Windows 11 Pro"},
      {"agent_id":"abc-2","hostname":"CLI09160699-02","client_name":"CLI-09-16-0699 - Garage Test","site_name":"Siège","status":"offline","last_seen":"2026-11-01T09:30:00Z","needs_reboot":False,"has_patches_pending":True,"checks":{"failing":0},"operating_system":"Windows 10 Pro"},
      {"agent_id":"zzz-9","hostname":"PC-AUTRE","client_name":"CLI-01-01-0001 - Autre","site_name":"S","status":"online","last_seen":"2026-11-26T09:30:00Z"}]
class H(BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def send(self,code,obj,raw=False):
        b=(obj if raw else json.dumps(obj)).encode(); self.send_response(code); self.send_header("Content-Type","application/json"); self.send_header("Content-Length",str(len(b))); self.end_headers(); self.wfile.write(b)
    def body(self):
        n=int(self.headers.get("Content-Length",0)); return self.rfile.read(n) if n else b""
    def handle_any(self,method):
        u=urlparse(self.path); p=u.path; q=parse_qs(u.query); b=self.body() if method=="POST" else b""
        LOG.append({"m":method,"p":p,"q":u.query,"h":{k:v for k,v in self.headers.items() if k in("Authorization","Session-Token","App-Token","X-API-KEY","Content-Type")},"b":b[:4000].decode("utf-8","replace")})
        if p=="/__log": return self.send(200,LOG)
        if p=="/": return self.send(200,{"ok":1})
        if p=="/front/inventory.php" and method=="POST":
            try:
                inv=json.loads(b.decode("utf-8","replace")); ct=inv.get("content",{})
                name=ct.get("hardware",{}).get("name",""); tag=inv.get("tag","")
                mark=next((s for s in ct.get("softwares",[]) if s.get("name")=="État PC Santé AB"),None)
                cid={"CLI09160699-07":7}.get(name,7)
                if not any(c["id"]==cid for c in COMPUTERS):
                    COMPUTERS.append({"id":cid,"name":name,"entities_id":5,"serial":ct.get("bios",{}).get("ssn",""),"last_inventory_update":"2026-11-26 21:30:00"})
                if mark: SUPERV[cid]=mark.get("version","")
                LOG[-1]["inventory"]={"tag":tag,"name":name,"deviceid":inv.get("deviceid",""),"has_marker":mark is not None}
                return self.send(200,{"status":"ok"})
            except Exception as ex:
                return self.send(400,{"status":"error","message":str(ex)})
        if p.startswith("/range/"):
            body="0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n1E4C9B93F3F0682250B6CF8331B7EE68FD8:9999999\r\nFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF:0"
            b=body.encode(); self.send_response(200); self.send_header("Content-Type","text/plain"); self.send_header("Content-Length",str(len(b))); self.end_headers(); self.wfile.write(b); return
        if p.startswith("/api/v3/files/"):
            if self.headers.get("x-apikey")!="vtk": return self.send(401,{"error":"key"})
            sha=p.rsplit("/",1)[1]
            if sha=="a"*64: return self.send(404,{"error":{"code":"NotFoundError"}})
            return self.send(200,{"data":{"attributes":{"meaningful_name":"evil.exe","last_analysis_stats":{"malicious":41,"suspicious":2,"undetected":20,"harmless":0}}}})
        if p.startswith("/apirest.php"):
            p=p[len("/apirest.php"):]
            if p=="/initSession": return self.send(200,{"session_token":"S1"}) if self.headers.get("Authorization")=="user_token tok" else self.send(401,["ERROR_GLPI_LOGIN","bad"])
            if self.headers.get("Session-Token")!="S1": return self.send(401,["ERROR_SESSION_TOKEN_INVALID",""])
            if p=="/killSession": return self.send(200,{})
            if p=="/Computer/" or p=="/Computer":
                n=q.get("searchText[name]",[None])[0]
                return self.send(200,[c for c in COMPUTERS if n is None or n.lower() in c["name"].lower()])
            mc=re.match(r"^/Computer/(\d+)$",p)
            if mc:
                cid=int(mc.group(1)); c=next((x for x in COMPUTERS if x["id"]==cid),None)
                if c is None: return self.send(404,["ERROR_ITEM_NOT_FOUND",""])
                out=dict(c)
                if "with_softwares" in q:
                    sw=SUPERV.get(cid)
                    out["_softwares"]=[{"softwares_name":"État PC Santé AB","name":"État PC Santé AB","version":sw}] if sw else []
                return self.send(200,out)
            if p=="/Agent/": return self.send(200,AGENTS)
            m=re.match(r"^/Computer/(\d+)/(\w+)/$",p)
            if m:
                cid,ty=int(m.group(1)),m.group(2)
                if ty=="ItemAntivirus": return self.send(400,["ERROR_ITEM_NOT_FOUND",""])
                if cid==1 and ty=="Item_Disk": return self.send(200,[{"mountpoint":"C:","totalsize":100000,"freesize":8000,"encryption_status":1}])
                if cid==1 and ty=="ComputerAntivirus": return self.send(200,[{"name":"Kaspersky Endpoint Security","is_active":1,"is_uptodate":0}])
                if cid==1 and ty=="Item_OperatingSystem": return self.send(200,[{"operatingsystems_id":"Windows 11","operatingsystemversions_id":"26H2"}])
                if cid==2 and ty=="Item_OperatingSystem": return self.send(200,[{"operatingsystems_id":"Windows 10","operatingsystemversions_id":"22H2"}])
                return self.send(200,[])
            if p=="/Entity/" and method=="GET": return self.send(200,[{"id":5,"name":"CLI-09-16-0699 - Garage Test"}])
            if p.startswith("/search/Ticket/"): return self.send(200,{"totalcount":1,"data":[{"2":42,"1":"NAS « DS » : alerte","12":2,"15":"2026-11-20 10:00:00"}]})
            if p=="/Document/" and method=="POST":
                ct=self.headers.get("Content-Type","")
                msg=BytesParser(policy=policy.default).parsebytes(b"Content-Type: "+ct.encode()+b"\r\n\r\n"+b)
                parts={pt.get_param("name",header="content-disposition"):pt for pt in msg.iter_parts()}
                man=json.loads(parts["uploadManifest"].get_content()); f=parts["filename[0]"]
                LOG[-1]["upload"]={"manifest":man,"filename":f.get_filename(),"size":len(f.get_payload(decode=True))}
                return self.send(201,{"id":77,"message":"Document uploaded"})
            if method=="POST": return self.send(201,{"id":{"/Ticket/":42,"/Entity/":9}.get(p,100),"message":""})
            return self.send(404,["ERROR_RESOURCE_NOT_FOUND",""])
        if p.startswith("/trmm"):
            if self.headers.get("X-API-KEY")!="k1": return self.send(401,{"detail":"bad key"})
            p=p[5:]
            if p=="/agents/": return self.send(200,TRMM)
            m=re.match(r"^/agents/([\w\-]+)/cmd/$",p)
            if m: return self.send(200,json.dumps("Bonjour\r\nJSON|"+json.dumps([{"Modele":"Dell Latitude","RAM":16}])+"\r\n"),raw=True)
        return self.send(404,{"err":p})
    def do_GET(self): self.handle_any("GET")
    def do_POST(self): self.handle_any("POST")
s=ThreadingHTTPServer(("127.0.0.1",int(sys.argv[1])),H)
ctx=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain("cert.pem","key.pem"); s.socket=ctx.wrap_socket(s.socket,server_side=True)
s.serve_forever()
