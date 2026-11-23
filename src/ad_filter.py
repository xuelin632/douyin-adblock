# -*- coding: utf-8 -*-
"""抖音去广告 mitmproxy addon —— TLS 白名单精准解密 + 信息流广告过滤"""
import json
import os
import time
from urllib.parse import urlparse

_HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(_HERE, 'filter.log')
LOG_OLD = os.path.join(_HERE, 'filter.log.old')
MAX_LOG_BYTES = 1024 * 1024  # 1 MB 自动轮转
_log_write_count = 0


def _rotate_log_if_needed():
    try:
        if os.path.exists(LOG) and os.path.getsize(LOG) >= MAX_LOG_BYTES:
            os.replace(LOG, LOG_OLD)
    except Exception:
        pass


def _log(msg):
    global _log_write_count
    try:
        _log_write_count += 1
        if _log_write_count == 1 or _log_write_count % 50 == 0:
            _rotate_log_if_needed()
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(f'{time.strftime("%Y-%m-%d %H:%M:%S")} {msg}\n')
    except Exception:
        pass


# ---- TLS 解密白名单 ----
# 仅解密真正下发抖音信息流 JSON 的域名；
# 其余所有域名（直播流、视频 CDN、浏览器及其他客户端）直接 TLS 隧道透传，
# 避免无谓解密开销与证书固定（SSL Pinning）冲突。
DECRYPT_HOSTS = frozenset((
    'www.douyin.com',
))


def _should_decrypt_host(host):
    if not host:
        return False
    return host.strip().lower().rstrip('.') in DECRYPT_HOSTS


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


# 需要检查并过滤广告的信息流接口路径
TARGETS = (
    '/aweme/v1/web/tab/feed/',
    '/aweme/v1/web/feed/',
    '/aweme/v1/web/familiar/feed/',
    '/aweme/v1/web/follow/feed/',
    '/aweme/v1/web/nearby/feed/',
)


def _is_target_path(path):
    if not path:
        return False
    p = path.lower()
    return any(t in p for t in TARGETS)


class AdFilter:
    def response(self, flow):
        url = flow.request.pretty_url
        try:
            u = urlparse(url)
            host = (u.hostname or '').lower()
            path = (u.path or '').lower()
        except Exception:
            return

        if not _should_decrypt_host(host) or not _is_target_path(path):
            return

        resp = flow.response
        if not resp or resp.status_code != 200:
            return

        content_type = resp.headers.get('content-type', '').split(';', 1)[0].strip().lower()
        if content_type and 'json' not in content_type and content_type != 'text/plain':
            return

        raw = resp.content
        if not raw or not raw.strip():
            return

        try:
            d = json.loads(raw.decode('utf-8'))
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
            note = ' [all filtered]' if len(kept) == 0 else ''
            _log(f'REMOVED {removed}/{before} ads{note}  <- {url[:80]}')
        else:
            _log(f'pass {before} items (no ad)  <- {url[:60]}')


class SelectiveDecrypt:
    """白名单之外的 TLS 连接直接透传；白名单内的非目标响应即时流式转发。"""

    def tls_clienthello(self, data):
        try:
            sni = data.client_hello.sni or ''
            address = data.context.server.address
            host = sni or (address[0] if address else '')
            if not _should_decrypt_host(host):
                data.ignore_connection = True
        except Exception:
            # 出错时保守透传，不阻断正常连接
            data.ignore_connection = True

    def responseheaders(self, flow):
        if flow.response is None:
            return
        try:
            path = urlparse(flow.request.pretty_url).path.lower()
        except Exception:
            return
        # 仅信息流目标接口需要完整缓冲后改写 JSON，其余响应一律流式即时转发。
        if not _is_target_path(path):
            flow.response.stream = True


addons = [AdFilter(), SelectiveDecrypt()]
