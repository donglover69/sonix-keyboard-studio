# Finds functions of build A in build B by their first bytes (masking rel32/abs32 operands): sigfind.py exeA exeB vaA [vaA ...]
import sys, pefile, capstone
def load(p):
    pe = pefile.PE(p); return pe.OPTIONAL_HEADER.ImageBase, pe.get_memory_mapped_image(), pe
ba, ia, pa = load(sys.argv[1]); bb, ib, pb = load(sys.argv[2])
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32); md.detail = True
text_b = next(s for s in pb.sections if s.Name.startswith(b'.text'))
lo, hi = text_b.VirtualAddress, text_b.VirtualAddress + text_b.Misc_VirtualSize
def masked(img, base, va, n=48):
    out = []
    for ins in md.disasm(img[va - base: va - base + 200], va):
        b = bytearray(ins.bytes)
        if ins.mnemonic in ('call', 'jmp') and len(b) == 5: b[1:] = b'\x00' * 4
        if len(b) >= 5 and any(op.type in (2, 3) and (op.type == 2 and op.imm > 0x400000 or op.type == 3 and op.mem.disp > 0x400000) for op in ins.operands):
            b[-4:] = b'\x00' * 4
        out.append(bytes(b))
        if sum(map(len, out)) >= n: break
    return out
for a in sys.argv[3:]:
    va = int(a, 16); sig = masked(ia, ba, va); total = sum(map(len, sig))
    hits = []
    first = sig[0]
    o = ib.find(first, lo, hi)
    while o != -1 and len(hits) < 5:
        if b''.join(masked(ib, bb, bb + o)) [:total] == b''.join(sig)[:total]: hits.append(hex(bb + o))
        o = ib.find(first, o + 1, hi)
    print(a, '->', hits)
