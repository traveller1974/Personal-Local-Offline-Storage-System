using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Stock.Core;

namespace Stock.Desktop;

public static class QwenSettings
{
    public const string EmbeddedRecognitionSwitch="LocalStockManager.UseEmbeddedRecognition";
    public static bool UsesEmbeddedRecognition=>AppContext.TryGetSwitch(EmbeddedRecognitionSwitch,out var enabled)&&enabled;
    private static readonly HttpClient Client=new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
    private static string PathFor(StockService service)=>Path.Combine(Path.GetDirectoryName(service.DataDirectory)!,"Settings","qwen.dpapi");
    public static QwenConfiguration Load(StockService service)
    {
        var path=PathFor(service);if(!File.Exists(path))return new("",QwenConfiguration.Beijing);
        try{return JsonSerializer.Deserialize<QwenConfiguration>(Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path),false)))??throw new BusinessException("百炼设置无效。");}
        catch(Exception ex) when(ex is not BusinessException){throw new BusinessException("百炼设置无法由当前 Windows 用户解密，请重新填写。");}
    }
    public static void Save(StockService service,QwenConfiguration configuration)
    {
        configuration.Validate();var path=PathFor(service);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".tmp";
        try{File.WriteAllBytes(temp,Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(configuration)),true));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static IRecognitionService Service(StockService service)=>CreateService(Load(service));
    internal static IRecognitionService CreateService(QwenConfiguration configuration)
    {
        var original=new QwenRecognitionService(Client,configuration);
        return UsesEmbeddedRecognition?new EmbeddedRecognitionService(configuration,original):original;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob{public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] bytes,bool encrypt)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            var ok=encrypt?CryptProtectData(ref input,"LocalStockManager Qwen",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new BusinessException("Windows 当前用户密钥加密 / 解密失败。");
            var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,output.Size);return result;
        }
        finally{Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}
    }
}
