// Offline COM/DSP host only. No registration, endpoint changes or audio devices.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "MicStudioApoApi.h"
#include "MicStudioApoDsp.h"
#include <atomic>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <propvarutil.h>

namespace
{
int passed = 0;
void Check(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void Test(const char* name, const std::function<void()>& body)
{ body(); ++passed; std::printf("PASS %s\n", name); }
template<class T> struct Com
{
    T* p = nullptr;
    ~Com() { if (p) p->Release(); }
    T* operator->() const { return p; }
    T** Put() { if (p) p->Release(); p = nullptr; return &p; }
};
constexpr GUID FloatFormat{ 3, 0, 0x10, { 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71 } };
constexpr PROPERTYKEY EndpointGuidKey{ { 0x1da5d803, 0xd492, 0x4edd, { 0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e } }, 4 };

class Media final : public IAudioMediaType
{
    std::atomic<ULONG> refs_{ 1 };
    UNCOMPRESSEDAUDIOFORMAT f_{};
    WAVEFORMATEX w_{};
public:
    Media(uint32_t rate, uint32_t channels, bool floating = true)
    {
        f_ = { FloatFormat, channels, floating ? 4u : 2u, floating ? 32u : 16u, static_cast<float>(rate), 0 };
        if (!floating) f_.guidFormatType.Data1 = 1;
        w_.wFormatTag = floating ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM;
        w_.nSamplesPerSec = rate; w_.nChannels = static_cast<WORD>(channels); w_.wBitsPerSample = floating ? 32 : 16;
        w_.nBlockAlign = static_cast<WORD>(channels * w_.wBitsPerSample / 8); w_.nAvgBytesPerSec = rate * w_.nBlockAlign;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override
    {
        if (!out) return E_POINTER; *out = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IAudioMediaType)) return E_NOINTERFACE;
        *out = static_cast<IAudioMediaType*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { auto n = --refs_; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* result) override { if (!result) return E_POINTER; *result = FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* other, DWORD* flags) override
    { if (!other || !flags) return E_POINTER; UNCOMPRESSEDAUDIOFORMAT f{}; other->GetUncompressedAudioFormat(&f); *flags = 0; return std::memcmp(&f, &f_, sizeof(f)) == 0 ? S_OK : S_FALSE; }
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override { return &w_; }
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* result) override
    { if (!result) return E_POINTER; *result = f_; return S_OK; }
};

class Properties final : public IPropertyStore
{
    std::atomic<ULONG> refs_{ 1 };
    std::wstring guid_;
public:
    explicit Properties(std::wstring guid) : guid_(std::move(guid)) { }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override
    { if (!out) return E_POINTER; *out = nullptr; if (iid != __uuidof(IUnknown) && iid != __uuidof(IPropertyStore)) return E_NOINTERFACE; *out = this; AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { auto n = --refs_; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* count) override { if (!count) return E_POINTER; *count = 1; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAt(DWORD index, PROPERTYKEY* key) override { if (!key) return E_POINTER; if (index != 0) return E_INVALIDARG; *key = EndpointGuidKey; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetValue(REFPROPERTYKEY key, PROPVARIANT* value) override
    {
        if (!value) return E_POINTER; PropVariantInit(value);
        if (key.fmtid != EndpointGuidKey.fmtid || key.pid != EndpointGuidKey.pid) return S_OK;
        value->pwszVal = static_cast<wchar_t*>(CoTaskMemAlloc((guid_.size() + 1) * sizeof(wchar_t)));
        if (!value->pwszVal) return E_OUTOFMEMORY;
        std::memcpy(value->pwszVal, guid_.c_str(), (guid_.size() + 1) * sizeof(wchar_t)); value->vt = VT_LPWSTR; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetValue(REFPROPERTYKEY, REFPROPVARIANT) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE Commit() override { return E_ACCESSDENIED; }
};

struct Host
{
    Com<IAudioProcessingObject> apo;
    Com<IAudioProcessingObjectConfiguration> config;
    Com<IAudioProcessingObjectRT> rt;
    Com<IMicStudioApoControl> control;
    explicit Host(IClassFactory* factory, bool initialize = true)
    {
        Check(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(apo.Put()))), "CreateInstance");
        Check(SUCCEEDED(apo->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration), reinterpret_cast<void**>(config.Put()))), "configuration interface");
        Check(SUCCEEDED(apo->QueryInterface(__uuidof(IAudioProcessingObjectRT), reinterpret_cast<void**>(rt.Put()))), "RT interface");
        Check(SUCCEEDED(apo->QueryInterface(__uuidof(IMicStudioApoControl), reinterpret_cast<void**>(control.Put()))), "control interface");
        if (initialize) Check(SUCCEEDED(apo->Initialize(0, nullptr)), "offline Initialize");
    }
    HRESULT Lock(uint32_t rate = 48000, uint32_t channels = 1, uint32_t frames = 4096)
    {
        Com<IAudioMediaType> input, output; input.p = new Media(rate, channels); output.p = new Media(rate, channels);
        APO_CONNECTION_DESCRIPTOR a{ APO_CONNECTION_BUFFER_TYPE_EXTERNAL, 0, frames, input.p, APO_CONNECTION_DESCRIPTOR_SIGNATURE };
        APO_CONNECTION_DESCRIPTOR b{ APO_CONNECTION_BUFFER_TYPE_EXTERNAL, 0, frames, output.p, APO_CONNECTION_DESCRIPTOR_SIGNATURE };
        auto ap = &a, bp = &b; return config->LockForProcess(1, &ap, 1, &bp);
    }
    void Parameters(MicStudioApoParams params)
    {
        params.HeartbeatTickMs = GetTickCount64(); params.OwnerProcessId = GetCurrentProcessId();
        Check(SUCCEEDED(control->SetParameters(&params)), "valid parameters rejected");
    }
    void Process(const float* input, float* output, uint32_t frames, APO_BUFFER_FLAGS flags = BUFFER_VALID)
    {
        APO_CONNECTION_PROPERTY a{ reinterpret_cast<UINT_PTR>(input), frames, flags, APO_CONNECTION_PROPERTY_SIGNATURE };
        APO_CONNECTION_PROPERTY b{ reinterpret_cast<UINT_PTR>(output), 0, BUFFER_INVALID, APO_CONNECTION_PROPERTY_SIGNATURE };
        auto ap = &a, bp = &b; rt->APOProcess(1, &ap, 1, &bp);
        Check(b.u32ValidFrameCount == frames && b.u32BufferFlags != BUFFER_INVALID, "APOProcess rejected valid buffer");
    }
};

MicStudioApoParams Flat()
{
    auto p = MicStudioDefaultParams(); p.Enabled = 1; p.EqEnabled = 0; p.LimiterEnabled = 0;
    p.HeartbeatTickMs = GetTickCount64(); p.OwnerProcessId = GetCurrentProcessId(); return p;
}
std::vector<float> Tone(uint32_t rate = 48000, uint32_t channels = 1, double amplitude = .1)
{
    std::vector<float> v(static_cast<size_t>(rate) * channels);
    for (uint32_t i = 0; i < rate; ++i) for (uint32_t c = 0; c < channels; ++c)
        v[static_cast<size_t>(i) * channels + c] = static_cast<float>(amplitude * std::sin(2 * micstudio::Pi * 1000 * i / rate));
    return v;
}
double Rms(const std::vector<float>& samples)
{
    double power = 0; size_t count = 0;
    for (size_t i = samples.size() / 2; i < samples.size(); ++i) { power += samples[i] * samples[i]; ++count; }
    return std::sqrt(power / count);
}
double Measure(Host& host, const std::vector<float>& source)
{
    std::vector<float> output(source.size());
    for (size_t offset = 0; offset < source.size();)
    {
        const auto frames = static_cast<uint32_t>(std::min<size_t>(1024, source.size() - offset));
        host.Process(source.data() + offset, output.data() + offset, frames); offset += frames;
    }
    return 20 * std::log10(Rms(output) / Rms(source));
}

// The test process overrides its own ProgramData environment only. Files live
// beneath a freshly generated temporary directory, never the real system path.
struct MappingSandbox
{
    std::filesystem::path root, filePath;
    std::wstring oldRoot, guidText;
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    uint32_t* view = nullptr;
    uint32_t sequence = 0;
    MappingSandbox()
    {
        wchar_t old[MAX_PATH]{}, temporary[MAX_PATH]{}, guid[40]{};
        GetEnvironmentVariableW(L"ProgramData", old, MAX_PATH); oldRoot = old;
        Check(GetTempPathW(MAX_PATH, temporary) != 0, "temporary path");
        GUID id{}; Check(SUCCEEDED(CoCreateGuid(&id)) && StringFromGUID2(id, guid, 40) == 39, "temporary GUID");
        guidText = guid; auto bare = guidText.substr(1, 36);
        root = std::filesystem::path(temporary) / (L"MicStudioApoOffline-" + bare);
        filePath = root / L"MicStudio" / L"APO" / L"Parameters" / (bare + L".bin");
        std::filesystem::create_directories(filePath.parent_path());
        file = CreateFileW(filePath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr); Check(file != INVALID_HANDLE_VALUE, "temporary parameter file");
        mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, sizeof(MicStudioApoParams), nullptr); Check(mapping != nullptr, "temporary mapping");
        view = static_cast<uint32_t*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(MicStudioApoParams))); Check(view != nullptr, "temporary view");
        Publish(MicStudioDefaultParams());
        Check(SetEnvironmentVariableW(L"ProgramData", root.c_str()) != FALSE, "isolated ProgramData environment");
    }
    ~MappingSandbox()
    {
        SetEnvironmentVariableW(L"ProgramData", oldRoot.empty() ? nullptr : oldRoot.c_str());
        if (view) UnmapViewOfFile(view);
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        std::error_code ignored;
        std::filesystem::remove(filePath, ignored);
        std::filesystem::remove(filePath.parent_path(), ignored);
        std::filesystem::remove(filePath.parent_path().parent_path(), ignored);
        std::filesystem::remove(root / L"MicStudio", ignored);
        std::filesystem::remove(root, ignored); // Only empty owned directories.
    }
    void Publish(MicStudioApoParams p, bool leaveOdd = false)
    {
        uint32_t words[sizeof(p) / 4]; std::memcpy(words, &p, sizeof(p));
        sequence = (sequence + 1u) | 1u;
        InterlockedExchange(reinterpret_cast<volatile LONG*>(view + 2), static_cast<LONG>(sequence));
        MemoryBarrier();
        for (size_t i = 0; i < sizeof(p) / 4; ++i) if (i != 2) view[i] = words[i];
        MemoryBarrier();
        if (!leaveOdd) { ++sequence; InterlockedExchange(reinterpret_cast<volatile LONG*>(view + 2), static_cast<LONG>(sequence)); }
    }
    void Initialize(Host& h)
    {
        Com<IPropertyStore> properties; properties.p = new Properties(guidText);
        APOInitSystemEffects init{}; init.APOInit = { sizeof(init), CLSID_MicStudioApo }; init.pAPOEndpointProperties = properties.p;
        Check(SUCCEEDED(h.apo->Initialize(sizeof(init), reinterpret_cast<BYTE*>(&init))), "system-effects initialization with fake endpoint");
    }
};

