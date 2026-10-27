using Xunit;

namespace Fworker.Domain.Tests;

public class ProjectTests
{
    [Fact]
    public void DomainSeesContractsThroughItsReference()
    {
        // the one reference Domain has: the seam interfaces of Contracts resolve from here
        Assert.True(typeof(Fworker.Contracts.Seams.IVerbRouter).IsInterface);
    }
}
