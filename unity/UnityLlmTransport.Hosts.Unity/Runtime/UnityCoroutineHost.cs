using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public sealed class UnityCoroutineHost : MonoBehaviour, IUnityWebRequestRunner
    {
        private readonly UnityMainThreadDispatcher _dispatcher = new UnityMainThreadDispatcher();
        private readonly UnityLifecycleToken _lifecycle = new UnityLifecycleToken();

        public CancellationToken ShutdownToken
        {
            get { return _lifecycle.Token; }
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            _dispatcher.Drain();
        }

        private void OnDestroy()
        {
            _lifecycle.Dispose();
        }

        private void OnApplicationQuit()
        {
            _lifecycle.Dispose();
        }

        public void InvalidateLifecycle()
        {
            _lifecycle.Invalidate();
        }

        public void Post(Action action)
        {
            _dispatcher.Post(action);
        }

        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
        {
            if (func == null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => completion.TrySetCanceled(ct));
            completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

            _dispatcher.Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    completion.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    completion.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

            return completion.Task;
        }

        public Task<UwrResponseSnapshot> StartAsync(UnityWebRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var completion = new TaskCompletionSource<UwrResponseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() => StartCoroutine(RunRequest(request, ct, completion)));
            return completion.Task;
        }

        public Task<UwrResponseSnapshot> WaitForCompletionAsync(UnityWebRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var completion = new TaskCompletionSource<UwrResponseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() => StartCoroutine(WaitForCompletion(request, completion)));
            return completion.Task;
        }

        private IEnumerator WaitForCompletion(UnityWebRequest request, TaskCompletionSource<UwrResponseSnapshot> completion)
        {
            while (true)
            {
                bool isDone;
                try
                {
                    isDone = request.isDone;
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new UwrResponseSnapshot(UwrCompletion.Aborted, 0, new TransportHeaders(), ex.Message));
                    yield break;
                }
                if (isDone)
                {
                    break;
                }
                yield return null;
            }

            UwrResponseSnapshot snapshot;
            try
            {
                snapshot = BuildSnapshot(request, false);
            }
            catch (Exception ex)
            {
                snapshot = new UwrResponseSnapshot(UwrCompletion.Aborted, 0, new TransportHeaders(), ex.Message);
            }
            completion.TrySetResult(snapshot);
        }

        private IEnumerator RunRequest(
            UnityWebRequest request,
            CancellationToken ct,
            TaskCompletionSource<UwrResponseSnapshot> completion)
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetResult(new UwrResponseSnapshot(
                    UwrCompletion.Aborted,
                    0,
                    new TransportHeaders(),
                    "Cancelled before start."));
                yield break;
            }

            int cancelled = 0;
            CancellationTokenRegistration registration = default;
            if (ct.CanBeCanceled)
            {
                registration = ct.Register(() =>
                {
                    Interlocked.Exchange(ref cancelled, 1);
                    _dispatcher.Post(() => request.Abort());
                });
            }

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            IStreamingDownloadHandler handler = request.downloadHandler as IStreamingDownloadHandler;

            while (!operation.isDone)
            {
                if (handler != null && handler.FirstDataReceived && request.responseCode != 0)
                {
                    break;
                }
                yield return null;
            }

            var snapshot = BuildSnapshot(request, Volatile.Read(ref cancelled) != 0);
            registration.Dispose();
            completion.TrySetResult(snapshot);
        }

        private static UwrResponseSnapshot BuildSnapshot(UnityWebRequest request, bool cancelled)
        {
            var completion = cancelled ? UwrCompletion.Aborted : MapResult(request.result);
            var headers = new TransportHeaders();
            try
            {
                var values = request.GetResponseHeaders();
                if (values != null)
                {
                    foreach (var pair in values)
                    {
                        headers[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception)
            {
                // 响应头尚未可用时容忍为空；U2 门禁负责验证首字节时 headers 可用。
            }

            return new UwrResponseSnapshot(completion, (int)request.responseCode, headers, request.error);
        }

        private static UwrCompletion MapResult(UnityWebRequest.Result result)
        {
            switch (result)
            {
                case UnityWebRequest.Result.InProgress:
                    return UwrCompletion.InProgress;
                case UnityWebRequest.Result.Success:
                    return UwrCompletion.Success;
                case UnityWebRequest.Result.ConnectionError:
                    return UwrCompletion.ConnectionError;
                case UnityWebRequest.Result.ProtocolError:
                    return UwrCompletion.ProtocolError;
                case UnityWebRequest.Result.DataProcessingError:
                    return UwrCompletion.DataProcessingError;
                default:
                    return UwrCompletion.ProtocolError;
            }
        }
    }
}
