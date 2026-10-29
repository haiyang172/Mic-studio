#pragma once
#include <windows.h>
#include <audioenginebaseapo.h>
#include "MicStudioApoParams.h"

// Stable identity, shared by the native DLL and the managed deployment helper.
inline constexpr CLSID CLSID_MicStudioApo =
{ 0x6843d3f0, 0xea67, 0x4b0c, { 0xab, 0x63, 0x90, 0xa2, 0x8c, 0xf4, 0x55, 0x78 } };

struct MicStudioApoStatus
{
    uint32_t SizeBytes;
    uint32_t Initialized;
    uint32_t Locked;
    uint32_t SampleRate;
    uint32_t Channels;
    uint32_t MappedParameters;
    uint64_t ProcessedFrames; // An offline host can call APOProcess too; not audiodg proof.
};

// Non-real-time test/control interface. System deployment uses the endpoint's
// file-backed shared snapshot instead of COM calls from the UI into audiodg.
MIDL_INTERFACE("98A1E0EF-395C-4A63-907C-8F31CAC79663")
IMicStudioApoControl : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE SetParameters(const MicStudioApoParams* parameters) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStatus(MicStudioApoStatus* status) = 0;
};

using MicStudioGetClassObject = HRESULT(WINAPI*)(REFCLSID, REFIID, void**);
using MicStudioGetContract = HRESULT(WINAPI*)(UINT32*, UINT32*, CLSID*);
