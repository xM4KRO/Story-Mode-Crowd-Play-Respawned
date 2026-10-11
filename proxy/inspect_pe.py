from pathlib import Path
import struct
import sys

def u16(data, offset): return struct.unpack_from('<H', data, offset)[0]
def u32(data, offset): return struct.unpack_from('<I', data, offset)[0]

def inspect(path):
    data = Path(path).read_bytes()
    pe = u32(data, 0x3c)
    assert data[:2] == b'MZ' and data[pe:pe+4] == b'PE\0\0'
    machine, nsections, _, _, _, optsize, _ = struct.unpack_from('<HHIIIHH',data,pe+4)
    opt = pe+24
    magic = u16(data,opt)
    directories = opt+(112 if magic==0x20b else 96)
    sections=[]
    for i in range(nsections):
        section=opt+optsize+i*40
        name=data[section:section+8].rstrip(b'\0').decode('ascii','replace')
        virtual_size,virtual_address,raw_size,raw_ptr=struct.unpack_from('<IIII',data,section+8)
        sections.append((name,virtual_size,virtual_address,raw_size,raw_ptr))
    def offset(rva):
        for name,virtual_size,virtual_address,raw_size,raw_ptr in sections:
            if virtual_address <= rva < virtual_address+max(virtual_size,raw_size):
                return raw_ptr+rva-virtual_address
        return rva
    def zstring(rva):
        at=offset(rva)
        return data[at:data.index(b'\0',at)].decode('ascii','replace')
    print(path, 'machine',hex(machine),'pe_magic',hex(magic))
    export_rva,export_size=struct.unpack_from('<II',data,directories)
    if not export_rva:
        print(' no exports')
    else:
        ed=offset(export_rva)
        fields=struct.unpack_from('<IIHHIIIIIII',data,ed)
        base,nfunc,nnames,funcs_rva,names_rva,ords_rva=fields[5:]
        print(' module',zstring(fields[4]),'export_count',nnames,'ordinal_base',base)
        for i in range(nnames):
            name_rva=u32(data,offset(names_rva)+i*4)
            index=u16(data,offset(ords_rva)+i*2)
            target_rva=u32(data,offset(funcs_rva)+index*4)
            forwarded = export_rva <= target_rva < export_rva+export_size
            print(' ',base+index,zstring(name_rva), '=> '+zstring(target_rva) if forwarded else '')

    import_rva,import_size=struct.unpack_from('<II',data,directories+8)
    if import_rva:
        print(' imports:')
        descriptor=offset(import_rva)
        while True:
            original,stamp,chain,name_rva,first=struct.unpack_from('<IIIII',data,descriptor)
            if not (original or stamp or chain or name_rva or first): break
            name=zstring(name_rva)
            print('  ',name)
            if name.lower()=='dinput8.dll':
                thunk=offset(original or first)
                step=8 if magic==0x20b else 4
                while True:
                    value=struct.unpack_from('<Q' if step==8 else '<I',data,thunk)[0]
                    if not value: break
                    if value & (1<<(8*step-1)):
                        print('    #'+str(value & 0xffff))
                    else:
                        print('   ',zstring(value+2))
                    thunk+=step
            descriptor+=20

for arg in sys.argv[1:]: inspect(arg)
