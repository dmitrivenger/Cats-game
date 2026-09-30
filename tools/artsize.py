import os,shutil,sys
from PIL import Image
prefixes=('Assets/Sprites/','Assets/ui/','Assets/Combos/','Assets/Blockers/','Assets/Level-menu/')
BIG=('splash.png','bg_bokeh.png','popup_panel.png')
KEEP1024=('assets/ui/win.png','assets/ui/lose.png','assets/ui/logo.png','assets/ui/panel.png','assets/ui/play_big.png','assets/ui/play_big_pressed.png',
          'assets/level-menu/map_path.png')
def maxsize(p):
    n=p.lower()
    if n.endswith(BIG) or 'win_cats' in n or 'lose_cats' in n: return 2048
    if '/fx/' in n or 'confetti' in n or 'combo_bit' in n or 'lose_drop' in n or 'lose_splash' in n: return 512
    if n in KEEP1024 or n.startswith('assets/ui/result/') or n.startswith('assets/combos/soft/') or n.startswith('assets/sprites/background/'): return 1024
    if n.startswith('assets/combos/layers/') and not ('rocket' in n or 'star' in n): return 1024
    return 512   # pieces, blockers, power-ups, icons, map bits: shown at ~1-2 cells
if __name__=='__main__':
    apply='--apply' in sys.argv
    plan=[]
    for root,_,fs in os.walk('Assets'):
        for f in fs:
            p=os.path.join(root,f)
            if not f.lower().endswith('.png') or not p.startswith(prefixes): continue
            w,h=Image.open(p).size; M=maxsize(p)
            s=min(1.0,M/max(w,h)); nw,nh=round(w*s),round(h*s)
            tw,th=(nw+3)//4*4,(nh+3)//4*4
            if (tw,th)!=(w,h): plan.append((p,w,h,nw,nh,tw,th))
    print(len(plan))
    if apply:
        for p,w,h,nw,nh,tw,th in plan:
            bk=os.path.join('_art_originals',p); os.makedirs(os.path.dirname(bk),exist_ok=True)
            if not os.path.exists(bk): shutil.copy2(p,bk)
            im=Image.open(p).convert('RGBA')
            if (nw,nh)!=(w,h): im=im.resize((nw,nh),Image.LANCZOS)
            out=Image.new('RGBA',(tw,th),(0,0,0,0)); out.paste(im,((tw-nw)//2,(th-nh)//2)); out.save(p,optimize=True)
        print('applied')
