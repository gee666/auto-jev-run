"""Download only the matching AutoJev vision component and verify its pinned hash."""
import hashlib, json, pathlib, shutil, urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
REPO='thomasgauthier/autojev-27b-GGUF'
REVISION='834f2eac1702c7e47e12d63eb729297951362b8a'
NAME='mmproj-autojev-bf16.gguf'
SIZE=931146208
SHA='461cc3179ce17d2fa56ab0af23d1ec933390b4950e90cdceb74500b68ba454a9'
target=ROOT/'models/autojev-27b'/NAME
def digest(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
if not target.exists():
    if shutil.disk_usage(target.parent).free<SIZE+128*2**20:raise RuntimeError('Insufficient free disk space')
    part=target.with_suffix('.gguf.part')
    url=f'https://huggingface.co/{REPO}/resolve/{REVISION}/{NAME}'
    with urllib.request.urlopen(url,timeout=60) as response,part.open('wb') as output:
        downloaded=0;next_log=128*2**20
        while chunk:=response.read(4*2**20):
            output.write(chunk);downloaded+=len(chunk)
            if downloaded>=next_log:print(f'Downloaded {downloaded/2**20:.0f} MiB',flush=True);next_log+=128*2**20
    if part.stat().st_size!=SIZE or digest(part)!=SHA:raise RuntimeError('Vision checksum mismatch; partial file retained for inspection')
    part.replace(target)
if target.stat().st_size!=SIZE or digest(target)!=SHA:raise RuntimeError('Vision weights do not match the pinned release')
(target.parent/'vision-provenance.json').write_text(json.dumps({'repo':REPO,'revision':REVISION,'file':NAME,'size':SIZE,'sha256':SHA},indent=2))
print(f'Verified {NAME}, {SIZE:,} bytes',flush=True)
