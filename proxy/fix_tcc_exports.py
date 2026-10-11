"""Remove TinyCC's stdcall suffixes from a 32-bit DirectInput proxy export table.

The Microsoft dinput8.dll exports plain names even on x86. TinyCC emits
`_Function@N` for WINAPI functions, so patch only the export-name strings in
place. The function RVAs and code remain untouched.
"""
from pathlib import Path
import struct
import sys

def u16(b, n): return struct.unpack_from('<H', b, n)[0]
def u32(b, n): return struct.unpack_from('<I', b, n)[0]

path=Path(sys.argv[1])
data=bytearray(path.read_bytes())
pe=u32(data,0x3c)
assert data[pe:pe+4]==b'PE\0\0'
machine,nsections,_,_,_,optsize,_=struct.unpack_from('<HHIIIHH',data,pe+4)
assert machine==0x14c
opt=pe+24
assert u16(data,opt)==0x10b
export_rva=u32(data,opt+96)
sections=[]
for i in range(nsections):
    section=opt+optsize+i*40
    vsize,va,rsize,rptr=struct.unpack_from('<IIII',data,section+8)
    sections.append((vsize,va,rsize,rptr))
def offset(rva):
    for vsize,va,rsize,rptr in sections:
        if va<=rva<va+max(vsize,rsize): return rptr+rva-va
    return rva
def get_string(rva):
    at=offset(rva)
    end=data.index(0,at)
    return at, bytes(data[at:end])
ed=offset(export_rva)
fields=struct.unpack_from('<IIHHIIIIIII',data,ed)
_,_,_,_,module_rva,base,nfunc,nnames,funcs_rva,names_rva,ords_rva=fields
expected={
    b'DirectInput8Create',b'DllCanUnloadNow',b'DllGetClassObject',
    b'DllRegisterServer',b'DllUnregisterServer',b'GetdfDIJoystick',
}
fixed=set()
for i in range(nnames):
    at,old=get_string(u32(data,offset(names_rva)+4*i))
    if not old.startswith(b'_') or b'@' not in old:
        raise ValueError(f'unexpected TinyCC x86 export: {old!r}')
    new=old[1:old.rindex(b'@')]
    if new not in expected: raise ValueError(f'unexpected DirectInput export: {new!r}')
    data[at:at+len(old)+1]=new+b'\0'*(len(old)+1-len(new))
    fixed.add(new)
if fixed!=expected: raise ValueError('incomplete exports')
module_at,module_old=get_string(module_rva)
module_new=b'dinput8.dll'
assert len(module_old)>=len(module_new)
data[module_at:module_at+len(module_old)+1]=module_new+b'\0'*(len(module_old)+1-len(module_new))
path.write_bytes(data)
print('fixed exports in',path)