void Run(IClassFactory* factory)
{
    Test("Wire ABI is fixed and new parameters are disabled", [] {
        const auto p = MicStudioDefaultParams(); Check(sizeof(p) == 224 && offsetof(MicStudioApoParams, Bands) == 48 &&
            offsetof(MicStudioApoParams, HeartbeatTickMs) == 208 && !p.Enabled && p.Version == 2, "ABI mismatch");
    });
    Test("Required COM interfaces and IUnknown identity", [&] {
        Host h(factory); Com<IAudioSystemEffects> effects; Com<IUnknown> identity, other;
        Check(SUCCEEDED(h.apo->QueryInterface(__uuidof(IAudioSystemEffects), reinterpret_cast<void**>(effects.Put()))), "system effects");
        h.apo->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(identity.Put()));
        h.rt->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(other.Put())); Check(identity.p == other.p, "COM identity");
        Com<IAudioProcessingObjectVBR> vbr;
        Check(h.apo->QueryInterface(__uuidof(IAudioProcessingObjectVBR), reinterpret_cast<void**>(vbr.Put())) == E_NOINTERFACE, "VBR must not be exposed");
    });
    Test("Aggregation and unknown class are rejected", [&] {
        void* p = nullptr; Check(factory->CreateInstance(factory, __uuidof(IAudioProcessingObject), &p) == CLASS_E_NOAGGREGATION && !p, "aggregation");
    });
    Test("Initialize enforces lifecycle and class identity", [&] {
        Host h(factory, false); Check(h.Lock() == APOERR_NOT_INITIALIZED, "uninitialized lock");
        APOInitBaseStruct bad{ sizeof(bad), {} };
        Check(h.apo->Initialize(sizeof(bad), reinterpret_cast<BYTE*>(&bad)) == APOERR_INVALID_APO_CLSID, "wrong CLSID");
        Check(SUCCEEDED(h.apo->Initialize(0, nullptr)) && h.apo->Initialize(0, nullptr) == APOERR_ALREADY_INITIALIZED, "initialization lifecycle");
    });
    Test("Registration metadata describes real in-place float APO", [&] {
        Host h(factory); APO_REG_PROPERTIES* p = nullptr; Check(SUCCEEDED(h.apo->GetRegistrationProperties(&p)) && p, "registration");
        Check(p->clsid == CLSID_MicStudioApo && p->u32NumAPOInterfaces == 4 && p->u32MinInputConnections == 1 &&
            p->u32MaxOutputConnections == 1 && (p->Flags & APO_FLAG_INPLACE), "registration content"); CoTaskMemFree(p);
        HNSTIME latency = -1; Check(SUCCEEDED(h.apo->GetLatency(&latency)) && latency == 0, "zero algorithm latency");
    });
    Test("Format negotiation accepts matching float32 and rejects PCM", [&] {
        Host h(factory); Com<IAudioMediaType> floatType, pcm, supported, stereo;
        floatType.p = new Media(48000, 1); pcm.p = new Media(48000, 1, false); stereo.p = new Media(48000, 2);
        Check(SUCCEEDED(h.apo->IsInputFormatSupported(nullptr, floatType.p, supported.Put())) && supported.p == floatType.p, "float accepted");
        Check(h.apo->IsInputFormatSupported(nullptr, pcm.p, supported.Put()) == APOERR_FORMAT_NOT_SUPPORTED && !supported.p, "PCM rejected");
        Check(h.apo->IsOutputFormatSupported(stereo.p, floatType.p, supported.Put()) == APOERR_FORMAT_NOT_SUPPORTED, "channel mismatch");
    });
    Test("Locking limits formats channels and connection counts", [&] {
        Host h(factory); Check(h.config->LockForProcess(0, nullptr, 1, nullptr) == APOERR_NUM_CONNECTIONS_INVALID, "connection count");
        Check(h.Lock(48000, 9) == APOERR_INVALID_CONNECTION_FORMAT && h.Lock(4000, 1) == APOERR_INVALID_CONNECTION_FORMAT, "format bounds");
        Check(SUCCEEDED(h.Lock()) && h.Lock() == APOERR_APO_LOCKED, "double lock");
        Check(SUCCEEDED(h.config->UnlockForProcess()) && h.config->UnlockForProcess() == APOERR_ALREADY_UNLOCKED, "unlock lifecycle");
    });
    Test("Default passthrough preserves exact float bits", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock");
        uint32_t bits[] = { 0x80000000u, 0x3f000000u, 0x7fc01234u, 0x7f800000u, 0xbf800000u };
        float input[5], output[5]{}; std::memcpy(input, bits, sizeof(bits)); h.Process(input, output, 5);
        Check(std::memcmp(input, output, sizeof(input)) == 0, "passthrough bits");
    });
    Test("Silent buffers do not read an absent input buffer", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); float output[32]; std::fill_n(output, 32, 123.f);
        h.Process(nullptr, output, 32, BUFFER_SILENT); Check(std::all_of(output, output + 32, [](float x) { return x == 0; }), "silence");
    });
    Test("Invalid buffer metadata cannot overrun the output", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock(48000, 1, 16)), "lock"); float input[32]{}, output[32]; std::fill_n(output, 32, 123.f);
        APO_CONNECTION_PROPERTY a{ reinterpret_cast<UINT_PTR>(input), 32, BUFFER_VALID, APO_CONNECTION_PROPERTY_SIGNATURE };
        APO_CONNECTION_PROPERTY b{ reinterpret_cast<UINT_PTR>(output), 0, BUFFER_INVALID, APO_CONNECTION_PROPERTY_SIGNATURE };
        auto ap = &a, bp = &b; h.rt->APOProcess(1, &ap, 1, &bp);
        Check(b.u32BufferFlags == BUFFER_INVALID && b.u32ValidFrameCount == 0 && output[0] == 123, "oversized buffer");
    });
    Test("In-place buffers and aliased connection properties are supported", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.TestMode = 1; h.Parameters(p);
        float values[3] = { .1f, -.1f, .2f };
        APO_CONNECTION_PROPERTY property{ reinterpret_cast<UINT_PTR>(values), 3, BUFFER_VALID, APO_CONNECTION_PROPERTY_SIGNATURE };
        auto v = &property; h.rt->APOProcess(1, &v, 1, &v);
        Check(property.u32ValidFrameCount == 3 && std::abs(values[0] - .1 * micstudio::Linear(-6)) < 1e-6, "aliased in-place");
    });
    Test("Fixed -6 dB mode changes actual APO output", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.TestMode = 1; h.Parameters(p);
        Check(std::abs(Measure(h, Tone()) + 6) < .005, "fixed attenuation");
    });
    Test("Input and output gains update in the same locked stream", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.InputGainDb = -3; p.OutputGainDb = -3; h.Parameters(p);
        Check(std::abs(Measure(h, Tone()) + 6) < .005, "combined gains");
        p.InputGainDb = -6; h.Parameters(p); Check(std::abs(Measure(h, Tone()) + 9) < .005, "live gain");
        MicStudioApoStatus status{ sizeof(status) }; Check(SUCCEEDED(h.control->GetStatus(&status)) && status.Locked && status.ProcessedFrames == 96000, "same stream");
    });
    Test("Peak EQ and master off produce measured center response", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.EqEnabled = 1;
        for (auto& b : p.Bands) b.Enabled = 0;
        p.Bands[4].Enabled = 1; p.Bands[4].FrequencyHz = 1000; p.Bands[4].GainDb = 6; h.Parameters(p);
        Check(std::abs(Measure(h, Tone()) - 6) < .02, "peak center gain");
        p.EqEnabled = 0; p.InputGainDb = -6; h.Parameters(p); Check(std::abs(Measure(h, Tone()) + 6) < .02, "EQ master off retains gain");
    });
    Test("Limiter contains finite extreme input and sanitizes nonfinite samples", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.InputGainDb = 30; p.LimiterEnabled = 1; h.Parameters(p);
        float input[] = { 1, -1, std::numeric_limits<float>::max(), std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity() }, output[5]{};
        h.Process(input, output, 5); for (auto v : output) Check(std::isfinite(v) && std::abs(v) <= micstudio::Linear(-1) + 1e-6, "limiter finite bound");
    });
    Test("Invalid parameter updates are rejected before publication", [&] {
        Host h(factory); auto p = Flat(); p.Version = 99; Check(h.control->SetParameters(&p) == E_INVALIDARG, "version");
        p = Flat(); p.InputGainDb = std::numeric_limits<float>::quiet_NaN(); Check(h.control->SetParameters(&p) == E_INVALIDARG, "nonfinite");
        p = Flat(); p.Sequence = 1; Check(h.control->SetParameters(&p) == E_INVALIDARG, "odd sequence");
        p = Flat(); p.SizeBytes = 208; Check(h.control->SetParameters(&p) == E_INVALIDARG, "old ABI");
    });
    Test("Expired and future controller leases fall back to passthrough", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.TestMode = 1;
        p.HeartbeatTickMs = GetTickCount64() > 4000 ? GetTickCount64() - 4000 : 0;
        Check(SUCCEEDED(h.control->SetParameters(&p)), "stale snapshot can be published");
        Check(std::abs(Measure(h, Tone())) < .005, "stale lease passthrough");
        p.HeartbeatTickMs = GetTickCount64() + 10000; Check(SUCCEEDED(h.control->SetParameters(&p)), "future lease");
        Check(std::abs(Measure(h, Tone())) < .005, "future lease passthrough");
    });
    Test("Heartbeat updates retain filter history and disabled state is exact", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.TestMode = 1; h.Parameters(p);
        Check(std::abs(Measure(h, Tone()) + 6) < .005, "enabled test");
        p.Enabled = 0; h.Parameters(p); Check(std::abs(Measure(h, Tone())) < .005, "disabled exact");
    });
    Test("Reset while locked clears history without changing the connections", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); auto p = Flat(); p.InputGainDb = -6; h.Parameters(p);
        Measure(h, Tone()); Check(SUCCEEDED(h.apo->Reset()), "reset"); Check(std::abs(Measure(h, Tone()) + 6) < .005, "reset output");
    });
    Test("Every supported channel count and low rate processes finite samples", [&] {
        for (uint32_t channels : { 1u, 2u, 8u }) for (uint32_t rate : { 8000u, 48000u, 384000u })
        {
            Host h(factory); Check(SUCCEEDED(h.Lock(rate, channels, 64)), "format lock"); auto p = MicStudioDefaultParams(); p.Enabled = 1; h.Parameters(p);
            std::vector<float> input(64 * channels, .1f), output(input.size()); h.Process(input.data(), output.data(), 64);
            Check(std::all_of(output.begin(), output.end(), [](float v) { return std::isfinite(v); }), "finite supported format");
        }
    });
    Test("Concurrent nonRT updates never tear an RT snapshot", [&] {
        Host h(factory); Check(SUCCEEDED(h.Lock()), "lock"); std::atomic<bool> done{ false };
        std::thread writer([&] { auto p = Flat(); for (int i = 0; i < 500; ++i) { p.InputGainDb = i % 2 ? -6.f : 0.f; h.Parameters(p); } done = true; });
        float input[64], output[64]; std::fill_n(input, 64, .1f);
        while (!done.load()) { h.Process(input, output, 64); for (auto value : output) Check(std::isfinite(value) && std::abs(value) <= .101f, "torn snapshot"); }
        writer.join();
    });
    Test("Actual file-backed endpoint parameters update without relocking", [&] {
        MappingSandbox file; Host h(factory, false); file.Initialize(h); Check(SUCCEEDED(h.Lock()), "mapped lock");
        auto p = Flat(); p.TestMode = 1; file.Publish(p); Check(std::abs(Measure(h, Tone()) + 6) < .005, "mapped gain");
        p.TestMode = 0; p.InputGainDb = -3; file.Publish(p); Check(std::abs(Measure(h, Tone()) + 3) < .005, "mapped live update");
        MicStudioApoStatus s{ sizeof(s) }; Check(SUCCEEDED(h.control->GetStatus(&s)) && s.MappedParameters && s.Locked && s.ProcessedFrames == 96000, "mapped status");
        Check(h.control->SetParameters(&p) == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED), "file source cannot be overridden through test control");
    });
    Test("Torn corrupt or expired mapped snapshots immediately pass through", [&] {
        MappingSandbox file; Host h(factory, false); file.Initialize(h); Check(SUCCEEDED(h.Lock()), "mapped lock");
        auto p = Flat(); p.TestMode = 1; file.Publish(p); Check(std::abs(Measure(h, Tone()) + 6) < .005, "mapped valid");
        file.Publish(p, true); Check(std::abs(Measure(h, Tone())) < .005, "odd snapshot passthrough");
        p.Version = 99; file.Publish(p); Check(std::abs(Measure(h, Tone())) < .005, "corrupt snapshot passthrough");
        p = Flat(); p.TestMode = 1; p.HeartbeatTickMs = 0; file.Publish(p); Check(std::abs(Measure(h, Tone())) < .005, "expired snapshot passthrough");
    });
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) { std::fprintf(stderr, "NativeTests requires an explicit APO DLL path.\n"); return 2; }
    const auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 2;
    HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) { std::fprintf(stderr, "DLL load failed: %lu\n", GetLastError()); CoUninitialize(); return 1; }
    int result = 0;
    try
    {
        auto getClass = reinterpret_cast<MicStudioGetClassObject>(GetProcAddress(module, "DllGetClassObject"));
        auto canUnload = reinterpret_cast<HRESULT(WINAPI*)()>(GetProcAddress(module, "DllCanUnloadNow"));
        Check(getClass && canUnload && canUnload() == S_OK, "COM exports and initial unload");
        {
            Com<IClassFactory> factory;
            Check(SUCCEEDED(getClass(CLSID_MicStudioApo, __uuidof(IClassFactory), reinterpret_cast<void**>(factory.Put()))), "class factory");
            Check(canUnload() == S_FALSE, "factory keeps DLL loaded"); Run(factory.p);
        }
        Check(canUnload() == S_OK, "all COM references released");
        std::printf("Native APO offline checks: %d passed. No registration, endpoint changes or audio capture.\n", passed);
    }
    catch (const std::exception& ex) { std::fprintf(stderr, "FAIL %s\n", ex.what()); result = 1; }
    FreeLibrary(module); CoUninitialize(); return result;
}
