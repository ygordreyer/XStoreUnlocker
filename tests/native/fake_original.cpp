#include <Windows.h>
extern "C" __declspec(dllexport) HRESULT __cdecl QueryApiImpl(const GUID*, const GUID*, void**) {
    return HRESULT_FROM_WIN32(ERROR_CANCELLED);
}
