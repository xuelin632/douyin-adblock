# -*- coding: utf-8 -*-
"""抖音去广告 mitmproxy addon —— 网络层过滤广告条目"""
import json, time, os

LOG = r'F:\dsh-work-big\douyin-proxy\filter.log'
DUMP = r'F:\dsh-work-big\douyin-proxy\dump.log'


def _log(msg):
    try:
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(f'{time.strftime("%H:%M:%S")} {msg}\n')
    except Exception:
        pass


def _dump(msg):
    try:
        with open(DUMP, 'a', encoding='utf-8') as f:
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


# ---- 深度广告特征检测（第二层）----
# 抖音有些「品牌号推广」不走 is_ads，而是这些字段
AD_DEEP_MARKS = (
    'is_advertisement', 'ad_type', 'is_soft_ad', 'is_ecom_ad',
    'is_brand_ad', 'commercial_type', 'ad_brand_info',
    'is_prop_ad', 'is_live_ad', 'is_task_ad', 'ad_info',
    'ad_tag', 'ad_log_extra', 'ad_brand', 'promotion_info',
    'is_game_ad', 'is_shop_ad', 'commerce_info',
)


def deep_ad_reason(it):
    """返回命中的字段名列表"""
    hits = []
    if not isinstance(it, dict):
        return hits
    for k in AD_DEEP_MARKS:
        v = it.get(k)
        if v not in (None, False, 0, '', [], {}):
            hits.append(k)
    return hits


# ---- 直播相关：直接放行，不走代理（直播流延迟敏感，代理会拖断）----
LIVE_HOSTS = (
    'live.douyin.com',
    'bytelink',              # bytelink100-ws.douyin.com 等
    'bytelink-ws.douyin.com',
    'douyincdn.com',         # pull-flv / hls CDN
    'pull-flv',
    'pull-hls',
    'webcast',
    'douyinpic.com/live',
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


def _is_live(url: str) -> bool:
    """判断是否直播相关请求"""
    u = url.lower()
    for h in LIVE_HOSTS:
        if h in u:
            return True
    for hint in LIVE_PATH_HINTS:
        if hint in u:
            return True
    return False


TARGETS = (
    '/aweme/v1/web/tab/feed/',
    '/aweme/v1/web/feed/',
    '/aweme/v1/web/familiar/feed/',
    '/aweme/v1/web/follow/feed/',
    '/aweme/v1/web/nearby/feed/',
    '/aweme/v1/web/hot/search/list/',
    '/aweme/v1/web/general/search/',
)


class AdFilter:
    def __init__(self):
        self.n = 0

    def response(self, flow):
        url = flow.request.pretty_url
        # 直播相关一律放行（不处理，让代理透传）
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
        kept = []
        for x in lst:
            if is_ad_item(x):
                continue
            # 深度检测只记录，不删（避免误杀）
            hits = deep_ad_reason(x)
            if hits:
                aid = x.get('aweme_id', '?')
                desc = ''
                try:
                    desc = (x.get('desc') or '')[:40]
                except Exception:
                    pass
                _dump(f'DEEP-HIT aweme={aid} fields={hits} desc={desc}')
            kept.append(x)

        removed = before - len(kept)
        if removed:
            d['aweme_list'] = kept
            body = json.dumps(d, ensure_ascii=False).encode('utf-8')
            resp.content = body
            try:
                resp.headers.pop('content-length', None)
            except Exception:
                pass
            _log(f'REMOVED {removed}/{before} ads  <- {url[:80]}')
        else:
            _log(f'pass {before} items (no ad)  <- {url[:60]}')




# ---- 直播 WebSocket 长连接：不拦截，直接隧道透传 ----
def _should_bypass(flow_url: str) -> bool:
    return _is_live(flow_url)


class LiveBypass:
    """对直播 WebSocket 请求不做 TLS 解密，直接隧道透传"""

    def http_connect(self, flow):
        host = flow.request.host or ''
        if any(h in host.lower() for h in LIVE_HOSTS):
            flow.request.ignore_hosts = True

    def tls_clienthello(self, data):
        sni = ''
        try:
            sni = data.client_hello.sni or ''
        except Exception:
            pass
        if sni and any(h in sni.lower() for h in LIVE_HOSTS):
            data.ignore_connection = True


addons = [AdFilter(), LiveBypass()]
