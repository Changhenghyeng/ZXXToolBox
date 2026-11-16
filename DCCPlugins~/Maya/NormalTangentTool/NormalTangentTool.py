# -*- coding: utf-8 -*-
"""
NormalTangentTool.py  —  Maya 2018-2024
================================================================
选中模型 → 切线空间平滑法线 XY → UV set "outlineData"。
Shader: sqrt(1-x²-y²) 还原 Z。

全程只读不改网格拓扑/构造历史/绑定。
用法: 拖入 Maya 视口安装; import NormalTangentTool; NormalTangentTool.show()
================================================================
"""
from __future__ import division, print_function

import os, sys, math
import maya.cmds as cmds
import maya.OpenMaya as om
import maya.mel as mel

try:
    from PySide2 import QtWidgets, QtCore
except ImportError:
    from PySide6 import QtWidgets, QtCore

OUTLINE_UV = "outlineData"
PAD_UV      = "_outlinePad"
WELD_TOL   = 1e-3
TARGET_IDX  = 2
EPS = 1e-12


def _weld(pts, nv):
    if nv == 0: return []
    lo=[1e30]*3; hi=[-1e30]*3
    for v in range(nv):
        p=pts[v]
        if p.x<lo[0]: lo[0]=p.x
        if p.y<lo[1]: lo[1]=p.y
        if p.z<lo[2]: lo[2]=p.z
        if p.x>hi[0]: hi[0]=p.x
        if p.y>hi[1]: hi[1]=p.y
        if p.z>hi[2]: hi[2]=p.z
    d=((hi[0]-lo[0])**2+(hi[1]-lo[1])**2+(hi[2]-lo[2])**2)**0.5
    tol=max(d*WELD_TOL,1e-6); t2=tol*tol; inv=1.0/tol
    rt=list(range(nv))
    def f(x):
        while rt[x]!=x: rt[x]=rt[rt[x]]; x=rt[x]
        return x
    def u(a,b):
        ra,rb=f(a),f(b)
        if ra!=rb: rt[rb]=ra
    c={}
    for v in range(nv):
        p=pts[v]; cx=int(math.floor(p.x*inv)); cy=int(math.floor(p.y*inv)); cz=int(math.floor(p.z*inv))
        for dx in (-1,0,1):
            for dy in (-1,0,1):
                for dz in (-1,0,1):
                    b=c.get((cx+dx,cy+dy,cz+dz))
                    if not b: continue
                    for ov in b:
                        op=pts[ov]; dx=p.x-op.x; dy=p.y-op.y; dz=p.z-op.z
                        if dx*dx+dy*dy+dz*dz<=t2: u(v,ov)
        c.setdefault((cx,cy,cz),[]).append(v)
    return [f(v) for v in range(nv)]


def _norm(x,y,z):
    ln=(x*x+y*y+z*z)**0.5
    if ln<EPS: return (0.0,0.0,0.0)
    s=1.0/ln; return (x*s,y*s,z*s)


def _uv_sets(node):
    return cmds.polyUVSet(node,q=True,allUVSets=True) or []


