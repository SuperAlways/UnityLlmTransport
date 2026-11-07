using System;

namespace UnityLlmTransport
{
    internal sealed class WebSocketBufferBudget
    {
        private readonly int? _maxBytes;
        private int _currentBytes;

        public WebSocketBufferBudget(int? maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public bool Add(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            _currentBytes += count;
            return _maxBytes == null || _currentBytes <= _maxBytes.Value;
        }

        public void Reset()
        {
            _currentBytes = 0;
        }
    }
}
