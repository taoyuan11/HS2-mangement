"""Exercise native solver ownership, constraints, reset and malformed-input rejection."""
import argparse
import ctypes as ct
import json
import os
from pathlib import Path

import numpy as np


def run(directory):
    directory = directory.resolve()
    dll_search = os.add_dll_directory(str(directory))
    # PhysX 3.4 uses legacy delay-load calls, so bind its owned dependencies by
    # absolute path before invoking the solver instead of changing process PATH.
    dependencies = [ct.CDLL(str(directory / name)) for name in
                    ['PxFoundation_x64.dll', 'PhysX3Common_x64.dll', 'PxPvdSDK_x64.dll', 'PhysX3_x64.dll']]
    dll = ct.CDLL(str(directory / 'HS2Manager.Physics.dll'))
    pointer, floats, uints = ct.c_void_p, ct.POINTER(ct.c_float), ct.POINTER(ct.c_uint)
    signatures = {
        'create': (pointer, []), 'destroy': (None, [pointer]), 'error': (ct.c_char_p, []),
        'version': (ct.c_uint, []),
        'add': (ct.c_int, [pointer, ct.c_int, floats, ct.c_int, uints, floats, floats, floats]),
        'update': (ct.c_int, [pointer, ct.c_int, floats, floats, floats, floats, floats, ct.c_int]),
        'step': (ct.c_int, [pointer, ct.c_float]), 'read': (ct.c_int, [pointer, ct.c_int, ct.c_int, floats]),
    }
    for name, (result, arguments) in signatures.items():
        function = getattr(dll, 'hs2_cloth_' + name)
        function.restype, function.argtypes = result, arguments
    def checked(name, *arguments):
        value = getattr(dll, 'hs2_cloth_' + name)(*arguments)
        if value < 0:
            raise RuntimeError(dll.hs2_cloth_error().decode('utf-8'))
        return value
    f = lambda values: (ct.c_float * len(values))(*values)
    u = lambda values: (ct.c_uint * len(values))(*values)
    assert dll.hs2_cloth_version() == 0x03040200
    vertices = f([0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1])
    transform = f([0, 0, 0, 0, 0, 0, 1])
    for repetition in range(3):
        scene = dll.hs2_cloth_create()
        if not scene:
            raise RuntimeError(dll.hs2_cloth_error().decode('utf-8'))
        try:
            index = checked('add', scene, 4, vertices, 6, u([0, 2, 1, 1, 2, 3]),
                            f([0, 0, 5, 5]), f([1, 1, .1, .5, 0, 1, 120, 10, 1, .1, 0, 1, .5, 1, 1]), transform)
            for _ in range(30):
                checked('step', scene, 1 / 30)
            output = f([0] * 12)
            checked('read', scene, index, 4, output)
            simulated = np.array(output).reshape(4, 3)
            assert np.isfinite(simulated).all()
            assert np.max(abs(simulated[:2] - [[0, 1, 0], [1, 1, 0]])) < 1e-5
            assert simulated[2, 1] < .9
            checked('update', scene, index, vertices, None, None, transform, f([0, 0, 0]), 1)
            checked('read', scene, index, 4, output)
            assert np.max(abs(np.array(output) - np.array(vertices))) < 1e-5
            assert dll.hs2_cloth_step(scene, float('nan')) == -1
            assert dll.hs2_cloth_read(scene, index, 3, output) == -1
        finally:
            dll.hs2_cloth_destroy(scene)
    dll_search.close()
    return {'solver': 'PhysX 3.4.2', 'lifecycleCycles': 3, 'constraintsAndReset': True,
            'characterParityVerified': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    arguments = parser.parse_args()
    print(json.dumps(run(arguments.directory)))