def compute(mesh_dag):
    sel=om.MSelectionList(); sel.add(mesh_dag)
    dag=om.MDagPath(); sel.getDagPath(0,dag); fn=om.MFnMesh(dag)

    pts=om.MPointArray(); fn.getPoints(pts,om.MSpace.kObject); nv=pts.length()
    tc=om.MIntArray(); tv=om.MIntArray(); fn.getTriangles(tc,tv); nf=tc.length()

    # 跟 Unity 完全一致的固定量化合并（Key: round(v*10000)）
    Q=10000.0
    grp={}; idx=0
    for fi in range(nf):
        for _ in range(tc[fi]):
            i0=tv[idx]; i1=tv[idx+1]; i2=tv[idx+2]; idx+=3
            p0=pts[i0]; p1=pts[i1]; p2=pts[i2]
            ex=p1.x-p0.x; ey=p1.y-p0.y; ez=p1.z-p0.z
            fx=p2.x-p0.x; fy=p2.y-p0.y; fz=p2.z-p0.z
            nx=ey*fz-ez*fy; ny=ez*fx-ex*fz; nz=ex*fy-ey*fx
            ln=(nx*nx+ny*ny+nz*nz)**0.5
            if ln<EPS: continue
            s=1.0/ln; nx*=s; ny*=s; nz*=s
            for v in (i0,i1,i2):
                p=pts[v]; k=(int(round(p.x*Q)),int(round(p.y*Q)),int(round(p.z*Q)))
                g=grp.get(k)
                if g: g[0]+=nx; g[1]+=ny; g[2]+=nz
                else: grp[k]=[nx,ny,nz]
    gn={}
    for k,g in grp.items():
        ln=(g[0]**2+g[1]**2+g[2]**2)**0.5; s=1.0/ln if ln>EPS else 1.0
        gn[k]=(g[0]*s,g[1]*s,g[2]*s) if ln>EPS else (0,0,1)
    sm=[]
    for v in range(nv):
        p=pts[v]; k=(int(round(p.x*Q)),int(round(p.y*Q)),int(round(p.z*Q)))
        sm.append(gn[k])
    print("[NormalTangent] fixed-quantize: {} verts -> {} groups".format(nv, len(gn)))

    nm=om.MFloatVectorArray(); tn=om.MFloatVectorArray(); bn=om.MFloatVectorArray()
    fn.getNormals(nm,om.MSpace.kObject); fn.getTangents(tn,om.MSpace.kObject)
    fn.getBinormals(bn,om.MSpace.kObject); nn=nm.length(); nt=tn.length()

    fu={}; fv={}
    it=om.MItMeshFaceVertex(dag)
    while not it.isDone():
        fid=it.faceId(); vid=it.vertId(); nid=it.normalId(); tid=it.tangentId()
        sn=sm[vid]
        if nid<0 or nid>=nn or tid<0 or tid>=nt:
            fu[(fid,vid)]=0.5; fv[(fid,vid)]=0.5; it.next(); continue
        rn=nm[nid]; rt=tn[tid]; rb=bn[tid]
        Nx,Ny,Nz=_norm(rn.x,rn.y,rn.z)
        Tx=rt.x-Nx*(Nx*rt.x+Ny*rt.y+Nz*rt.z)
        Ty=rt.y-Ny*(Nx*rt.x+Ny*rt.y+Nz*rt.z)
        Tz=rt.z-Nz*(Nx*rt.x+Ny*rt.y+Nz*rt.z)
        Tx,Ty,Tz=_norm(Tx,Ty,Tz)
        Bx=Ny*Tz-Nz*Ty; By=Nz*Tx-Nx*Tz; Bz=Nx*Ty-Ny*Tx
        w=1.0 if (Bx*rb.x+By*rb.y+Bz*rb.z)>=0.0 else -1.0
        Bx*=w; By*=w; Bz*=w
        fu[(fid,vid)]=sn[0]*Tx+sn[1]*Ty+sn[2]*Tz
        fv[(fid,vid)]=sn[0]*Bx+sn[1]*By+sn[2]*Bz
        it.next()

    up=[]; vp=[]
    uc=om.MIntArray(); ui=om.MIntArray()
    pit=om.MItMeshPolygon(dag); vs=om.MIntArray(); r=0
    while not pit.isDone():
        fid=pit.index(); pit.getVertices(vs); n=vs.length(); uc.append(n)
        for j in range(n):
            v=vs[j]
            u=fu.get((fid,v),0.5); vv=fv.get((fid,v),0.5)
            l2=u*u+vv*vv
            if l2>1.0: s=1.0/math.sqrt(l2); u*=s; vv*=s
            up.append(u*0.5+0.5); vp.append(vv*0.5+0.5)
            ui.append(r); r+=1
        pit.next()
    return up, vp, uc, ui


def pad_uv(node, fn):
    ex=_uv_sets(node); m1=ex[0] if ex else "map1"
    mu=om.MFloatArray(); mv=om.MFloatArray(); fn.getUVs(mu,mv,m1)
    mc=om.MIntArray(); mi=om.MIntArray(); fn.getAssignedUVs(mc,mi,m1)
    cur=len(ex); pi=0
    while cur<TARGET_IDX:
        p="{}{}".format(PAD_UV,pi); pi+=1
        if p in _uv_sets(node): fn.deleteUVSet(p)
        fn.createUVSetWithName(p); fn.setUVs(mu,mv,p); fn.assignUVs(mc,mi,p); cur+=1


def write_uv(node, fn, name, ul, vl, uc, ui):
    if name in _uv_sets(node): fn.deleteUVSet(name)
    fn.createUVSetWithName(name)
    ua=om.MFloatArray(); va=om.MFloatArray()
    for x in ul: ua.append(x)
    for x in vl: va.append(x)
    fn.setUVs(ua,va,name); fn.assignUVs(uc,ui,name)


def run(meshes, log=print):
    import traceback; ok=0
    for n in meshes:
        try:
            log("-- {} --".format(n))
            up,vp,uc,ui=compute(n)
            log("  {} uv entries".format(len(up)))
            sl=om.MSelectionList(); sl.add(n); dg=om.MDagPath(); sl.getDagPath(0,dg); fn=om.MFnMesh(dg)
            pad_uv(dg.fullPathName(),fn); write_uv(dg.fullPathName(),fn,OUTLINE_UV,up,vp,uc,ui)
            ok+=1; uv=_uv_sets(dg.fullPathName())
            log("[OK] {}  ->  UV #{}".format(n, uv.index(OUTLINE_UV) if OUTLINE_UV in uv else -1))
        except Exception as e:
            log("[FAIL] {}".format(str(e))); log(traceback.format_exc())
    log("-- Done: {}/{} --".format(ok,len(meshes)))
    cmds.select(meshes,replace=True)


