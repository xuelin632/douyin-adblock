# -*- coding: utf-8 -*-
"""抖音去广告 mitmproxy addon —— 网络层过滤广告条目"""
import json, time, os

_HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(_HERE, 'filter.log')


def _log(msg):
    try:
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(f'{time.strftime("%H:%M:%S")} {msg}\n')
    except Exception:
        pass


# ---- 广告判定 ----
AD_FLAGS = ('is_ads',)
AD_OBJECTS = ('raw_ad_data', 'ad_candidates', 'ad_aweme_source')
AD_LISTS = ('commerce_top_labels', 'simple_promotions')


def is_ad_item(it):
    if not isinstance(it, dict):
        return False
    for k in AD_FLAGS:
        if it.get(k) is True:
            return True
    for k in AD_OBJECTS:
        if it.get(k):
            return True
    for k in AD_LISTS:
        v = it.get(k)
        if isinstance(v, (list, dict)) and len(v) > 0:
            return True
    return False


# ---- 直播放行 ----
LIVE_HOSTS = (
    'live.douyin.com',
    'bytelink',
    'bytelink-ws.douyin.com',
    'douyincdn.com',
    'pull-flv',
    'pull-hls',
    'webcast',
    'vcs.zijieapi.com',
    'rtc.douyin.com',
)

LIVE_PATH_HINTS = (
    '/webcast/',
    'bytelink',
    '/pull-',
    '.flv',
    '.m3u8',
    '/live/',
)


from urllib.parse import urlparse


def _is_live(url):
    """只判断 host 和 path，绝不匹配 query（query 里有超长签名，会误伤）"""
    if not url:
        return False
    try:
        u = urlparse(url if '://' in url else '//' + url)
        host = (u.hostname or '').lower()
        path = (u.path or '').lower()
    except Exception:
        return False
    for h in LIVE_HOSTS:
        if h in host:
            return True
    for hint in LIVE_PATH_HINTS:
        if hint in path:
            return True
    return False


TARGETS = (
    '/aweme/v1/web/tab/feed/',
    '/aweme/v1/web/feed/',
    '/aweme/v1/web/familiar/feed/',
    '/aweme/v1/web/follow/feed/',
    '/aweme/v1/web/nearby/feed/',
)


class AdFilter:
    def response(self, flow):
        url = flow.request.pretty_url
        if _is_live(url):
            return
        if not any(t in url for t in TARGETS):
            return
        try:
            resp = flow.response
            if not resp or not resp.content:
                return
            d = json.loads(resp.content.decode('utf-8'))
        except Exception as ex:
            _log(f'parse fail {url[:60]}: {ex}')
            return

        lst = d.get('aweme_list')
        if not isinstance(lst, list):
            return

        before = len(lst)
        kept = [x for x in lst if not is_ad_item(x)]
        removed = before - len(kept)
        if removed:
            d['aweme_list'] = kept
            body = json.dumps(d, ensure_ascii=False).encode('utf-8')
            # mitmproxy 会按原 Content-Encoding 重新编码，并自动更新 Content-Length。
            # 保留这两个响应头，避免客户端把压缩数据当成普通 JSON。
            resp.content = body
            _log(f'REMOVED {removed}/{before} ads  <- {url[:80]}')
        else:
            _log(f'pass {before} items (no ad)  <- {url[:60]}')


# ---- 直播 WebSocket / 长连接：不做 TLS 解密，直接隧道透传 ----
class LiveBypass:
    """已知直播主机跳过 TLS 解密，其余直播和媒体响应即时转发。"""

    def responseheaders(self, flow):
        if flow.response is None:
            return
        path = urlparse(flow.request.pretty_url).path.lower()
        # 推荐列表必须完整接收，才能继续过滤广告。
        if any(t in path for t in TARGETS):
            return
        content_type = flow.response.headers.get('content-type', '').split(';', 1)[0].strip().lower()
        if (_is_live(flow.request.pretty_url)
                or content_type.startswith(('video/', 'audio/'))
                or content_type in ('application/x-flv', 'application/vnd.apple.mpegurl',
                                    'application/x-mpegurl', 'application/dash+xml',
                                    'text/event-stream')):
            # response() 才放行已经太晚；长直播流可能一直等不到响应结束。
            flow.response.stream = True

    def tls_clienthello(self, data):
        try:
            sni = data.client_hello.sni or ''
            # 无 SNI 时，常规代理仍可从 CONNECT 的目标取得主机名。
            address = data.context.server.address
            host = sni or (address[0] if address else '')
            if _is_live(host):
                data.ignore_connection = True
        except Exception:
            pass


addons = [AdFilter(), LiveBypass()]
