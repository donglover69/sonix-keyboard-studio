# Compare functions between two builds: fdiff.py exeA exeB vaA vaB [vaA vaB ...]
# Disassembles each function (until the first int3-padded ret), normalizes addresses, and reports differences.
import sys, re, pefile, capstone, difflib
def load(p):
    pe = pefile.PE(p); return pe.OPTIONAL_HEADER.ImageBase, pe.get_memory_mapped_image()
def func(base, img, va):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32); out = []
    for i in md.disasm(img[va - base: va - base + 0x4000], va):
        s = f"{i.mnemonic} {i.op_str}"
        s = re.sub(r'0x[0-9a-f]{6,}', 'ADDR', s)          # absolute addresses / rel32 targets
        s = re.sub(r'\[(e[a-z]{2}) \+ 0x[0-9a-f]{6,}\]', r'[\1 + ADDR]', s)
        out.append(s)
        if i.mnemonic == 'ret' and i.address > va + 0x20 and img[i.address - base + (3 if i.op_str else 1)] == 0xcc:
            break
    return out
ba, ia = load(sys.argv[1]); bb, ib = load(sys.argv[2])
pairs = sys.argv[3:]
for x, y in zip(pairs[::2], pairs[1::2]):
    fa = func(ba, ia, int(x, 16)); fb = func(bb, ib, int(y, 16))
    d = [l for l in difflib.unified_diff(fa, fb, lineterm='', n=0) if l[:1] in '+-' and not l.startswith(('+++', '---'))]
    print(f"{x} ({len(fa)} insns) vs {y} ({len(fb)} insns): {'IDENTICAL' if not d else f'{len(d)} diff lines'}")
    for l in d[:30]: print("   ", l)
