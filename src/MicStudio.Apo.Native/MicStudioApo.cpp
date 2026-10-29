#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "MicStudioApoApi.h"
#include "MicStudioApoDsp.h"
#include <propvarutil.h>
#include <atomic>
#include <cwchar>
#include <new>

namespace
{
std::atomic<long> objects{ 0 }, serverLocks{ 0 };
constexpr GUID FloatFormat{ 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
// PKEY_AudioEndpoint_GUID, defined by the Windows SDK mmdeviceapi.h.
constexpr PROPERTYKEY EndpointGuidProperty{ { 0x1da5d803, 0xd492, 0x4edd, { 0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e } }, 4 };
bool PropertySignature(uint32_t signature) noexcept
{
    if (signature == APO_CONNECTION_PROPERTY_SIGNATURE) return true;
#ifdef APO_CONNECTION_PROPERTY_V2_SIGNATURE
    if (signature == APO_CONNECTION_PROPERTY_V2_SIGNATURE) return true;
#endif
    return false;
}
static_assert(sizeof(void*) == 8, "Only the reviewed Windows x64 APO is supported");
static_assert(std::atomic<uint32_t>::is_always_lock_free, "RT atomics must be lock-free");
static_assert(std::atomic<uint64_t>::is_always_lock_free, "RT counters must be lock-free");

bool Format(IAudioMediaType* media, uint32_t& rate, uint32_t& channels) noexcept
{
    if (!media) return false;
    UNCOMPRESSEDAUDIOFORMAT f{};
    if (FAILED(media->GetUncompressedAudioFormat(&f)) || !IsEqualGUID(f.guidFormatType, FloatFormat) ||
        f.dwBytesPerSampleContainer != 4 || f.dwValidBitsPerSample != 32 ||
        f.dwSamplesPerFrame < 1 || f.dwSamplesPerFrame > 8 || !std::isfinite(f.fFramesPerSecond) ||
        f.fFramesPerSecond < 8000 || f.fFramesPerSecond > 384000 || std::floor(f.fFramesPerSecond) != f.fFramesPerSecond) return false;
    rate = static_cast<uint32_t>(f.fFramesPerSecond); channels = f.dwSamplesPerFrame; return true;
}

class Apo final : public IAudioProcessingObject, public IAudioProcessingObjectConfiguration,
    public IAudioProcessingObjectRT, public IAudioSystemEffects, public IMicStudioApoControl
{
    std::atomic<ULONG> refs_{ 1 };
    std::atomic<bool> initialized_{ false }, locked_{ false };
    std::atomic<bool> resetRequested_{ false };
    std::atomic<uint64_t> processedFrames_{ 0 };
    std::array<std::atomic<uint32_t>, sizeof(MicStudioApoParams) / 4> local_{};
    SRWLOCK localWriter_ = SRWLOCK_INIT; // SetParameters only. Never acquired by APOProcess.
    HANDLE file_ = INVALID_HANDLE_VALUE, mapping_ = nullptr;
    const volatile uint32_t* mapped_ = nullptr;
    std::atomic<uint32_t> rate_{ 0 }, channels_{ 0 }, maxFrames_{ 0 };
    micstudio::Dsp dsp_;

    void CloseMapping() noexcept
    {
        if (mapped_) UnmapViewOfFile(const_cast<uint32_t*>(mapped_));
        if (mapping_) CloseHandle(mapping_);
        if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
        mapped_ = nullptr; mapping_ = nullptr; file_ = INVALID_HANDLE_VALUE;
    }
    void MapParameters(IPropertyStore* endpoint) noexcept
    {
        if (!endpoint) return;
        PROPVARIANT value{};
        if (FAILED(endpoint->GetValue(EndpointGuidProperty, &value))) return;
        GUID guid{};
        const bool valid = value.vt == VT_LPWSTR && value.pwszVal && SUCCEEDED(CLSIDFromString(value.pwszVal, &guid));
        PropVariantClear(&value);
        if (!valid) return;
        wchar_t root[MAX_PATH]{}, id[40]{}, path[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"ProgramData", root, MAX_PATH);
        if (length == 0 || length >= MAX_PATH || StringFromGUID2(guid, id, 40) != 39) return;
        id[37] = L'\0'; // GUID D format, excluding the surrounding braces.
        if (swprintf_s(path, L"%s\\MicStudio\\APO\\Parameters\\%s.bin", root, id + 1) < 0) return;
        file_ = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file_ == INVALID_HANDLE_VALUE) return;
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file_, &size) || size.QuadPart < sizeof(MicStudioApoParams)) { CloseMapping(); return; }
        mapping_ = CreateFileMappingW(file_, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!mapping_) { CloseMapping(); return; }
        mapped_ = static_cast<const volatile uint32_t*>(MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, sizeof(MicStudioApoParams)));
        if (!mapped_) CloseMapping();
    }
    bool Snapshot(MicStudioApoParams& snapshot) noexcept
    {
        uint32_t words[sizeof(MicStudioApoParams) / 4]{};
        uint32_t before, after;
        if (mapped_)
        {
            before = mapped_[2];
            if (before & 1u) return false;
            MemoryBarrier();
            for (size_t i = 0; i < local_.size(); ++i) words[i] = mapped_[i];
            MemoryBarrier(); after = mapped_[2];
        }
        else
        {
            before = local_[2].load(std::memory_order_acquire);
            if (before & 1u) return false;
            for (size_t i = 0; i < local_.size(); ++i) words[i] = local_[i].load(std::memory_order_relaxed);
            std::atomic_thread_fence(std::memory_order_acquire);
            after = local_[2].load(std::memory_order_acquire);
        }
        if (before != after || (after & 1u) || words[2] != before) return false;
        std::memcpy(&snapshot, words, sizeof(snapshot)); return true;
    }
    HRESULT Supported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) noexcept
    {
        if (!supported) return E_POINTER;
        *supported = nullptr;
        uint32_t rate, channels, otherRate, otherChannels;
        if (!Format(requested, rate, channels) || (opposite && (!Format(opposite, otherRate, otherChannels) ||
            rate != otherRate || channels != otherChannels))) return APOERR_FORMAT_NOT_SUPPORTED;
        *supported = requested; requested->AddRef(); return S_OK;
    }
