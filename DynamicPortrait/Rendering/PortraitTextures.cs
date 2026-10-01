// SPDX-License-Identifier: AGPL-3.0-or-later
// GPU capture approach adapted from FFXIV VR's Resources.cs and Renderer.CopyTexture.
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace DynamicPortrait.Rendering;

internal sealed unsafe class PortraitTextures : IDisposable
{
    private sealed class Slot : IDisposable
    {
        public ID3D11Texture2D* Texture;
        public ID3D11ShaderResourceView* View;
        public ID3D11Texture2D* OpaqueTexture;
        public ID3D11ShaderResourceView* OpaqueView;
        public ID3D11UnorderedAccessView* OpaqueOutput;
        public void Dispose()
        {
            if (OpaqueOutput != null) { OpaqueOutput->Release(); OpaqueOutput = null; }
            if (OpaqueView != null) { OpaqueView->Release(); OpaqueView = null; }
            if (OpaqueTexture != null) { OpaqueTexture->Release(); OpaqueTexture = null; }
            if (View != null) { View->Release(); View = null; }
            if (Texture != null) { Texture->Release(); Texture = null; }
        }
    }

    private readonly object gate = new();
    private Slot[] slots = [];
    private readonly List<(long Frame, Slot[] Slots)> retired = [];
    private int next;
    private int width, height;
    private Format format;
    private nint deviceIdentity;
    private long presents;
    private nint front;
    private readonly OpaqueTexturePass opaque = new();
    private bool probeRequested = true;
    public string ProbeResult { get; private set; } = "pending";
    public void RequestProbe() { lock (gate) probeRequested = true; }
    public void ClearPublished() { lock (gate) front = 0; }
    public long Copies { get; private set; }

    public (nint Handle, int Width, int Height) Read()
    {
        lock (gate) return (front, width, height);
    }

