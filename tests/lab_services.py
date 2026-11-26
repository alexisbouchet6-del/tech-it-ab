# Services simulés pour tester les scans Nmap de Tech IT AB (127.0.0.2 et 127.0.0.3)
import socket, threading, http.server, sys
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer
def banner(host, port, text):
    s=socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind((host,port)); s.listen(5)
    while True:
        c,_=s.accept()
        try: c.sendall(text); c.close()
        except Exception: pass
class T(http.server.BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def do_GET(self):
        b=b"<html><head><title>Synology DiskStation - Connexion</title></head><body>login</body></html>"
        self.send_response(200); self.send_header("Content-Type","text/html"); self.send_header("Content-Length",str(len(b))); self.end_headers(); self.wfile.write(b)
threading.Thread(target=banner,args=("127.0.0.2",23,b"\xff\xfd\x18\xff\xfd\x20Debian GNU/Linux 12\r\nlogin: "),daemon=True).start()
threading.Thread(target=banner,args=("127.0.0.2",3306,b"J\x00\x00\x00\n8.0.36\x00"),daemon=True).start()
srv=http.server.ThreadingHTTPServer(("127.0.0.3",80),T); threading.Thread(target=srv.serve_forever,daemon=True).start()
a=DummyAuthorizer(); a.add_anonymous("/tmp/lab/ftp"); h=FTPHandler; h.authorizer=a; h.banner="vsFTPd 3.0.3"
FTPServer(("127.0.0.2",21),h).serve_forever()
