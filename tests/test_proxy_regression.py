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
logs = []
addon._log = lambda message: logs.append(message)
passed = 0


def flow(url, content_type='application/json', status=200, body=b''):
    return types.SimpleNamespace(
        request=mitm_http.Request.make('GET', url),
        response=mitm_http.Response.make(status, body, {'content-type': content_type}))


# 1. responseheaders：仅目标信息流接口完整缓冲，其余响应全部即时流式转发
for url, mime, expected in [
    ('https://unknown.example/live/test', 'application/octet-stream', True),
    ('https://unknown.example/player', 'video/x-flv', True),
    ('https://unknown.example/player', 'Application/X-FLV; charset=binary', True),
    ('https://unknown.example/player', 'application/vnd.apple.mpegurl', True),
    ('https://unknown.example/player', 'text/event-stream', True),
    ('https://www.douyin.com/static/app.js', 'application/javascript', True),
    ('https://www.douyin.com/aweme/v1/web/tab/feed/', 'video/x-flv', False),
    ('https://www.douyin.com/aweme/v1/web/tab/feed/?signature=/live/', 'application/json', False),
]:
    f = flow(url, mime)
    addon.SelectiveDecrypt().responseheaders(f)
    assert f.response.stream is expected, (url, mime, f.response.stream)
    passed += 1

# 2. tls_clienthello：白名单模式，仅解密 www.douyin.com，其他域名全部直接隧道透传
for sni, connect_host, expected in [
    ('live.douyin.com', '127.0.0.1', True),
    ('pull-flv-l1.douyincdn.com', '127.0.0.1', True),
    ('webcast3-ws-web-lq.douyin.com', '127.0.0.1', True),
    ('v26-weba.douyinvod.com', '127.0.0.1', True),
    ('google.com', 'google.com', True),
    ('', 'live.douyin.com', True),
    ('www.douyin.com', 'www.douyin.com', False),
    ('WWW.DOUYIN.COM.', '127.0.0.1', False),
    ('', 'www.douyin.com', False),
]:
    data = types.SimpleNamespace(
        client_hello=types.SimpleNamespace(sni=sni),
        context=types.SimpleNamespace(server=types.SimpleNamespace(address=(connect_host, 443))),
        ignore_connection=False)
    addon.SelectiveDecrypt().tls_clienthello(data)
    assert data.ignore_connection is expected, (sni, connect_host, data.ignore_connection)
    passed += 1

# 3. 压缩编码、chunked 与广告过滤完整性检查
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
            addon.SelectiveDecrypt().responseheaders(f)
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

# 4. 非 200 / 空响应体 / HTML 响应不触发 parse fail
logs.clear()
for status, mime, body in [
    (204, 'application/json', b''),
    (200, 'application/json', b'   '),
    (200, 'text/html', b'<html>blocked</html>'),
    (302, 'application/json', b''),
]:
    f = flow('https://www.douyin.com/aweme/v1/web/tab/feed/', mime, status, body)
    addon.AdFilter().response(f)
    passed += 1
assert not any('parse fail' in m for m in logs), logs

print(f'PASS: {passed} offline checks', flush=True)

# 5. 真实本机代理流式首段数据转发测试
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
