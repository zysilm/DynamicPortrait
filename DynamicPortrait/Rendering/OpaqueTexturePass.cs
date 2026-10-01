// SPDX-License-Identifier: AGPL-3.0-or-later
using Silk.NET.Direct3D11;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicPortrait.Rendering;

// The game's scene alpha is not an ImGui opacity mask. Convert a private copy;
// never change the scene resource. Restore CS state and SRV bindings that D3D11
// may automatically unbind when an older portrait becomes a UAV destination.
internal sealed unsafe class OpaqueTexturePass : IDisposable
{
    private ID3D11ComputeShader* shader;

    public void Run(ID3D11Device* device, ID3D11DeviceContext* context,
        ID3D11ShaderResourceView* input, ID3D11UnorderedAccessView* output, int width, int height)
    {
        if (shader == null)
        {
            var bytes = Encoding.UTF8.GetBytes("""
                Texture2D<float4> Source : register(t0);
                RWTexture2D<float4> Destination : register(u0);
                [numthreads(8,8,1)] void main(uint3 p : SV_DispatchThreadID) {
                    uint w, h; Destination.GetDimensions(w,h);
                    if (p.x < w && p.y < h) Destination[p.xy] = float4(Source.Load(int3(p.xy,0)).rgb, 1);
                }
                """);
            var hr = D3DCompile(bytes, (nuint)bytes.Length, "portrait-opaque", 0, 0, "main", "cs_5_0", 0, 0, out var code, out var errors);
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                var data = ((delegate* unmanaged<nint, void*>)(*(void***)code)[3])(code);
                var size = ((delegate* unmanaged<nint, nuint>)(*(void***)code)[4])(code);
                ID3D11ComputeShader* created = null;
                Marshal.ThrowExceptionForHR(device->CreateComputeShader(data, size, null, &created));
                shader = created;
            }
            finally
            {
                if (errors != 0) Marshal.Release(errors);
                if (code != 0) Marshal.Release(code);
            }
        }
        ID3D11ComputeShader* oldShader = null;
        var savedResources = stackalloc ID3D11ShaderResourceView*[6 * 128];
        ID3D11UnorderedAccessView* oldOutput = null;
        var instances = stackalloc ID3D11ClassInstance*[256];
        uint count = 256;
        context->CSGetShader(&oldShader, instances, &count);
        for (var stage = 0; stage < 6; stage++) Resources(context, stage, savedResources + stage * 128, false);
        context->CSGetUnorderedAccessViews(0, 1, &oldOutput);
        uint preserveCounter = uint.MaxValue;
        try
        {
            context->CSSetShader(shader, null, 0);
            context->CSSetUnorderedAccessViews(0, 1, &output, &preserveCounter);
            context->CSSetShaderResources(0, 1, &input);
            context->Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
        }
        finally
        {
            ID3D11ShaderResourceView* emptyInput = null;
            ID3D11UnorderedAccessView* emptyOutput = null;
            context->CSSetShaderResources(0, 1, &emptyInput);
            context->CSSetUnorderedAccessViews(0, 1, &emptyOutput, &preserveCounter);
            context->CSSetUnorderedAccessViews(0, 1, &oldOutput, &preserveCounter);
            for (var stage = 0; stage < 6; stage++) Resources(context, stage, savedResources + stage * 128, true);
            context->CSSetShader(oldShader, instances, count);
            if (oldShader != null) oldShader->Release();
            for (var i = 0; i < 6 * 128; i++) if (savedResources[i] != null) savedResources[i]->Release();
            if (oldOutput != null) oldOutput->Release();
            for (var i = 0; i < count; i++) if (instances[i] != null) instances[i]->Release();
        }
    }

    public void Dispose() { if (shader != null) { shader->Release(); shader = null; } }

    private static void Resources(ID3D11DeviceContext* context, int stage, ID3D11ShaderResourceView** views, bool restore)
    {
        switch (stage)
        {
            case 0: if (restore) context->VSSetShaderResources(0, 128, views); else context->VSGetShaderResources(0, 128, views); break;
            case 1: if (restore) context->PSSetShaderResources(0, 128, views); else context->PSGetShaderResources(0, 128, views); break;
            case 2: if (restore) context->GSSetShaderResources(0, 128, views); else context->GSGetShaderResources(0, 128, views); break;
            case 3: if (restore) context->HSSetShaderResources(0, 128, views); else context->HSGetShaderResources(0, 128, views); break;
            case 4: if (restore) context->DSSetShaderResources(0, 128, views); else context->DSGetShaderResources(0, 128, views); break;
            case 5: if (restore) context->CSSetShaderResources(0, 128, views); else context->CSGetShaderResources(0, 128, views); break;
        }
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] data, nuint size, string sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out nint code, out nint errors);
}