public:
    Apo() noexcept
    {
        ++objects;
        const auto defaults = MicStudioDefaultParams();
        uint32_t words[sizeof(defaults) / 4]; std::memcpy(words, &defaults, sizeof(defaults));
        for (size_t i = 0; i < local_.size(); ++i) local_[i].store(words[i], std::memory_order_relaxed);
    }
    ~Apo() { CloseMapping(); --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IAudioProcessingObject)) *result = static_cast<IAudioProcessingObject*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectRT)) *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == __uuidof(IAudioSystemEffects)) *result = static_cast<IAudioSystemEffects*>(this);
        else if (iid == __uuidof(IMicStudioApoControl)) *result = static_cast<IMicStudioApoControl*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { const auto left = --refs_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Reset() override
    {
        if (!initialized_.load()) return APOERR_NOT_INITIALIZED;
        resetRequested_.store(true, std::memory_order_release); processedFrames_ = 0; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* time) override { if (!time) return E_POINTER; *time = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** properties) override
    {
        if (!properties) return E_POINTER;
        *properties = nullptr;
        constexpr size_t size = sizeof(APO_REG_PROPERTIES) + 3 * sizeof(IID);
        auto p = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(size));
        if (!p) return E_OUTOFMEMORY;
        std::memset(p, 0, size); p->clsid = CLSID_MicStudioApo;
        p->Flags = static_cast<APO_FLAG>(APO_FLAG_DEFAULT | APO_FLAG_INPLACE);
        wcscpy_s(p->szFriendlyName, L"Mic Studio Capture APO");
        wcscpy_s(p->szCopyrightInfo, L"Mic Studio contributors");
        p->u32MajorVersion = 1; p->u32MinorVersion = 0;
        p->u32MinInputConnections = p->u32MaxInputConnections = p->u32MinOutputConnections = p->u32MaxOutputConnections = 1;
        p->u32MaxInstances = UINT32_MAX; p->u32NumAPOInterfaces = 4;
        p->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);
        p->iidAPOInterfaceList[1] = __uuidof(IAudioProcessingObjectConfiguration);
        p->iidAPOInterfaceList[2] = __uuidof(IAudioProcessingObjectRT);
        p->iidAPOInterfaceList[3] = __uuidof(IAudioSystemEffects);
        *properties = p; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size, BYTE* data) override
    {
        if (initialized_.load()) return APOERR_ALREADY_INITIALIZED;
        if (size == 0 && !data) { initialized_ = true; return S_OK; } // Offline host.
        if (!data || size < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
        const auto base = reinterpret_cast<const APOInitBaseStruct*>(data);
        if (base->cbSize != size) return E_INVALIDARG;
        if (base->clsid != CLSID_MicStudioApo) return APOERR_INVALID_APO_CLSID;
        if (size == sizeof(APOInitSystemEffects2))
        {
            const auto init = reinterpret_cast<const APOInitSystemEffects2*>(data);
            if (!init->InitializeForDiscoveryOnly) MapParameters(init->pAPOEndpointProperties);
        }
        else if (size == sizeof(APOInitSystemEffects)) MapParameters(reinterpret_cast<const APOInitSystemEffects*>(data)->pAPOEndpointProperties);
        else if (size != sizeof(APOInitBaseStruct)) return E_INVALIDARG;
        initialized_ = true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override
    { return Supported(opposite, requested, supported); }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override
    { return Supported(opposite, requested, supported); }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* channels) override
    { if (!channels) return E_POINTER; if (!locked_.load()) return APOERR_NOT_INITIALIZED; *channels = channels_; return S_OK; }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in, UINT32 outputs, APO_CONNECTION_DESCRIPTOR** out) override
    {
        if (!initialized_.load()) return APOERR_NOT_INITIALIZED;
        if (locked_.load()) return APOERR_APO_LOCKED;
        if (inputs != 1 || outputs != 1) return APOERR_NUM_CONNECTIONS_INVALID;
        if (!in || !out || !in[0] || !out[0]) return E_POINTER;
        if (in[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE || out[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE) return E_INVALIDARG;
        uint32_t rate, channels, outputRate, outputChannels;
        if (!Format(in[0]->pFormat, rate, channels) || !Format(out[0]->pFormat, outputRate, outputChannels) ||
            rate != outputRate || channels != outputChannels) return APOERR_INVALID_CONNECTION_FORMAT;
        if (!in[0]->u32MaxFrameCount || in[0]->u32MaxFrameCount > 384000 || out[0]->u32MaxFrameCount < in[0]->u32MaxFrameCount)
            return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
        rate_ = rate; channels_ = channels; maxFrames_ = in[0]->u32MaxFrameCount;
        dsp_.Configure(rate_, channels_); resetRequested_ = false; processedFrames_ = 0; locked_.store(true, std::memory_order_release); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override
    {
        if (!locked_.exchange(false)) return APOERR_ALREADY_UNLOCKED;
        dsp_.Reset(); resetRequested_ = false; return S_OK;
    }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** in, UINT32 outputs, APO_CONNECTION_PROPERTY** out) override
    {
        if (outputs != 1 || !out || !out[0]) return;
        // Snapshot metadata first: an in-place offline host may alias both the
        // connection property and the actual buffer, not just the latter.
        APO_CONNECTION_PROPERTY inputCopy{};
        if (inputs == 1 && in && in[0]) inputCopy = *in[0];
        auto output = out[0]; output->u32ValidFrameCount = 0; output->u32BufferFlags = BUFFER_INVALID;
        if (!locked_.load(std::memory_order_acquire) || inputs != 1 || !in || !in[0]) return;
        const auto input = &inputCopy;
        if (!PropertySignature(input->u32Signature) || !PropertySignature(output->u32Signature) ||
            input->u32ValidFrameCount > maxFrames_ || input->u32BufferFlags == BUFFER_INVALID ||
            (input->u32BufferFlags != BUFFER_VALID && input->u32BufferFlags != BUFFER_SILENT) ||
            !output->pBuffer || (output->pBuffer % alignof(float)) != 0 ||
            (input->u32BufferFlags != BUFFER_SILENT && (!input->pBuffer || (input->pBuffer % alignof(float)) != 0))) return;
        MicStudioApoParams p{};
        if (resetRequested_.exchange(false, std::memory_order_acquire)) dsp_.Reset();
        if (Snapshot(p))
        {
            const auto now = GetTickCount64();
            const bool lease = p.OwnerProcessId != 0 && p.HeartbeatTickMs != 0 && now >= p.HeartbeatTickMs && now - p.HeartbeatTickMs <= 3000;
            if (p.Enabled == 0 || lease) dsp_.Adopt(p); else dsp_.Invalidate();
        }
        else dsp_.Invalidate();
        const bool silent = input->u32BufferFlags == BUFFER_SILENT;
        dsp_.Process(reinterpret_cast<const float*>(input->pBuffer), reinterpret_cast<float*>(output->pBuffer), input->u32ValidFrameCount, silent);
        output->u32ValidFrameCount = input->u32ValidFrameCount;
        output->u32BufferFlags = silent && !dsp_.Active() ? BUFFER_SILENT : BUFFER_VALID;
        processedFrames_.fetch_add(input->u32ValidFrameCount, std::memory_order_relaxed);
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 frames) override { return frames; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 frames) override { return frames; }
    HRESULT STDMETHODCALLTYPE SetParameters(const MicStudioApoParams* parameters) override
    {
        if (!initialized_.load()) return APOERR_NOT_INITIALIZED;
        if (!parameters) return E_POINTER;
        if (!micstudio::ValidParams(*parameters, parameters->SampleRate ? parameters->SampleRate : 48000,
            parameters->Channels ? parameters->Channels : 1)) return E_INVALIDARG;
        if (mapped_) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        uint32_t words[sizeof(MicStudioApoParams) / 4]; std::memcpy(words, parameters, sizeof(words));
        AcquireSRWLockExclusive(&localWriter_);
        const auto odd = (local_[2].load(std::memory_order_relaxed) + 1u) | 1u;
        local_[2].store(odd, std::memory_order_release);
        for (size_t i = 0; i < local_.size(); ++i) if (i != 2) local_[i].store(words[i], std::memory_order_relaxed);
        local_[2].store(odd + 1u, std::memory_order_release);
        ReleaseSRWLockExclusive(&localWriter_); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetStatus(MicStudioApoStatus* status) override
    {
        if (!status || status->SizeBytes != sizeof(*status)) return E_INVALIDARG;
        *status = { sizeof(*status), initialized_.load() ? 1u : 0u, locked_.load() ? 1u : 0u,
            rate_, channels_, mapped_ ? 1u : 0u, processedFrames_.load() }; return S_OK;
    }
};

class Factory final : public IClassFactory
{
    std::atomic<ULONG> refs_{ 1 };
public:
    Factory() { ++objects; } ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        if (!result) return E_POINTER; *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { const auto n = --refs_; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override
    {
        if (!result) return E_POINTER; *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto apo = new (std::nothrow) Apo(); if (!apo) return E_OUTOFMEMORY;
        const auto hr = apo->QueryInterface(iid, result); apo->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) ++serverLocks;
        else { long value = serverLocks.load(); while (value > 0 && !serverLocks.compare_exchange_weak(value, value - 1)) { } }
        return S_OK;
    }
};
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    if (!result) return E_POINTER; *result = nullptr;
    if (clsid != CLSID_MicStudioApo) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    const auto hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return objects.load() == 0 && serverLocks.load() == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
