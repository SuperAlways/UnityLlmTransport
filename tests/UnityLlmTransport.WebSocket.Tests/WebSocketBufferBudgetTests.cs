using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class WebSocketBufferBudgetTests
    {
        [Fact]
        public void Null_Max_Is_Unlimited()
        {
            var budget = new WebSocketBufferBudget(null);
            Assert.True(budget.Add(int.MaxValue));
        }

        [Fact]
        public void Accumulates_Until_Reset()
        {
            var budget = new WebSocketBufferBudget(10);
            Assert.True(budget.Add(4));
            Assert.True(budget.Add(6));
            Assert.False(budget.Add(1));

            budget.Reset();
            Assert.True(budget.Add(10));
        }

        [Fact]
        public void Exceed_Returns_False_And_Keeps_Count()
        {
            var budget = new WebSocketBufferBudget(5);
            Assert.True(budget.Add(4));
            Assert.False(budget.Add(2));
            Assert.False(budget.Add(0));
            budget.Reset();
            Assert.True(budget.Add(5));
        }

        [Fact]
        public void Negative_Count_Throws()
        {
            var budget = new WebSocketBufferBudget(5);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => budget.Add(-1));
        }
    }
}