class Win(QtWidgets.QDialog):
    def __init__(self,p=None):
        super(Win,self).__init__(p); self.setWindowTitle("Normal -> Tangent Tool")
        self.setMinimumWidth(420); self.setMinimumHeight(300)
        self.setAttribute(QtCore.Qt.WA_DeleteOnClose,False); self._ui()
    def _ui(self):
        l=QtWidgets.QVBoxLayout(self)
        i=QtWidgets.QLabel("TS normal XY -> UV \"{}\"\nShader: sqrt(1-x*x-y*y) recovers Z".format(OUTLINE_UV))
        i.setWordWrap(True); l.addWidget(i)
        w=QtWidgets.QLabel(
            "!!  FBX EXPORT: Geometry -> [v] Tangents and Binormals  !!\n"
            "!!  Unity: Model Inspector -> Tangents = Import           !!"
        )
        w.setWordWrap(True); w.setStyleSheet("background:#553300;color:#FFCC00;padding:8px;font-weight:bold;border-radius:4px")
        l.addWidget(w)
        l.addWidget(QtWidgets.QLabel("Selected:"))
        self._lst=QtWidgets.QListWidget(); self._lst.setSelectionMode(QtWidgets.QAbstractItemView.ExtendedSelection); l.addWidget(self._lst)
        br=QtWidgets.QHBoxLayout()
        b1=QtWidgets.QPushButton("Refresh"); b1.clicked.connect(self._rf); br.addWidget(b1)
        b2=QtWidgets.QPushButton("From Sel"); b2.clicked.connect(self._ps); br.addWidget(b2)
        b3=QtWidgets.QPushButton("Process"); b3.setStyleSheet("font-weight:bold;min-height:28px")
        b3.clicked.connect(self._go); br.addWidget(b3); l.addLayout(br)
        l.addWidget(QtWidgets.QLabel("Log:"))
        self._lw=QtWidgets.QPlainTextEdit(); self._lw.setReadOnly(True); self._lw.setMaximumBlockCount(200); l.addWidget(self._lw)
        self._rf()
    def _lg(self,m): self._lw.appendPlainText(m)
    def _rf(self):
        self._lst.clear()
        for o in (cmds.ls(sl=True,long=True) or []):
            if _pm(o): self._lst.addItem(QtWidgets.QListWidgetItem(o))
        if self._lst.count()==0: self._lg("[INFO] Select meshes, Refresh")
    def _ps(self):
        pl=[o for o in (cmds.ls(sl=True,long=True) or []) if _pm(o)]
        if not pl: self._lg("[INFO] No polygon"); return
        self._lst.clear()
        for o in pl: self._lst.addItem(QtWidgets.QListWidgetItem(o))
        self._lg("Loaded {}".format(len(pl)))
    def _go(self):
        its=[self._lst.item(i).text() for i in range(self._lst.count())]
        if not its: self._lg("[INFO] Empty"); return
        self._lg("-- {} mesh(es) --".format(len(its))); run(its,log=self._lg); self._rf()

def _pm(n):
    ss=cmds.listRelatives(n,shapes=True,noIntermediate=True) or []
    for s in ss:
        if cmds.nodeType(s)=="mesh": return True
    return False

_g=None
MD=lambda:os.path.join(cmds.internalVar(userAppDir=True),"scripts")

def show():
    global _g
    if _g:
        try: _g.close(); _g.deleteLater()
        except: pass
    _g=Win(); _g.show()

def _menu():
    m="arx_tools_menu"
    if not cmds.menu(m,exists=True): return
    for i in (cmds.menu(m,q=True,ia=True) or []):
        if cmds.menuItem(i,q=True,l=True)=="Normal Tangent Tool": cmds.deleteUI(i)
    cmds.menuItem(label="Normal Tangent Tool",command="import NormalTangentTool; NormalTangentTool.show()",parent=m)

def _sp():
    try: return __file__
    except NameError:
        for p in sys.path:
            c=os.path.join(p,"NormalTangentTool.py")
            if os.path.isfile(c): return c
        return os.path.join(MD(),"NormalTangentTool.py")

def install():
    src=os.path.abspath(_sp()); dst=os.path.join(MD(),"NormalTangentTool.py")
    st=os.path.join(MD(),"userSetup.py")
    en="import NormalTangentTool; maya.utils.executeDeferred(lambda: NormalTangentTool.onMayaDroppedPythonFile(None))\n"
    mk="# NormalTangentTool"
    if os.path.normcase(src)!=os.path.normcase(dst):
        if not os.path.isdir(MD()): os.makedirs(MD())
        with open(src,"rb") as f: d=f.read()
        with open(dst,"wb") as f: f.write(d)
    if os.path.isfile(st):
        with open(st,"r") as f: o=f.read()
        if mk in o: return
        with open(st,"a") as f: f.write("\n"+mk+"\n"+en)
    else:
        with open(st,"w") as f: f.write("# -*- coding: utf-8 -*-\nimport maya.utils\n"+mk+"\n"+en)

def onMayaDroppedPythonFile(_):
    install()
    try: _menu()
    except: pass
    show()

if __name__=="__main__": show()
