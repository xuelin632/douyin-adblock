"""Create a per-user mitmproxy CA and trust its public certificate."""
import argparse
from pathlib import Path
import subprocess
from mitmproxy.certs import CertStore


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--confdir', type=Path, default=Path.home() / '.mitmproxy')
    parser.add_argument('--generate-only', action='store_true')
    args = parser.parse_args()
    CertStore.from_store(args.confdir, 'mitmproxy', 2048)
    certificate = args.confdir / 'mitmproxy-ca-cert.cer'
    if args.generate_only:
        print('Generated public certificate:', certificate)
        return
    print('Trusting the local mitmproxy CA for the current Windows user.')
    print('Windows may ask you to confirm the certificate installation.')
    subprocess.run(['certutil', '-addstore', '-user', 'Root', str(certificate)], check=True)
    print('Done. Open the launcher to start Douyin.')


if __name__ == '__main__':
    main()
