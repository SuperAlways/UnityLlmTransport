using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace UnityLlmTransport
{
    internal sealed class UnityMainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public void Post(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
            _queue.Enqueue(action);
        }

        public void Drain()
        {
            Action action;
            while (_queue.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}
