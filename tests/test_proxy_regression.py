import http.client
import http.server
import json
from pathlib import Path
import socket
import subprocess
import threading
import time
import types
import sys
import tempfile

from mitmproxy import http as mitm_http
from mitmproxy.net import encoding

ROOT = Path(sys.argv[1]).resolve()
scratch = tempfile.TemporaryDirectory(prefix='douyin-regression-')
WORK = Path(scratch.name)
addon = types.ModuleType('tested_addon')
addon.__file__ = str(ROOT / 'ad_filter.py')
exec(compile((ROOT / 'ad_filter.py').read_bytes(), addon.__file__, 'exec'), addon.__dict__)
addon._log = lambda message: None
passed = 0

def flow(url, content_type='application/json'):
    return types.SimpleNamespace(
        request=mitm_http.Request.make('GET', url),
        response=mitm_http.Response.make(200, b'', {'content-type': content_type}))

for url, mime, expected in [
    ('https://unknown.example/live/test', 'application/octet-stream', True),
    ('https://unknown.example/player', 'video/x-flv', True),
    ('https://unknown.example/player', 'Application/X-FLV; charset=binary', True),
    ('https://unknown.example/player', 'application/vnd.apple.mpegurl', True),
    ('https://unknown.example/player', 'text/event-stream', True),
    ('https://unknown.example/api', 'application/json', False),
    ('https://www.douyin.com/aweme/v1/web/tab/feed/', 'video/x-flv', False),
    ('https://www.douyin.com/aweme/v1/web/tab/feed/?signature=/live/', 'application/json', False),
]:
    f = flow(url, mime)
    addon.LiveBypass().responseheaders(f)
    assert f.response.stream is expected, (url, mime)
    passed += 1

for sni, connect_host, expected in [
    ('live.douyin.com', '127.0.0.1', True),
    ('pull-flv-l1.douyincdn.com', '127.0.0.1', True),
    ('webcast3-ws-web-lq.douyin.com', '127.0.0.1', True),
    ('', 'live.douyin.com', True),
    ('www.douyin.com', 'www.douyin.com', False),
    ('', 'www.douyin.com', False),
]:
    data = types.SimpleNamespace(
        client_hello=types.SimpleNamespace(sni=sni),
        context=types.SimpleNamespace(server=types.SimpleNamespace(address=(connect_host, 443))),
        ignore_connection=False)
    addon.LiveBypass().tls_clienthello(data)
    assert data.ignore_connection is expected, (sni, connect_host)
    passed += 1

for enc in ('identity', 'gzip', 'br', 'zstd'):
    for ad in (False, True):
        for chunked in (False, True):
            f = flow('https://www.douyin.com/aweme/v1/web/tab/feed/')
            f.response.headers['content-encoding'] = enc
            if chunked:
                f.response.headers['transfer-encoding'] = 'chunked'
                f.response.headers.pop('content-length', None)
            items = [{'aweme_id': 'normal'}, {'aweme_id': 'other', 'is_ads': ad}]
            f.response.content = json.dumps({'aweme_list': items, 'has_more': 1, 'max_cursor': 123}).encode()
            addon.LiveBypass().responseheaders(f)
            assert f.response.stream is False
            addon.AdFilter().response(f)
            result = json.loads(encoding.decode(f.response.raw_content, f.response.headers['content-encoding']))
            assert result['aweme_list'] == (items[:1] if ad else items)
            assert result['has_more'] == 1 and result['max_cursor'] == 123
            if chunked:
                assert 'content-length' not in f.response.headers
            else:
                assert int(f.response.headers['content-length']) == len(f.response.raw_content)
            passed += 1
print(f'PASS: {passed} offline checks', flush=True)

gates = {}
class MediaServer(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        gate = gates[self.path]
        self.send_response(200)
        self.send_header('Content-Type', 'video/x-flv' if self.path == '/player' else 'application/octet-stream')
        self.send_header('Content-Length', '9')
        self.end_headers()
        self.wfile.write(b'FIRST')
        self.wfile.flush()
        if not gate.wait(6):
            return
        self.wfile.write(b'LAST')
        self.wfile.flush()

    def log_message(self, *args):
        pass

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), MediaServer)
threading.Thread(target=server.serve_forever, daemon=True).start()
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
proc = None
try:
    with (WORK / 'live-test-proxy.log').open('w', encoding='utf-8') as log:
        proc = subprocess.Popen([
            str(ROOT / 'python/python.exe'), '-B', str(ROOT / 'run_mitmdump.py'),
            '--listen-host', '127.0.0.1', '--listen-port', str(port),
            '--set', 'confdir=' + str(WORK / 'mitm-test-config'),
            '-q', '-s', str(ROOT / 'ad_filter.py'),
        ], cwd=WORK, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW)
        deadline = time.monotonic() + 15
        while True:
            if proc.poll() is not None:
                raise RuntimeError('Test proxy exited; see live-test-proxy.log')
            try:
                with socket.create_connection(('127.0.0.1', port), timeout=.2):
                    break
            except OSError:
                if time.monotonic() > deadline:
                    raise RuntimeError('Test proxy did not start')
                time.sleep(.1)
        for path in ('/live/test.flv', '/player'):
            gate = threading.Event()
            gates[path] = gate
            client = http.client.HTTPConnection('127.0.0.1', port, timeout=3)
            try:
                start = time.monotonic()
                client.request('GET', f'http://127.0.0.1:{server.server_port}{path}')
                response = client.getresponse()
                assert response.status == 200
                assert response.read(5) == b'FIRST'
                assert not gate.is_set()
                elapsed = time.monotonic() - start
                gate.set()
                assert response.read() == b'LAST'
                print(f'PASS: real proxy {path} first bytes in {elapsed:.3f}s before server completion', flush=True)
            finally:
                gate.set()
                client.close()
finally:
    for gate in gates.values():
        gate.set()
    if proc is not None and proc.poll() is None:
        proc.terminate()
        proc.wait(timeout=10)
    server.shutdown()
    server.server_close()
print('PASS: all live and video regression checks; owned test proxy cleaned up', flush=True)

scratch.cleanup()
