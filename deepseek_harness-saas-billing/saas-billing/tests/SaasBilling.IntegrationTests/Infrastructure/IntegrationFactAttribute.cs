using System.Runtime.CompilerServices;
using Xunit;

namespace SaasBilling.IntegrationTests.Infrastructure;

/// <summary>Fact that is skipped when neither Docker nor BILLING_TEST_CONNECTION is available.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestEnvironment.IsAvailable)
        {
            Skip = TestEnvironment.SkipReason;
        }
    }
}
