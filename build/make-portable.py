# -*- coding: utf-8 -*-
"""构建便携包：把系统 Python + mitmproxy 精简复制到 dist/，再打包成 zip。

用法：
    python build/make-portable.py

需要：源环境已安装 mitmproxy（pip install mitmproxy）
"""
import os
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DIST = os.path.join(ROOT, 'dist')
PKG = os.path.join(DIST, 'douyin-adblock')

# 源 Python 目录（改成本机的）
SRC_PY = sys.prefix


def copy_tree(src, dst, skip=()):
    os.makedirs(dst, exist_ok=True)
    for name in os.listdir(src):
        if name in skip:
            continue
        s = os.path.join(src, name)
        d = os.path.join(dst, name)
        if os.path.isdir(s):
            shutil.copytree(s, d, dirs_exist_ok=True)
        else:
            shutil.copy2(s, d)


def main():
    if os.path.exists(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DIST)

    print('[1/5] 复制 Python 核心 ...')
    pydir = os.path.join(PKG, 'python')
    os.makedirs(pydir)
    for f in ['python.exe', 'pythonw.exe', os.path.basename(sys.executable).replace('python', 'python'),
              'vcruntime140.dll', 'vcruntime140_1.dll', 'python3.dll']:
        s = os.path.join(SRC_PY, f)
        if os.path.isfile(s):
            shutil.copy2(s, os.path.join(pydir, f))
    for dll in os.listdir(SRC_PY):
        if dll.startswith('python3') and dll.endswith('.dll'):
            shutil.copy2(os.path.join(SRC_PY, dll), os.path.join(pydir, dll))

    print('[2/5] 复制 DLLs ...')
    copy_tree(os.path.join(SRC_PY, 'DLLs'), os.path.join(pydir, 'DLLs'))

    print('[3/5] 复制标准库（跳过 test/tkinter 等）...')
    skip = {'test', 'tkinter', 'idlelib', 'turtledemo', 'lib2to3',
            'site-packages', '__pycache__'}
    copy_tree(os.path.join(SRC_PY, 'Lib'), os.path.join(pydir, 'Lib'), skip=skip)

    print('[4/5] 复制 mitmproxy 及其依赖（按缺失逐个补）...')
    src_sp = os.path.join(SRC_PY, 'Lib', 'site-packages')
    dst_sp = os.path.join(pydir, 'Lib', 'site-packages')
    os.makedirs(dst_sp, exist_ok=True)

    import re
    added = set()
    target_py = os.path.join(pydir, 'python.exe')
    for i in range(80):
        r = subprocess.run(
            [target_py, '-c', 'import mitmproxy.addons; import mitmproxy.tools.main'],
            capture_output=True, text=True, timeout=60,
            creationflags=0x08000000 if os.name == 'nt' else 0)
        if r.returncode == 0:
            print(f'    依赖完整（补了 {len(added)} 个）')
            break
        m = re.search(r"No module named '([^']+)'", r.stderr)
        if not m:
            print('    其他错误：', r.stderr[-400:])
            break
        mod = m.group(1).split('.')[0]
        if mod in added:
            print(f'    {mod} 重复缺失，停止')
            break
        added.add(mod)
        low = mod.lower()
        hit = False
        for entry in os.listdir(src_sp):
            e = entry.lower()
            base = e.split('-')[0].split('.')[0]
            if base == low or e.startswith(low + '.') or e.startswith(low + '-'):
                s = os.path.join(src_sp, entry)
                d = os.path.join(dst_sp, entry)
                if os.path.isdir(s):
                    shutil.copytree(s, d, dirs_exist_ok=True)
                elif os.path.isfile(s):
                    shutil.copy2(s, d)
                hit = True
        print(f'    + {mod}' + ('' if hit else '（源环境没有）'))
        if not hit:
            break

    print('[5/5] 复制应用文件 ...')
    shutil.copy2(os.path.join(ROOT, 'src', 'ad_filter.py'), os.path.join(PKG, 'ad_filter.py'))
    shutil.copy2(os.path.join(ROOT, 'src', 'DouyinLauncher.cs'), os.path.join(PKG, 'DouyinLauncher.cs'))
    shutil.copy2(os.path.join(HERE, 'install-cert.bat'), os.path.join(PKG, '安装证书.bat'))
    shutil.copy2(os.path.join(HERE, 'README-便携版.txt'), os.path.join(PKG, '使用说明.txt'))

    # 编译启动器
    csc = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if os.path.isfile(csc):
        print('    编译启动器 ...')
        subprocess.run([csc, '/nologo', '/target:exe', '/codepage:65001',
                        '/out:' + os.path.join(PKG, '抖音去广告.exe'),
                        os.path.join(ROOT, 'src', 'DouyinLauncher.cs')],
                       check=False)

    # 打包 zip
    zip_path = os.path.join(DIST, 'douyin-adblock-portable.zip')
    print('打包 zip ...')
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for r, d, fs in os.walk(PKG):
            for f in fs:
                full = os.path.join(r, f)
                z.write(full, os.path.relpath(full, DIST))

    size = os.path.getsize(zip_path) / 1024 / 1024
    print(f'\n完成: {zip_path} ({size:.1f} MB)')


if __name__ == '__main__':
    main()
