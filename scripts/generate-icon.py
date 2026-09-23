"""Original vector-style USB/backup icon; standard-library-only PNG and ICO renderer."""
import math, struct, zlib
from pathlib import Path
out = Path(__file__).resolve().parents[1] / 'Assets'
out.mkdir(parents=True, exist_ok=True)
svg = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
<rect x="88" y="20" width="80" height="65" rx="12" fill="#bdcfda"/>
<rect x="104" y="32" width="12" height="23" rx="3" fill="#40536b"/>
<rect x="140" y="32" width="12" height="23" rx="3" fill="#40536b"/>
<rect x="57" y="69" width="142" height="168" rx="35" fill="#102c3c"/>
<rect x="69" y="79" width="118" height="137" rx="25" fill="#147d79"/>
<path d="M116 107 H140 V149 H161 L128 181 L95 149 H116Z" fill="#d2fff0"/>
<rect x="97" y="192" width="62" height="8" rx="4" fill="#67c9b4"/>
</svg>'''
(out/'app-icon.svg').write_text(svg)
def rr(x,y,a,b,w,h,r):
    if not (a<=x<=a+w and b<=y<=b+h): return False
    dx=max(a+r-x,0,x-(a+w-r)); dy=max(b+r-y,0,y-(b+h-r))
    return dx*dx+dy*dy<=r*r
poly=[(116,107),(140,107),(140,149),(161,149),(128,181),(95,149),(116,149)]
def polygon(x,y):
    inside=False
    for i,(a,b) in enumerate(poly):
        c,d=poly[i-1]
        if (b>y)!=(d>y) and x<(c-a)*(y-b)/(d-b)+a: inside=not inside
    return inside
layers=[(88,20,80,65,12,(189,207,218)),(104,32,12,23,3,(64,83,107)),(140,32,12,23,3,(64,83,107)),(57,69,142,168,35,(16,44,60)),(69,79,118,137,25,(20,125,121))]
def sample(x,y):
    color=(0,0,0,0)
    for a,b,w,h,r,rgb in layers:
        if rr(x,y,a,b,w,h,r): color=(*rgb,255)
    if polygon(x,y): color=(210,255,240,255)
    if rr(x,y,97,192,62,8,4): color=(103,201,180,255)
    return color

def png(n):
    raw=bytearray()
    aa=4
    for y in range(n):
        raw.append(0)
        for x in range(n):
            samples=[sample((x+(sx+.5)/aa)*256/n,(y+(sy+.5)/aa)*256/n) for sy in range(aa) for sx in range(aa)]
            alpha=sum(p[3] for p in samples)
            raw.extend([round(sum(p[c]*p[3] for p in samples)/alpha) if alpha else 0 for c in range(3)])
            raw.append(round(alpha/(aa*aa)))
    def chunk(k,d): return struct.pack('>I',len(d))+k+d+struct.pack('>I',zlib.crc32(k+d)&0xffffffff)
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',n,n,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b'')
sizes=[16,24,32,48,64,128,256]
images=[png(n) for n in sizes]
offset=6+16*len(sizes)
headers=[]
for n,data in zip(sizes,images):
    headers.append(struct.pack('<BBBBHHII',n%256,n%256,0,0,1,32,len(data),offset)); offset+=len(data)
(out/'app-icon.ico').write_bytes(struct.pack('<HHH',0,1,len(sizes))+b''.join(headers)+b''.join(images))
(out/'app-icon.png').write_bytes(images[-1])
print('Generated original SVG, PNG and 7-size ICO')
