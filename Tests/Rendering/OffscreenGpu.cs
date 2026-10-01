// SPDX-License-Identifier: AGPL-3.0-or-later
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

internal sealed unsafe class OffscreenGpu : IDisposable
{
    // Silk 2.21's headless overload is marked obsolete, but the suggested overload is
    // absent in this package version. No native window or swapchain is used here.
#pragma warning disable CS0618
    private readonly D3D11 api = D3D11.GetApi();
#pragma warning restore CS0618
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11Texture2D* scene;
    private ID3D11Texture2D* depth;
    private ID3D11RenderTargetView* targetView;
    private ID3D11DepthStencilView* depthView;
    private ID3D11DepthStencilState* depthState;
    private ID3D11RasterizerState* rasterizer;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11Buffer* constants;

    public ID3D11Texture2D* Scene => scene;
    public ID3D11DeviceContext* Context => context;
    public ID3D11Device* Device => device;

    public void ClearTransparent()
    {
        var color = stackalloc float[] { 0.25f, 0.5f, 0.75f, 0 };
        context->ClearRenderTargetView(targetView, color);
    }

    public OffscreenGpu()
    {
        ID3D11Device* createdDevice = null;
        ID3D11DeviceContext* createdContext = null;
        D3DFeatureLevel feature;
        Check(api.CreateDevice(null, D3DDriverType.Warp, 0, (uint)CreateDeviceFlag.BgraSupport,
            null, 0, D3D11.SdkVersion, &createdDevice, &feature, &createdContext), "D3D11CreateDevice(WARP)");
        device = createdDevice;
        context = createdContext;
        Console.WriteLine($"Real D3D11 device: WARP, feature level {feature}");
        try
        {
            var desc = new Texture2DDesc(width: 640, height: 480, mipLevels: 1, arraySize: 1,
                format: Format.FormatR8G8B8A8Unorm, sampleDesc: new SampleDesc(1, 0), usage: Usage.Default,
                bindFlags: (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource));
            ID3D11Texture2D* texture = null;
            Check(device->CreateTexture2D(&desc, null, &texture), "Scene texture");
            scene = texture;
            ID3D11RenderTargetView* rtv = null;
            Check(device->CreateRenderTargetView((ID3D11Resource*)scene, null, &rtv), "Scene RTV");
            targetView = rtv;
            desc.Format = Format.FormatD32Float;
            desc.BindFlags = (uint)BindFlag.DepthStencil;
            texture = null;
            Check(device->CreateTexture2D(&desc, null, &texture), "Depth texture");
            depth = texture;
            ID3D11DepthStencilView* dsv = null;
            Check(device->CreateDepthStencilView((ID3D11Resource*)depth, null, &dsv), "Depth view");
            depthView = dsv;
            var dd = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.GreaterEqual };
            ID3D11DepthStencilState* ds = null;
            Check(device->CreateDepthStencilState(&dd, &ds), "Reverse-Z depth state");
            depthState = ds;
            var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* rs = null;
            Check(device->CreateRasterizerState(&rd, &rs), "Rasterizer");
            rasterizer = rs;
            var bd = new BufferDesc(byteWidth: 64, usage: Usage.Default, bindFlags: (uint)BindFlag.ConstantBuffer);
            ID3D11Buffer* buffer = null;
            Check(device->CreateBuffer(&bd, null, &buffer), "Camera constants");
            constants = buffer;
            using var vs = new ShaderBlob("VS", "vs_5_0");
            using var ps = new ShaderBlob("PS", "ps_5_0");
            ID3D11VertexShader* vertex = null;
            ID3D11PixelShader* pixel = null;
            Check(device->CreateVertexShader(vs.Data, vs.Size, null, &vertex), "Vertex shader");
            vertexShader = vertex;
            Check(device->CreatePixelShader(ps.Data, ps.Size, null, &pixel), "Pixel shader");
            pixelShader = pixel;
        }
        catch { Dispose(); throw; }
    }

    public void Draw(Matrix4x4 viewProjection)
    {
        var rtv = targetView;
        context->OMSetRenderTargets(1, &rtv, depthView);
        context->OMSetDepthStencilState(depthState, 0);
        context->RSSetState(rasterizer);
        var viewport = new Viewport(0, 0, 640, 480, 0, 1);
        context->RSSetViewports(1, &viewport);
        var clear = stackalloc float[] { 0.04f, 0.07f, 0.12f, 1 };
        context->ClearRenderTargetView(targetView, clear);
        context->ClearDepthStencilView(depthView, (uint)ClearFlag.Depth, 0, 0);
        context->UpdateSubresource((ID3D11Resource*)constants, 0, null, &viewProjection, 0, 0);
        context->VSSetShader(vertexShader, null, 0);
        context->PSSetShader(pixelShader, null, 0);
        var buffer = constants;
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        context->Draw(36, 0);
    }

    public byte[] ReadView(nint handle)
    {
        ID3D11Resource* resource = null;
        ((ID3D11ShaderResourceView*)handle)->GetResource(&resource);
        try { return Read((ID3D11Texture2D*)resource); }
        finally { resource->Release(); }
    }

    public byte[] Read(ID3D11Texture2D* texture)
    {
        Texture2DDesc desc;
        texture->GetDesc(&desc);
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.Usage = Usage.Staging;
        desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ID3D11Texture2D* staging = null;
        Check(device->CreateTexture2D(&desc, null, &staging), "Readback staging");
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
            MappedSubresource mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped), "Map readback");
            try
            {
                var data = new byte[desc.Width * desc.Height * 4];
                for (var y = 0; y < desc.Height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.PData + y * mapped.RowPitch, (int)desc.Width * 4)
                        .CopyTo(data.AsSpan(y * (int)desc.Width * 4));
                return data;
            }
            finally { context->Unmap((ID3D11Resource*)staging, 0); }
        }
        finally { staging->Release(); }
    }

    public void AssertNoDeviceError()
    {
        Check(device->GetDeviceRemovedReason(), "Device remains healthy");
        Console.WriteLine("PASS D3D11 device remains healthy after captures and resize");
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"{operation}: HRESULT 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (context != null) { context->ClearState(); context->Flush(); }
        Release(constants); constants = null;
        Release(vertexShader); vertexShader = null;
        Release(pixelShader); pixelShader = null;
        Release(rasterizer); rasterizer = null;
        Release(depthState); depthState = null;
        Release(depthView); depthView = null;
        Release(targetView); targetView = null;
        Release(depth); depth = null;
        Release(scene); scene = null;
        Release(context); context = null;
        Release(device); device = null;
        api.Dispose();
    }

    private static void Release(void* obj)
    {
        if (obj != null) ((delegate* unmanaged<void*, uint>)(*(void***)obj)[2])(obj);
    }

    private sealed class ShaderBlob : IDisposable
    {
        private nint blob;
        public void* Data => ((delegate* unmanaged<nint, void*>)(*(void***)blob)[3])(blob);
        public nuint Size => ((delegate* unmanaged<nint, nuint>)(*(void***)blob)[4])(blob);

        public ShaderBlob(string entry, string profile)
        {
            var source = Encoding.UTF8.GetBytes(Shader);
            var hr = D3DCompile(source, (nuint)source.Length, "offline-cube.hlsl", 0, 0, entry, profile, 0, 0, out blob, out var errors);
            try
            {
                if (hr < 0)
                {
                    var message = errors == 0 ? "" : Marshal.PtrToStringAnsi((nint)((delegate* unmanaged<nint, void*>)(*(void***)errors)[3])(errors));
                    throw new Exception($"D3DCompile {entry}: {message}");
                }
            }
            finally { Release((void*)errors); }
        }
        public void Dispose() { Release((void*)blob); blob = 0; }
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] data, nuint size, string sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out nint code, out nint errors);

    private const string Shader = """
        cbuffer Camera : register(b0) { row_major float4x4 ViewProjection; };
        struct Output { float4 Position : SV_Position; float3 Color : COLOR0; };
        Output VS(uint id : SV_VertexID) {
            const float3 p[8] = {
                float3(-.3,-.4,-.3), float3(-.3,.4,-.3), float3(.3,.4,-.3), float3(.3,-.4,-.3),
                float3(-.3,-.4,.3), float3(-.3,.4,.3), float3(.3,.4,.3), float3(.3,-.4,.3)
            };
            const uint indices[36] = { 0,1,2,0,2,3, 4,6,5,4,7,6, 0,4,5,0,5,1, 3,2,6,3,6,7, 1,5,6,1,6,2, 0,3,7,0,7,4 };
            const float3 colors[6] = { float3(.3,.2,.8), float3(1,.55,.2), float3(.2,.8,.5), float3(.1,.6,1), float3(1,.85,.35), float3(.7,.2,.4) };
            Output o;
            o.Position = mul(float4(p[indices[id]], 1), ViewProjection);
            o.Color = colors[id/6] * (.8 + .2 * p[indices[id]].y);
            return o;
        }
        float4 PS(Output input) : SV_Target { return float4(input.Color, 1); }
        """;
}
