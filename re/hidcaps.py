import ctypes, ctypes.wintypes as w, hid
k32 = ctypes.WinDLL('kernel32', use_last_error=True); hidd = ctypes.WinDLL('hid')
k32.CreateFileW.restype = w.HANDLE
class CAPS(ctypes.Structure):
    _fields_ = [('Usage', w.USHORT), ('UsagePage', w.USHORT), ('In', w.USHORT), ('Out', w.USHORT), ('Feat', w.USHORT), ('r', w.USHORT * 17), ('rest', w.USHORT * 10)]
for d in hid.enumerate(0x0c45) + hid.enumerate(0x05ac):  # keyboard (SONiX) + F108Pro dongle (Apple VID)
    h = k32.CreateFileW(d['path'].decode(), 0, 3, None, 3, 0, None)
    pp = ctypes.c_void_p()
    if hidd.HidD_GetPreparsedData(w.HANDLE(h), ctypes.byref(pp)):
        c = CAPS(); hidd.HidP_GetCaps(pp, ctypes.byref(c)); hidd.HidD_FreePreparsedData(pp)
        print(hex(d['product_id']), 'MI', d['interface_number'], 'page', hex(c.UsagePage), 'usage', hex(c.Usage), 'in', c.In, 'out', c.Out, 'feat', c.Feat)
    k32.CloseHandle(w.HANDLE(h))
