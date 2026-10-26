# Scan all of .text for "mov word ptr [..], 0xXX04" opcode stores (04 XX little-endian = command prefix 4 + opcode)
# and "mov byte ptr [..], 0xXX" right after, grouped by enclosing function. Usage: opscan.py exe
import sys, pefile, capstone, re, collections
pe = pefile.PE(sys.argv[1]); base = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
text = next(s for s in pe.sections if s.Name.startswith(b'.text'))
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
def fstart(o):
    while o > 0 and not (img[o-1] in (0xcc, 0xc3) and img[o:o+3] == b'\x55\x8b\xec'):
        o -= 1
    return o + base
byop = collections.defaultdict(list)
for i in md.disasm(img[text.VirtualAddress: text.VirtualAddress + text.Misc_VirtualSize], base + text.VirtualAddress):
    if i.mnemonic == 'mov' and i.op_str.startswith('word ptr') and re.search(r', 0x[0-9a-f]{1,2}04$', i.op_str):
        op = int(i.op_str.split(', 0x')[1], 16) >> 8
        byop[op].append((i.address, fstart(i.address - base)))
for op in sorted(byop):
    funcs = sorted({f for _, f in byop[op]})
    print(f"op {op:02x}: {len(byop[op]):3} stores in {len(funcs):2} funcs: {' '.join(f'{f:x}' for f in funcs)}")
