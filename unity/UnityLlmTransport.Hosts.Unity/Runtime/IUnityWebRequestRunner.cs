using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public interface IUnityWebRequestRunner
    {
        /// <summary>任意线程调用；把 func 编组到 Unity 主线程并等待结果。</summary>
        Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct);

        /// <summary>任意线程调用；在主线程启动 UWR，首字节/提前失败/取消时完成快照。</summary>
        Task<UwrResponseSnapshot> StartAsync(UnityWebRequest request, CancellationToken ct);

        /// <summary>任意线程调用；请求真正结束时完成快照（会话返回后继续观察用）。</summary>
        Task<UwrResponseSnapshot> WaitForCompletionAsync(UnityWebRequest request);

        /// <summary>fire-and-forget 主线程投递（Abort/Dispose 用）。</summary>
        void Post(Action action);
    }
}