    // Host-independent D3D11 implementation, also executed by the offline WARP tests.
    public void Capture(ID3D11Texture2D* source, ID3D11DeviceContext* context, int requestedWidth, int requestedHeight)
    {
        ID3D11Device* device = null;
        if (source == null || context == null) throw new InvalidOperationException("D3D11 device unavailable");
        source->GetDevice(&device);
        if (device == null) throw new InvalidOperationException("Texture device unavailable");
        try
        {
            Texture2DDesc desc;
            source->GetDesc(&desc);
            if (desc.SampleDesc.Count != 1 || desc.ArraySize != 1)
                throw new NotSupportedException("Multisampled/array scene textures are not supported by this backend");
            if (requestedWidth <= 0 || requestedHeight <= 0 || requestedWidth > desc.Width || requestedHeight > desc.Height)
                throw new InvalidOperationException("Render resolution changed during portrait pass; disable dynamic resolution and retry");
            var viewFormat = desc.Format switch
            {
                Format.FormatB8G8R8A8Typeless => Format.FormatB8G8R8A8Unorm,
                Format.FormatR8G8B8A8Typeless => Format.FormatR8G8B8A8Unorm,
                Format.FormatB8G8R8A8Unorm or Format.FormatR8G8B8A8Unorm or Format.FormatR16G16B16A16Float => desc.Format,
                _ => throw new NotSupportedException($"Unsupported scene texture format: {desc.Format}"),
            };
            lock (gate)
            {
                if (slots.Length == 0 || width != requestedWidth || height != requestedHeight || format != desc.Format || deviceIdentity != (nint)device)
                {
                    var replacement = new Slot[3];
                    try
                    {
                        for (var i = 0; i < replacement.Length; i++)
                        {
                            replacement[i] = new Slot();
                            var td = new Texture2DDesc(width: (uint)requestedWidth, height: (uint)requestedHeight,
                                mipLevels: 1, arraySize: 1, format: desc.Format,
                                sampleDesc: new SampleDesc(1, 0), usage: Usage.Default,
                                bindFlags: (uint)BindFlag.ShaderResource);
                            ID3D11Texture2D* texture = null;
                            Check(device->CreateTexture2D(&td, null, &texture), "CreateTexture2D");
                            replacement[i].Texture = texture;
                            var vd = new ShaderResourceViewDesc(format: viewFormat,
                                viewDimension: Silk.NET.Core.Native.D3DSrvDimension.D3DSrvDimensionTexture2D,
                                texture2D: new Tex2DSrv(0, 1));
                            ID3D11ShaderResourceView* view = null;
                            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, &vd, &view), "CreateShaderResourceView");
                            replacement[i].View = view;
                            td.Format = Format.FormatR8G8B8A8Unorm;
                            td.BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.UnorderedAccess);
                            texture = null;
                            Check(device->CreateTexture2D(&td, null, &texture), "Create opaque texture");
                            replacement[i].OpaqueTexture = texture;
                            view = null;
                            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "Create opaque SRV");
                            replacement[i].OpaqueView = view;
                            ID3D11UnorderedAccessView* output = null;
                            Check(device->CreateUnorderedAccessView((ID3D11Resource*)texture, null, &output), "Create opaque UAV");
                            replacement[i].OpaqueOutput = output;
                        }
                    }
                    catch
                    {
                        foreach (var slot in replacement) slot?.Dispose();
                        throw;
                    }
                    if (slots.Length != 0) retired.Add((presents, slots));
                    slots = replacement;
                    width = requestedWidth;
                    height = requestedHeight;
                    format = desc.Format;
                    if (deviceIdentity != (nint)device) opaque.Dispose();
                    deviceIdentity = (nint)device;
                    next = 0;
                }
                var left = (desc.Width - (uint)width) / 2;
                var top = (desc.Height - (uint)height) / 2;
                var box = new Box(left, top, 0, left + (uint)width, top + (uint)height, 1);
                var destination = slots[next];
                context->CopySubresourceRegion((ID3D11Resource*)destination.Texture, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
                if (probeRequested)
                {
                    probeRequested = false;
                    try { ProbeResult = Probe(device, context, destination.Texture); }
                    catch (Exception e) { ProbeResult = $"probe failed: {e.Message}"; }
                }
                opaque.Run(device, context, destination.View, destination.OpaqueOutput, width, height);
                front = (nint)destination.OpaqueView;
                next = (next + 1) % slots.Length;
                Copies++;
            }
        }
        finally { device->Release(); }
    }

    // Called after a normal Present: previous ImGui draw data has been consumed.
    public void OnPresented()
    {
        lock (gate)
        {
            presents++;
            for (var i = retired.Count - 1; i >= 0; i--)
                if (presents - retired[i].Frame >= 4)
                {
                    foreach (var slot in retired[i].Slots) slot.Dispose();
                    retired.RemoveAt(i);
                }
        }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"{operation}: HRESULT 0x{hr:X8}");
    }

    // One readback per explicit start, sampled from the actual cropped scene.
    private static string Probe(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* source)
    {
        Texture2DDesc desc;
        source->GetDesc(&desc);
        desc.Usage = Usage.Staging;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ID3D11Texture2D* staging = null;
        Check(device->CreateTexture2D(&desc, null, &staging), "Probe texture");
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)source);
            MappedSubresource mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped), "Probe map");
            try
            {
                float minAlpha = float.PositiveInfinity, maxAlpha = float.NegativeInfinity, maxRgb = 0;
                for (uint y = 0; y < desc.Height; y += Math.Max(1u, desc.Height / 16))
                    for (uint x = 0; x < desc.Width; x += Math.Max(1u, desc.Width / 16))
                    {
                        var pixel = (byte*)mapped.PData + y * mapped.RowPitch;
                        var fp16 = desc.Format == Format.FormatR16G16B16A16Float;
                        pixel += x * (fp16 ? 8 : 4);
                        var a = fp16 ? (float)((Half*)pixel)[3] : pixel[3] / 255f;
                        minAlpha = Math.Min(minAlpha, a); maxAlpha = Math.Max(maxAlpha, a);
                        for (var c = 0; c < 3; c++) maxRgb = Math.Max(maxRgb, fp16 ? (float)((Half*)pixel)[c] : pixel[c] / 255f);
                    }
                return $"{desc.Width}x{desc.Height} {desc.Format}, sampled alpha={minAlpha:F3}..{maxAlpha:F3}, maxRGB={maxRgb:F3}";
            }
            finally { context->Unmap((ID3D11Resource*)staging, 0); }
        }
        finally { staging->Release(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            front = 0;
            foreach (var slot in slots) slot.Dispose();
            foreach (var item in retired) foreach (var slot in item.Slots) slot.Dispose();
            slots = [];
            retired.Clear();
            opaque.Dispose();
        }
    }
}
