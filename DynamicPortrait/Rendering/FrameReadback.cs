// SPDX-License-Identifier: AGPL-3.0-or-later
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicPortrait.Rendering;

// Explicit investigation only: staging readback can stall the GPU. Compression
// and file I/O use a worker after all native pointers have been released.
internal static unsafe class FrameReadback
{
    public static void Export(ID3D11Texture2D* source, ID3D11DeviceContext* context, string path)
    {
        ID3D11Device* device = null;
        ID3D11Texture2D* staging = null;
        source->GetDevice(&device);
        Texture2DDesc desc;
        source->GetDesc(&desc);
        if (desc.Format is not (Format.FormatR8G8B8A8Unorm or Format.FormatB8G8R8A8Unorm))
        { if (device != null) device->Release(); throw new NotSupportedException("PNG investigation requires an RGBA8/BGRA8 buffer"); }
        byte[] pixels;
        try
        {
            desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
            desc.MiscFlags = 0;
            Marshal.ThrowExceptionForHR(device->CreateTexture2D(&desc, null, &staging));
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)source);
            MappedSubresource mapped;
            Marshal.ThrowExceptionForHR(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped));
            try
            {
                pixels = new byte[checked((int)(desc.Width * desc.Height * 4))];
                for (var y = 0; y < desc.Height; y++)
                    Marshal.Copy((nint)((byte*)mapped.PData + y * mapped.RowPitch), pixels, (int)(y * desc.Width * 4), (int)desc.Width * 4);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (desc.Format == Format.FormatB8G8R8A8Unorm) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    pixels[i + 3] = 255;
                }
            }
            finally { context->Unmap((ID3D11Resource*)staging, 0); }
        }
        finally { if (staging != null) staging->Release(); if (device != null) device->Release(); }
        var width = (int)desc.Width; var height = (int)desc.Height;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { WritePng(path, width, height, pixels); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError($"Frame export failed: {e}"); }
        });
    }

    private static void WritePng(string path, int width, int height, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(file, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgba, y * width * 4, width * 4); }
        Chunk(file, "IDAT", compressed.ToArray()); Chunk(file, "IEND", []);
    }
    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
        var name = Encoding.ASCII.GetBytes(type); file.Write(name); file.Write(data);
        var crc = 0xffffffffu;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
    }
}
