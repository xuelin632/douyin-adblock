"""Build a portable Windows package and a one-file update from a working runtime."""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def archive(folder, destination):
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as package:
        for path in sorted(folder.rglob('*')):
            if path.is_file():
                package.write(path, path.relative_to(folder.parent))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--python-dir', type=Path, default=Path(sys.prefix))
    parser.add_argument('--version', default='1.1.0')
    parser.add_argument('--output', type=Path, default=ROOT / 'dist')
    args = parser.parse_args()
    runtime = args.python_dir.resolve()
    output = args.output.resolve()
    stage = output / ('stage-v' + args.version)
    if stage.exists():
        raise SystemExit('Build stage already exists; choose a fresh --output directory: ' + str(stage))
    python = runtime / 'python.exe'
    csc = Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
    if not python.is_file() or not csc.is_file():
        raise SystemExit('Requires a working Windows Python runtime and .NET Framework C# compiler.')
    subprocess.run([str(python), '-B', '-c', 'import mitmproxy.addons; import mitmproxy.tools.main'], check=True)
    output.mkdir(parents=True, exist_ok=True)
    package = stage / 'douyin-adblock'
    package.mkdir(parents=True)
    print('Copying the working Python runtime...', flush=True)
    shutil.copytree(runtime, package / 'python', ignore=shutil.ignore_patterns('__pycache__', '*.pyc', '*.pyo'))
    for name in ('ad_filter.py', 'run_mitmdump.py', 'DouyinLauncher.cs'):
        shutil.copy2(ROOT / 'src' / name, package / name)
    for source, dest in [('install-cert.bat', '安装证书.bat'), ('install_cert.py', 'install_cert.py'),
                         ('README-便携版.txt', '使用说明.txt')]:
        shutil.copy2(ROOT / 'build' / source, package / dest)
    shutil.copy2(ROOT / 'LICENSE', package / 'LICENSE')
    subprocess.run([str(csc), '/nologo', '/optimize+', '/target:exe', '/codepage:65001',
                    '/out:' + str(package / '抖音去广告启动器.exe'), str(ROOT / 'src/DouyinLauncher.cs')], check=True)
    subprocess.run([str(package / 'python/python.exe'), '-B', str(package / 'run_mitmdump.py'), '--version'], check=True)
    full = output / ('douyin-adblock-portable-v' + args.version + '.zip')
    print('Creating full portable archive...', flush=True)
    archive(package, full)
    patch = output / ('douyin-adblock-update-v' + args.version + '.zip')
    with zipfile.ZipFile(patch, 'w', zipfile.ZIP_DEFLATED) as z:
        z.write(ROOT / 'src/ad_filter.py', 'ad_filter.py')
        z.write(ROOT / 'src/DouyinLauncher.cs', 'DouyinLauncher.cs')
        z.write(package / '抖音去广告启动器.exe', '抖音去广告启动器.exe')
        z.writestr(
            'UPDATE.txt',
            '完全退出抖音和旧版启动器后，将本压缩包中的 ad_filter.py、DouyinLauncher.cs 和 抖音去广告启动器.exe 覆盖到旧版所在文件夹即可。\n'
            '无需重新安装 Python 运行环境或证书。首次使用请下载完整便携包。\n'
        )
    checksums = output / 'SHA256SUMS.txt'
    checksums.write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest() + '  ' + p.name + '\n' for p in (full, patch)), encoding='ascii')
    for p in (full, patch, checksums):
        print(str(p) + ' (' + str(p.stat().st_size) + ' bytes)', flush=True)


if __name__ == '__main__':
    main()
