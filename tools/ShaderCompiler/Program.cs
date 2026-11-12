// Compiles an HLSL pixel shader to ps_3_0 bytecode for WPF ShaderEffect, using the D3D compiler
// that ships with Windows (no Windows SDK needed).
// Usage: dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Aurora.hlsl src/TuneSync.App/Effects/Aurora.ps
using System.Runtime.InteropServices;
using System.Text;

var source = File.ReadAllText(args[0]);
var bytes = Encoding.UTF8.GetBytes(source);
var hr = D3D.D3DCompile(bytes, (nint)bytes.Length, Path.GetFileName(args[0]), 0, 0, "main", "ps_3_0", 1 << 15, 0, out var code, out var errors);
if (errors is not null) Console.WriteLine(Marshal.PtrToStringAnsi(errors.GetBufferPointer()));
if (hr < 0 || code is null) { Console.WriteLine($"FAILED 0x{hr:X8}"); return 1; }
var data = new byte[(int)code.GetBufferSize()];
Marshal.Copy(code.GetBufferPointer(), data, 0, data.Length);
File.WriteAllBytes(args[1], data);
Console.WriteLine($"wrote {args[1]} ({data.Length} bytes)");
return 0;

static class D3D
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    public static extern int D3DCompile(byte[] srcData, nint srcDataSize, string sourceName, nint defines, nint include,
        string entryPoint, string target, int flags1, int flags2, out ID3DBlob? code, out ID3DBlob? errors);
}

[ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ID3DBlob
{
    [PreserveSig] nint GetBufferPointer();
    [PreserveSig] nint GetBufferSize();
}
