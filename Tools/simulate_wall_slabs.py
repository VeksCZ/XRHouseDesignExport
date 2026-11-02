#!/usr/bin/env python3
"""Plan-view simulation of XRModelFactory wall slabs (EstimateWallThickness) on a geometry file from extract_scan_geometry.py: lists every wall slab that reaches into another room of the same storey.
needs: pip install shapely. usage: python3 Tools/simulate_wall_slabs.py   (reads novostavba.geometry.json in the current folder)"""
import json,math
from shapely.geometry import Polygon, LineString
d=json.load(open('novostavba.geometry.json')); R=d['rooms']
for r in R: r['poly']=Polygon(r['floor']).buffer(0)
def geo(r,w):
    a,b=w['a'],w['b']; L=math.hypot(b[0]-a[0],b[1]-a[1]); u=((b[0]-a[0])/L,(b[1]-a[1])/L)
    m=((a[0]+b[0])/2,(a[1]+b[1])/2); n=(-u[1],u[0])
    from shapely.geometry import Point
    # outward = side whose probe is outside own polygon
    p1=Point(m[0]+n[0]*0.15,m[1]+n[1]*0.15)
    out=n if not r['poly'].buffer(0.02).contains(p1) else (-n[0],-n[1])
    return m,u,L,out
FALLBACK=0.25
def thickness(r,w):
    m,u,L,out=geo(r,w); h=(w['bottom']+w['top'])/2; H=w['top']-w['bottom']
    best=1e9
    for o in R:
        if o is r: continue
        for v in o['walls']:
            m2,u2,L2,out2=geo(o,v)
            inward2=(-out2[0],-out2[1])
            if inward2[0]*out[0]+inward2[1]*out[1]<0.9: continue
            dx,dz=m2[0]-m[0],m2[1]-m[1]
            gap=dx*out[0]+dz*out[1]
            if gap<-0.15 or gap>0.5 or gap>=best: continue
            along=dx*u[0]+dz*u[1]
            if along-L2/2>L/2-0.15 or along+L2/2<-L/2+0.15: continue
            h2=(v['bottom']+v['top'])/2; H2=v['top']-v['bottom']
            if abs(h2-h)>(H+H2)/2-0.5: continue
            best=gap
    if best==1e9: return FALLBACK,0,False
    if best>=0.02: return best,0,True
    return 0.02,max(0,-best)/2,True
res=[]
for r in R:
    for w in r['walls']:
        if w['length']<0.3: continue
        m,u,L,out=geo(r,w); t,shift,matched=thickness(r,w)
        a,b=w['a'],w['b']
        # slab: from face (moved shift inward) outward by t
        f=lambda p,k:(p[0]+out[0]*k,p[1]+out[1]*k)
        s=Polygon([f(a,-shift),f(b,-shift),f(b,t-shift),f(a,t-shift)])
        for o in R:
            if o is r or abs(o['floorY']-r['floorY'])>1.0: continue
            inter=s.intersection(o['poly'].buffer(-0.03)).area
            if inter>0.02: res.append((r['id'][:6],str(w['id'])[:6],round(L,2),round(t,2),matched,o['id'][:6],round(inter,2)))
print('slabs entering another room:')
for x in sorted(res,key=lambda x:-x[-1]): print(x)
print('unmatched walls (fallback 25 cm):',sum(1 for r in R for w in r['walls'] if w['length']>=0.3 and not thickness(r,w)[2]))
